using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;

namespace QuazalWV
{
    public static class Global
    {
        public static readonly string keyDATA = "CD&ML";
        public static readonly string keyCheckSum = "8dtRv2oj";
        public static string serverBindAddress = "127.0.0.1";
        public static uint idCounter = 0x12345678;
        public static uint pidCounter = 0x1234;
        public static uint dummyFriendPidCounter = 0x1235;
        public static string sessionURL = "prudp:/address=127.0.0.1;port=21032;RVCID=4660";
        // Player spawn transform (world coords) written into the entity-create replica
        // (MSG_ID_Net_Obj_Create, msg 0x271). The schema/offset is verified against the game's
        // cObjectManager::SerializeOneEntity (RE/plan/03-spawn-replica-schema.md): col3 of the 4x4
        // matrix = translation. (0,0,0) is the world origin and is almost never a valid spawn point,
        // which is why the player appears at the origin. Set these to a real in-bounds coordinate for
        // the loaded map (extract from the map's zen::SpawnZone data in Yeti.big via GROExplorerWV).
        // Fallback spawn transform if a real one can't be read from Yeti.big (see below).
        public static float spawnX = 0f;
        public static float spawnY = 0f;
        public static float spawnZ = 0f;
        // Path to the game's Yeti.big. The dedicated server reads real per-map spawn-zone
        // coordinates from it at runtime (YetiBigSpawnReader) so players spawn in-bounds.
        // Point this at the Yeti.big of the game install the clients use.
        public static string yetiBigPath = @"D:\Phoenix\GRO\GRO\PDC-Live-WV\Yeti.big";
        public static List<ClientInfo> clients = new List<ClientInfo>();
        public static Stopwatch uptime = new Stopwatch();

        public static ClientInfo GetClientByEndPoint(IPEndPoint ep)
        {
            foreach (ClientInfo c in clients)
                if (c.ep.Address.ToString() == ep.Address.ToString() && c.ep.Port == ep.Port)
                    return c;
            WriteLog(1, "Error : Cant find client for end point : " + ep.ToString());
            return null;
        }

        public static ClientInfo GetClientByIDsend(uint id)
        {
            foreach (ClientInfo c in clients)
                if (c.IDsend == id)
                    return c;
            WriteLog(1, "Error : Cant find client for id : 0x" + id.ToString("X8"));
            return null;
        }

        public static ClientInfo GetClientByIDrecv(uint id)
        {
            foreach (ClientInfo c in clients)
                if (c.IDrecv == id)
                    return c;
            WriteLog(1, "Error : Cant find client for id : 0x" + id.ToString("X8"));
            return null;
        }

        private static void WriteLog(int priority, string s)
        {
            Log.WriteLine(priority, "[Global] " + s);
        }
    }
}
