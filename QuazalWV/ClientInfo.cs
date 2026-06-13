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
}
