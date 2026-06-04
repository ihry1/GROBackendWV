using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace QuazalWV
{
    public static class Log
    {
        public static RichTextBox box = null;
        public static int MinPriority = 10; //1..10 1=less, 10=all
        public static string logFileName = "log.txt";
        public static string logPacketsFileName = "packetLog.bin";
        public static readonly object _sync = new object();
        public static readonly object _filesync = new object();
        public static StringBuilder logBuffer = new StringBuilder();
        public static List<byte[]> logPackets = new List<byte[]>();
        public static bool enablePacketLogging = true;
        static int _uiPending = 0;                  // bounds the cross-thread UI append queue under flood (drop, don't block/OOM)
        static volatile bool _saverStarted = false; // ONE persistent disk-saver thread (was spawned per-line -> thread exhaustion crash)

        public static void ClearLog()
        {
            if (File.Exists(logFileName))
                File.Delete(logFileName);
            if (File.Exists(logPacketsFileName))
                File.Delete(logPacketsFileName);
            lock (_sync)
            {
                logBuffer = new StringBuilder();
                logPackets = new List<byte[]>();
            }
        }

        public static void WriteLine(int priority, string s, object color = null)
        {
            string stamp = DateTime.Now.ToShortDateString() + " " + DateTime.Now.ToLongTimeString() + " : [" + priority.ToString("D2") + "]";
            // Always buffer to the file log (lock-bounded, drained by the ONE persistent saver thread).
            // Doing this off the UI thread means heavy "Log Packets" traffic can't stall or crash the UI.
            lock (_sync)
                logBuffer.Append(stamp + s + "\n");
            EnsureSaver();

            // UI append: priority-filtered, NON-blocking (BeginInvoke), flood-bounded, length-capped.
            RichTextBox b = box;
            if (b == null || priority > MinPriority)
                return;
            // Bound the pending cross-thread queue: under flood, drop UI updates (still file-logged) instead
            // of blocking the caller (old box.Invoke) or letting the BeginInvoke queue grow unbounded -> OOM.
            if (Interlocked.Increment(ref _uiPending) > 256)
            {
                Interlocked.Decrement(ref _uiPending);
                return;
            }
            Color c = (color != null) ? (Color)color : (s.ToLower().Contains("error") ? Color.Red : Color.Black);
            try
            {
                b.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        if (b.TextLength > 200000)   // cap the RichTextBox so it can't grow unbounded
                            b.Clear();
                        b.SelectionStart = b.TextLength;
                        b.SelectionLength = 0;
                        b.SelectionColor = c;
                        b.AppendText(stamp + s + "\n");
                        b.SelectionColor = b.ForeColor;
                        b.ScrollToCaret();
                    }
                    catch { }
                    finally { Interlocked.Decrement(ref _uiPending); }
                }));
            }
            catch { Interlocked.Decrement(ref _uiPending); }
        }

        // Start the single background disk-saver exactly once (replaces the per-line thread spawn).
        static void EnsureSaver()
        {
            if (_saverStarted)
                return;
            lock (_sync)
            {
                if (_saverStarted)
                    return;
                _saverStarted = true;
                Thread t = new Thread(tSaveLog);
                t.IsBackground = true;
                t.Start();
            }
        }

        public static string MakeDetailedPacketLog(byte[] data, bool isSinglePacket = false)
        {
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                QPacket qp = new QPacket(data);
                sb.AppendLine("##########################################################");
                sb.AppendLine(qp.ToStringDetailed());
                if (qp.type == QPacket.PACKETTYPE.DATA && qp.m_byPartNumber == 0)
                {
                    switch (qp.m_oSourceVPort.type)
                    {
                        case QPacket.STREAMTYPE.OldRVSec:
                            if (qp.flags.Contains(QPacket.PACKETFLAG.FLAG_ACK))
                                break;
                            sb.AppendLine("Trying to process RMC packet...");
                            try
                            {
                                MemoryStream m = new MemoryStream(qp.payload);
                                RMCP p = new RMCP(qp);
                                m.Seek(p._afterProtocolOffset + 4, 0);
                                if (!p.isRequest)
                                    m.ReadByte();
                                p.methodID = Helper.ReadU32(m);
                                sb.AppendLine("\tRMC Request  : " + p.isRequest);
                                sb.AppendLine("\tRMC Protocol : " + p.proto);
                                sb.AppendLine("\tRMC Method   : " + p.methodID.ToString("X"));
                                if (p.proto == RMCP.PROTOCOL.GlobalNotificationEventProtocol && p.methodID == 1)
                                {
                                    sb.AppendLine("\t\tNotification :");
                                    sb.AppendLine("\t\t\tSource".PadRight(20) + ": 0x" + Helper.ReadU32(m).ToString("X8"));
                                    uint type = Helper.ReadU32(m);
                                    sb.AppendLine("\t\t\tType".PadRight(20) + ": " + (type / 1000));
                                    sb.AppendLine("\t\t\tSubType".PadRight(20) + ": " + (type % 1000));
                                    sb.AppendLine("\t\t\tParam 1".PadRight(20) + ": 0x" + Helper.ReadU32(m).ToString("X8"));
                                    sb.AppendLine("\t\t\tParam 2".PadRight(20) + ": 0x" + Helper.ReadU32(m).ToString("X8"));
                                    sb.AppendLine("\t\t\tParam String".PadRight(20) + ": " + Helper.ReadString(m));
                                    sb.AppendLine("\t\t\tParam 3".PadRight(20) + ": 0x" + Helper.ReadU32(m).ToString("X8"));
                                }
                                sb.AppendLine();
                            }
                            catch
                            {
                                sb.AppendLine("Error processing RMC packet");
                                sb.AppendLine();
                            }
                            break;
                        case QPacket.STREAMTYPE.DO:
                            if (qp.flags.Contains(QPacket.PACKETFLAG.FLAG_ACK))
                                break;
                            sb.AppendLine("Trying to unpack DO messages...");
                            try
                            {
                                MemoryStream m = new MemoryStream(qp.payload);
                                uint size = Helper.ReadU32(m);
                                byte[] buff = new byte[size];
                                m.Read(buff, 0, (int)size);
                                DO.UnpackMessage(buff, 1, sb);
                                sb.AppendLine();
                            }
                            catch
                            {
                                sb.AppendLine("Error processing DO messages");
                                sb.AppendLine();
                            }
                            break;
                    }
                }
                int size2 = qp.toBuffer().Length;
                if (size2 == data.Length || isSinglePacket)
                    break;
                MemoryStream m2 = new MemoryStream(data);
                m2.Seek(size2, 0);
                size2 = (int)(m2.Length - m2.Position);
                if (size2 <= 8)
                    break;
                data = new byte[size2];
                m2.Write(data, 0, size2);
            }
            return sb.ToString();
        }

        public static void LogPacket(bool sent, byte[] data)
        {
            if (!enablePacketLogging)
                return;
            MemoryStream m = new MemoryStream();
            m.WriteByte(1);//version
            m.WriteByte((byte)(sent ? 1 : 0));
            Helper.WriteU32(m, (uint)data.Length);
            m.Write(data, 0, data.Length);
            lock (_sync)
            {
                logPackets.Add(m.ToArray());
            }
        }

        // Single persistent background saver: drains ALL pending log text + ALL pending packets each pass,
        // then sleeps. Replaces the old one-shot-per-line spawn (which created a thread per log line and
        // drained only one packet at a time -> thread exhaustion + backlog under "Log Packets").
        public static void tSaveLog(object obj)
        {
            while (true)
            {
                try
                {
                    lock (_filesync)
                    {
                        string buffer = null;
                        lock (_sync)
                        {
                            buffer = logBuffer.ToString();
                            logBuffer.Clear();
                        }
                        if (buffer != null && buffer.Length > 0)
                            File.AppendAllText(logFileName, buffer);

                        List<byte[]> pkts = null;
                        lock (_sync)
                        {
                            if (logPackets.Count != 0)
                            {
                                pkts = logPackets;
                                logPackets = new List<byte[]>();
                            }
                        }
                        if (pkts != null && pkts.Count > 0)
                        {
                            using (FileStream fs = new FileStream(logPacketsFileName, FileMode.Append, FileAccess.Write))
                            {
                                foreach (byte[] packet in pkts)
                                    fs.Write(packet, 0, packet.Length);
                                fs.Flush();
                            }
                        }
                    }
                }
                catch { }
                Thread.Sleep(50);
            }
        }
    }
}
