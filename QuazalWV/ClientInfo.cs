using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    public class ClientInfo
    {
        public uint PID;
        public uint sPID;
        public ushort sPort;
        public uint IDrecv;
        public uint IDsend;
        public byte sessionID;
        public byte[] sessionKey;
        public ushort seqCounter;
        /// <summary>
        /// Reliable substream sequence ID.
        /// </summary>
        public ushort seqCounterReliable = 1;
        // PRUDP reliable-fragment retransmit tracker (server->client). Each large RMC response is
        // fragmented + sent FLAG_RELIABLE; PRUDP makes the SENDER resend any fragment the client does
        // not ACK. We hold each un-ACKed fragment here (keyed by its reliable seqId) until the ACK
        // arrives, and a background thread resends timed-out ones. See ReliableRetransmit.
        public readonly Dictionary<ushort, PendingReliablePacket> pendingReliable = new Dictionary<ushort, PendingReliablePacket>();
        public readonly object pendingReliableLock = new object();
        public ushort seqCounterDO;
        public ushort callCounterDO_RMC;
        public uint callCounterRMC;
        public uint stationID;
        // 2-player identity: this client's pawn-owner net id, derived from its DO station
        // (0x05C00000 | stationID). A client masters a pawn iff the pawn's owner == its own station
        // (gro-pawn-master-slave), so each client's OWN pawn must carry ITS station, not a hardcoded
        // 0x5c00002. Station is assigned once per client in Global.EnsureStation (2, 3, ...).
        public uint pawnOwner { get { return 0x05C00000u | stationID; } }
        // M2 peer replication: distinct entity handles per client so two pawns coexist in each client's view.
        // slot 0 (station 2) -> abstract 1 / concrete 2; slot 1 (station 3) -> 3 / 4; etc.
        public uint pawnSlot { get { return stationID >= 2 ? stationID - 2 : 0; } }
        public uint pawnAbstractHandle { get { return 1 + pawnSlot * 2; } }
        public uint pawnConcreteHandle { get { return 2 + pawnSlot * 2; } }
        // PvP team — delegates to the PID-keyed Global.GetMatchTeam roster so the pawn's team (this value, set on
        // the DS) and the client's "my team" perspective (FetchSessionParticipants, same DS process) ALWAYS agree.
        // Stamped on the abstract+concrete create-blobs (teamID): peers render as ENEMIES (red, hitmarker), and
        // team-aware Yeti.big spawn puts the teams at opposite ends. (Was slot-based; the backend perspective could
        // not match a station-derived team -> the team-2 player saw its own pawn red. Both sides now key on the PID.)
        public byte team { get { return Global.GetMatchTeam(PID); } }
        // This client's spawned-pawn create blobs (replicated to OTHER clients on their in-match messages),
        // its spawn position, and which peer stations it has already been shown.
        public bool pawnSpawned = false;
        public OCP_AbstractPlayerEntity pawnAbstractEntity = null;
        public OCP_PlayerEntity pawnConcreteEntity = null;
        public float pawnX, pawnY, pawnZ;
        // Per-viewer peer handle roster: peer stationID -> handle SLOT (0,1,2..), allocated on first replication.
        // Each peer renders at abstract (3+slot*2) / concrete (4+slot*2) on THIS client, so N players get unique
        // non-colliding handles (self is always 1/2). Replaces the HashSet that hardcoded every peer to 3/4 ->
        // a 3rd client collided -> "duplicate entity handle 0x3". The dict KEYS double as the old "already shown" set.
        public readonly Dictionary<uint, uint> peerHandleSlot = new Dictionary<uint, uint>();
        // Per-viewer: the lifeGen of each peer's pawn copy this client currently holds. When a peer's lifeGen
        // advances (it respawned), AppendPeerPawns destroys the old (dead) concrete copy + recreates a fresh one.
        public readonly Dictionary<uint, int> peerLifeGen = new Dictionary<uint, int>();
        public uint GetOrAllocPeerSlot(uint peerStation)
        {
            uint slot;
            if (!peerHandleSlot.TryGetValue(peerStation, out slot))
            {
                // lowest FREE slot index (not Count) so a disconnected peer's freed slot is reused without
                // colliding with a higher slot still in use (AppendPeerDestroys removes the leaver's entry).
                slot = 0;
                while (peerHandleSlot.ContainsValue(slot)) slot++;
                peerHandleSlot[peerStation] = slot;
            }
            return slot;
        }
        // Discrete action-cmds (ability 0x0E/0x0F, gesture 0x28/0x29) this client emitted, queued to fan out to
        // PEERS. Each peer pulls them on its next outgoing bundle (BM_Message.AppendPeerActionCmds), re-targeted to
        // that viewer's slave handle for this player + master/server bits cleared (slave-accept, gate v2=0). Self-
        // cleans as each pending station consumes its copy. (Was the relay-to-self bug: sent back to the sender.)
        public readonly List<PendingPeerCmd> pendingPeerCmds = new List<PendingPeerCmd>();
        // Server-authoritative combat HP (peer-vs-peer damage). The DS is the authority: hits from other players
        // decrement hp; an UpdateHealth(handle 2, isServer=true) is queued on pendingSelfCmds + delivered on this
        // client's OWN next bundle (it masters its own pawn so it accepts the server cmd). hp<=0 -> dead -> timed
        // respawn. pendingSelfCmds = entity-cmds for THIS client's own pawn (handle as built, NOT re-targeted).
        public DateTime lastSeen = DateTime.UtcNow;   // last incoming BM traffic (0x99 etc.); Global.SweepIdleMatchClients reaps DEPLOYED clients gone silent (disconnect/crash) -> AppendPeerDestroys clears their ghost
        public float firePosX, firePosY, firePosZ;   // last position from this client's hit-cmd SOURCE Vec3; 3+ player hit resolution picks the enemy closest to a shot's impact point
        public bool hasFirePos = false;
        public float hp = 100f;
        public bool dead = false;
        public DateTime deathTime = DateTime.MinValue;
        public int lifeGen = 0;   // incremented on each respawn; peers destroy+recreate this player's pawn when it advances (proper respawn vs revive-in-place)
        public bool ownRespawnPending = false;   // CheckRespawn sets it on respawn; AppendSelfRespawn destroys+recreates THIS player's OWN pawn (handle 2) at the fresh spawn point (so its 0x99 restarts there)
        public readonly List<byte[]> pendingSelfCmds = new List<byte[]>();
        public byte[] lastReplicaPayload = null;   // M3: this client's latest 0x99 movement replica, relayed (re-targeted) to peers so they move
        public string name;
        public string pass;
        public IPEndPoint ep;
        public UdpClient udp;
        public bool bootStrapDone = false;
        public bool matchStartSent = false;
        public bool playerCreateStuffSent1 = false;
        public bool playerCreateStuffSent2 = false;
        // Match-init handshake: after RequestSpawn(0x34) we hold the abstract at state 3 (Spawning) and wait
        // for the client's ClientReady(0x35) before setting params bit 0x2000 + ChangeState(5). Without this
        // the deploy/combat-input gate (bIsClientReady) never opened -> no move/ADS/fire.
        public bool clientReadyHandled = false;
        // Post-spawn gesture: the pawn's anim-banks async-load over ~1-2s after the create, so a locomotion
        // Gesture cmd sent immediately would no-op. Count post-spawn BM messages and fire it once the model
        // has had time to load (fixes A-pose + movement; handled in BM_Message.HandleMessage).
        public int postSpawnTicks = 0;
        public bool gestureSent = false;
        public int replicaLogCount = 0;  // cycle-1 diag: cap on logging the client's incremental 0x99 replica updates
        // 4 = LoopAdversarial. The client's per-frame fire/input gate
        // (AI_EntityPlayerAbstract::Spawn @ AICLASS 0x100d8c20) only calls IncInputValue
        // (enables combat/fire-focus input) when cNetRulesManager::eStateID == 4
        // (bIsInLoopAdversarialState @ 0x10035720); bCanSpawn @ 0x10036390 also requires 4.
        // This value is sent verbatim as Synchronize(668) param1 (newState) in reply to the
        // client's AskForSynchronize(163). At 3 (SpawnAdversarial) the client can move (local
        // TryWalk is ungated) but never un-gates firing. 4 -> client ChangeState(4)+StartGame.
        public byte netRulesState = 4;
        public byte playerAbstractState = 2;
        public Payload_PlayerParameter settings = new Payload_PlayerParameter(new byte[0x40]);
        public int newsMsgId = -1;
        public List<GR5_NewsMessage> systemNews = new List<GR5_NewsMessage>();
        public List<GR5_NewsMessage> personaNews = new List<GR5_NewsMessage>();
        public List<GR5_NewsMessage> friendNews = new List<GR5_NewsMessage>();

        public void ClearSystemNews()
        {
            systemNews = new List<GR5_NewsMessage>();
        }

        public void ClearPersonaNews()
        {
            personaNews = new List<GR5_NewsMessage>();
        }

        public void ClearFriendNews()
        {
            friendNews = new List<GR5_NewsMessage>();
        }
    }

    // A discrete entity-cmd a player emitted (raw 0x96 payload), pending fan-out to peers. needStations = the peer
    // stations that still need it relayed (removed as each consumes); the cmd is dropped once the set is empty.
    public class PendingPeerCmd
    {
        public byte[] raw;
        public HashSet<uint> needStations;
    }
}
