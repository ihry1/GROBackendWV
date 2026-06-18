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
        // TEST AID: force ALL players to spawn at ONE shared point (the first spawner's captured point) so they
        // land together for combat testing instead of at opposite team zones. Set false to restore team-aware spawns.
        public static bool forceSharedSpawn = false;
        public static bool sharedSpawnSet = false;
        public static float sharedSpawnX, sharedSpawnY, sharedSpawnZ;
        // ---- Server-authoritative combat (WIP). A DS-spawned "fake enemy" dummy player so the single
        // connected client has an opponent to shoot: M1 = render it, M2 = damage/death/respawn. The DS owns
        // it (owner 0x5c00003 = a remote station, so the client renders it as a SLAVE, not itself), on team 2
        // (enemy) at handle 3. Set enableFakeEnemy=false to revert to clean single-player.
        public static bool enableFakeEnemy = false;  // OFF for real 2-player PvP — a 2nd real client is the opponent now (was the single-client M2 dummy)
        public static uint fakeEnemyHandle = 3;          // concrete pawn handle
        public static uint fakeEnemyAbstractHandle = 4;  // abstract player-object handle (must exist before the pawn)
        public static uint fakeEnemyOwner = 0x5c00003;   // remote station 3 -> client renders it as a slave; the pawn finds its abstract by this station
        public static float fakeEnemyHP = 100f;          // server-tracked current HP (M2)
        public static float fakeEnemyMaxHP = 100f;       // HP restored on each (re)spawn / life
        public static float fakeEnemyHitDamage = 34f;    // M2 placeholder damage per confirmed hit (~3 hits to down); real per-weapon damage TODO once weapon stats apply in-match
        public static int fakeEnemyKills = 0;            // M2 server-authoritative down count this session (diag)
        public static bool fakeEnemyDead = false;        // M2-iter2: enemy is in its death state, awaiting respawn
        public static DateTime fakeEnemyDeathTime = DateTime.MinValue;  // when it died (drives the respawn timer)
        public static float fakeEnemyRespawnDelay = 3f;  // seconds dead before the server auto-revives it
        // Real peer-vs-peer server-authoritative combat (replaces the fake-enemy dummy). Per-client HP/dead live
        // on ClientInfo; these are the shared tunables. Placeholder flat damage (no per-weapon damage in the DB yet).
        public static float realHitDamage = 20f;     // damage per confirmed hit (~5 hits to down) -> visible incremental HP drop
        public static float headshotMultiplier = 1.5f;   // headshot (bodypart==0) damage x1.5 = the RETAIL value: PCSet_Weapon_Damage_Factor_Player_Head global (AICLASS .data 0x102e8848 = 1.5f), read by AI_EntityPawn::GetBodyPartMultiplier. Torso/arms/legs = 1.0. Per-armor CHPMT_HeadMultiplier_F can override but has no compiled default and the DB serves none -> 1.5 for everyone. (No per-weapon head mult exists: WCPT_HeadShot_Multiplier propID 653 is absent from skillmodifiers.)
        public static float realMaxHP = 100f;
        public static float realRespawnDelay = 5f;    // seconds dead before the server auto-revives a real player
        // Path to the game's Yeti.big. The dedicated server reads real per-map spawn-zone
        // coordinates from it at runtime (YetiBigSpawnReader) so players spawn in-bounds.
        // Point this at the Yeti.big of the game install the clients use.
        public static string yetiBigPath = @"D:\Phoenix\GRO\GRO\PDC-Live-WV\Yeti.big";
        public static List<ClientInfo> clients = new List<ClientInfo>();
        public static Stopwatch uptime = new Stopwatch();

        // 2-player: monotonic DO station allocator. The session host is station 1; each connecting client
        // gets a distinct station starting at 2, assigned ONCE the first time it enters the session (DO
        // GetParticipantsRequest / JoinRequest). Distinct stations are what let the shared DO_Session hold
        // both players and let each client master its OWN pawn (owner 0x05C00000 | station).
        public static uint stationCounter = 2;
        private static readonly object stationLock = new object();
        public static void EnsureStation(ClientInfo client)
        {
            if (client.stationID == 0)
                lock (stationLock)
                    if (client.stationID == 0)
                        client.stationID = stationCounter++;
        }

        // PvP team roster, keyed by the PERSISTENT pid so a player's team is STABLE across the (separate) backend
        // and DS processes, across respawns, and across rematches. Assigned in arrival order (1st distinct pid ->
        // team 1, 2nd -> team 2, alternating = balanced). Used by BOTH the pawn create-blob team (ClientInfo.team,
        // on the DS) AND the client's "my team" perspective (the in-match FetchSessionParticipants list, same DS
        // process, + the backend match-found notification). On the DS the pawn + participant list share THIS roster
        // (same process) so they ALWAYS agree -> fixes the "team-2 player sees its own pawn red" bug. The backend
        // notification keeps its own copy, consistent for the normal sequential client-launch order. (Not order-
        // robust if two clients race the very first lookup across the two processes; fine for manual launches.
        // Persists until server restart -> stable teams across rematches.)
        private static readonly object teamLock = new object();
        private static readonly Dictionary<uint, byte> matchTeams = new Dictionary<uint, byte>();
        public static byte GetMatchTeam(uint pid)
        {
            lock (teamLock)
            {
                byte t;
                if (!matchTeams.TryGetValue(pid, out t))
                {
                    t = (byte)((matchTeams.Count % 2) + 1);
                    matchTeams[pid] = t;
                    WriteLog(1, "[TEAM] pid " + pid + " -> team " + t + " (player #" + matchTeams.Count + ")");
                }
                return t;
            }
        }

        // Reap match clients that have gone silent (graceful DISCONNECT or crash): a DEPLOYED client streams the
        // 0x99 replica continuously, so >15s of no BM traffic = gone. Gated on clientReadyHandled so lobby/login
        // connections (which legitimately idle while loading) are NEVER swept -- that gate is what keeps login safe
        // (removing on the raw DISCONNECT packet broke it). Driven by the remaining clients' traffic
        // (BM_Message.HandleMessage); removing the entry here lets AppendPeerDestroys destroy its ghost pawns.
        public static void SweepIdleMatchClients()
        {
            DateTime now = DateTime.UtcNow;
            foreach (ClientInfo c in clients.ToArray())
                if (c.clientReadyHandled && (now - c.lastSeen).TotalSeconds > 15.0)
                {
                    clients.Remove(c);
                    WriteLog(1, "[DC] swept idle st" + c.stationID + " pid" + c.PID + " (silent " + (int)(now - c.lastSeen).TotalSeconds + "s; clients now " + clients.Count + ")");
                }
        }

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
