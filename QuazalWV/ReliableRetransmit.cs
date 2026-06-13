using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace QuazalWV
{
    /// <summary>One un-ACKed reliable fragment the server sent and is waiting for the client to ACK.</summary>
    public class PendingReliablePacket
    {
        public ushort seq;
        public byte[] wire;       // exact serialized bytes - resend identical (RC4 here is deterministic, but we store once anyway)
        public IPEndPoint ep;
        public UdpClient udp;
        public long lastSentMs;
        public int retries;
    }

    /// <summary>
    /// PRUDP reliable-fragment retransmit (server -> client).
    ///
    /// Large RMC responses (lobby data, weapon SkillModifiers, ...) are fragmented and sent with
    /// FLAG_RELIABLE. PRUDP makes the SENDER responsible for resending any reliable packet the peer
    /// does not ACK. The emulator previously fired the fragments once and dropped incoming ACKs on the
    /// floor (RMC.HandlePacket returned on FLAG_ACK), so a single lost UDP fragment stalled the client
    /// forever ("loading lobby..."; the line-423 "Cannot Find Weapon Modifier List" on a later weapon
    /// when a SkillModifiers fragment was lost).
    ///
    /// We track every reliable fragment by its reliable seqId and resend it until the client ACKs.
    /// Kill switch: a file named "_noretransmit_" next to the exe disables tracking (falls back to the
    /// old fire-and-forget). Verbose trace: a file "_retransdiag_" -> retransdiag.txt.
    /// </summary>
    public static class ReliableRetransmit
    {
        // RTO must exceed the worst-case in-thread send time of ONE response, so we don't resend a
        // fragment whose ACK is merely sitting in the OS receive buffer while the (synchronous) sender
        // is still pacing out later fragments of the same response. Current responses are ~26 frags x
        // 12ms pace = ~0.3s, so 600ms is safe. (When the sender is moved off the receive thread for the
        // much larger full-component data, this can drop to ~RTT.)
        public const int RtoMs = 600;
        public const int MaxRetries = 10;     // ~6s of recovery before giving up on a fragment
        public const int TickMs = 40;

        private static int _on = -1;
        private static int _diag = -1;
        private static volatile bool _started;
        private static readonly object _startLock = new object();
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static readonly object _diagLock = new object();

        public static long NowMs { get { return _clock.ElapsedMilliseconds; } }
        private static string PathOf(string f) { try { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, f); } catch { return f; } }
        public static bool Enabled { get { if (_on < 0) { try { _on = File.Exists(PathOf("_noretransmit_")) ? 0 : 1; } catch { _on = 1; } } return _on == 1; } }
        private static bool Diag { get { if (_diag < 0) { try { _diag = File.Exists(PathOf("_retransdiag_")) ? 1 : 0; } catch { _diag = 0; } } return _diag == 1; } }
        private static void DiagLog(string s)
        {
            if (!Diag) return;
            try { lock (_diagLock) File.AppendAllText(PathOf("retransdiag.txt"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + Environment.NewLine); } catch { }
        }

        /// <summary>Register a reliable fragment right after it goes out, so we resend it if un-ACKed.</summary>
        public static void Track(ClientInfo client, ushort seq, byte[] wire, UdpClient udp, IPEndPoint ep)
        {
            if (!Enabled || client == null || wire == null || udp == null || ep == null) return;
            EnsureStarted();
            PendingReliablePacket f = new PendingReliablePacket { seq = seq, wire = wire, udp = udp, ep = ep, lastSentMs = NowMs, retries = 0 };
            int n;
            lock (client.pendingReliableLock) { client.pendingReliable[seq] = f; n = client.pendingReliable.Count; }
            DiagLog("TRACK   seq=" + seq + " len=" + wire.Length + " pending=" + n);
        }

        /// <summary>
        /// An ACK arrived (its uiSeqId == the fragment's reliable seq); stop resending that fragment.
        ///
        /// SEQ-AMBIGUITY NOTE: this emulator numbers small responses by (requestSeq+1) and reliable
        /// fragments by a separate seqCounterReliable, and the client's ACKs carry only [ACK] (never
        /// [RELIABLE]), so an ACK's seq alone can't prove which substream it belongs to, and the two
        /// ranges overlap. In practice this is safe: (a) each client/server PRUDP connection is its own
        /// ClientInfo, so only this connection's ACKs reach this tracker; (b) large responses are sent
        /// as a burst while the (synchronous) receive thread is blocked, so NO small responses - and
        /// thus no colliding small-response ACKs - are emitted during the burst; the only residual case
        /// is a small-response ACK after the burst whose seq happens to equal a still-un-ACKed (i.e.
        /// DROPPED) fragment's seq, which would merely fail to recover that one fragment == today's
        /// no-retransmit behavior, never worse. If _retransdiag_ ever shows this biting, the proper fix
        /// is to unify the outgoing seq counter (one counter per connection for reliable + unreliable).
        /// </summary>
        public static void Ack(ClientInfo client, ushort seq)
        {
            if (client == null) return;
            bool removed; int n;
            lock (client.pendingReliableLock) { removed = client.pendingReliable.Remove(seq); n = client.pendingReliable.Count; }
            if (removed) DiagLog("ACK     seq=" + seq + " pending=" + n);
        }

        /// <summary>Drop all tracked fragments for a client (e.g. on disconnect).</summary>
        public static void Clear(ClientInfo client)
        {
            if (client == null) return;
            lock (client.pendingReliableLock) client.pendingReliable.Clear();
        }

        private static void EnsureStarted()
        {
            if (_started) return;
            lock (_startLock)
            {
                if (_started) return;
                Thread t = new Thread(Loop) { IsBackground = true, Name = "PRUDP-Retransmit" };
                t.Start();
                _started = true;
            }
        }

        private static void Loop()
        {
            while (true)
            {
                try { Tick(); } catch { }
                Thread.Sleep(TickMs);
            }
        }

        private static void Tick()
        {
            long now = NowMs;
            ClientInfo[] snapshot;
            try { snapshot = Global.clients.ToArray(); } catch { return; }   // tolerate a concurrent Add on the unlocked list; retry next tick
            foreach (ClientInfo c in snapshot)
            {
                if (c == null) continue;
                List<PendingReliablePacket> resend = null;
                List<ushort> drop = null;
                lock (c.pendingReliableLock)
                {
                    if (c.pendingReliable.Count == 0) continue;
                    foreach (KeyValuePair<ushort, PendingReliablePacket> kv in c.pendingReliable)
                    {
                        PendingReliablePacket f = kv.Value;
                        if (now - f.lastSentMs < RtoMs) continue;
                        if (f.retries >= MaxRetries) { if (drop == null) drop = new List<ushort>(); drop.Add(kv.Key); continue; }
                        if (resend == null) resend = new List<PendingReliablePacket>();
                        resend.Add(f);
                    }
                    if (drop != null) foreach (ushort k in drop) c.pendingReliable.Remove(k);
                }
                if (resend != null)
                    foreach (PendingReliablePacket f in resend)
                    {
                        try { f.udp.Send(f.wire, f.wire.Length, f.ep); } catch { }
                        f.lastSentMs = now;
                        f.retries++;
                        DiagLog("RESEND  seq=" + f.seq + " try=" + f.retries);
                    }
                if (drop != null) DiagLog("GIVEUP  " + drop.Count + " frag(s) after " + MaxRetries + " retries");
            }
        }
    }
}
