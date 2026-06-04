using System;
using System.IO;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net;
using System.Linq;
using System.Text;
using System.Threading;
using QuazalWV;

namespace GRODedicatedServerWV
{
    public static class UDPDedicatedServer
    {
        public static readonly object _sync = new object();
        public static bool _exit = false;
        public static ushort listenPort = 21032;
        private static UdpClient listener;

        public static void Start()
        {
            _exit = false;
            new Thread(tMainThread).Start();
            QuazalWV.Global.uptime.Restart();
        }

        public static void Stop()
        {
            lock (_sync)
            {
                _exit = true;
            }
            if (listener != null)
                listener.Close();
            QuazalWV.Global.uptime.Stop();
        }

        public static void tMainThread(object obj)
        {
            WriteLog(1, "Server started");
            listener = new UdpClient(listenPort);
            // Disable the Windows UDP "connection reset" behavior. After we send to a client that has quit,
            // the OS otherwise makes the NEXT Receive() throw SocketException 10054 (ConnectionReset) -- which
            // would storm the catch below every iteration (and the old per-line logger turned that into a
            // crash). SIO_UDP_CONNRESET (0x9800000C) suppresses it, so a client quitting before the server is
            // stopped no longer crashes/spins the dedicated server.
            try { listener.Client.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null); } catch { }
            IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                lock (_sync)
                {
                    if (_exit)
                        break;
                }
                try
                {
                    byte[] bytes = listener.Receive(ref ep);
                    ProcessPacket(bytes, ep);
                }
                catch (Exception ex)
                {
                    WriteLog(1, "Server exception: " + ex.Message);
                    Thread.Sleep(1);   // avoid a tight CPU/log spin if some exception recurs every iteration
                }
            }
            WriteLog(1, "Server stopped");
        }

        public static void ProcessPacket(byte[] data, IPEndPoint ep)
        {
            QPacketHandler.ProcessPacket("UDP Dedicated Server", data, ep, listener, 0,0, true);
        }

        private static void WriteLog(int priority, string s)
        {
            Log.WriteLine(priority, "[UDP Dedicated Server] " + s);
        }
    }
}
