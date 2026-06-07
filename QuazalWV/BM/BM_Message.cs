using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    public abstract class BM_Message
    {
        public byte msgType = 0xA;
        public ushort msgID;
        public List<BM_Param> paramList = new List<BM_Param>();
        public static int g_cap99 = 0;   // [walk-fix TEMP] 0x99 capture counter (cap to bound the brief disk I/O)
        public static byte[] Make(BM_Message msg)
        {
            MemoryStream m = new MemoryStream();
            Helper.WriteU8(m, msg.msgType);
            Helper.WriteU16LE(m, msg.msgID);
            byte[] buff;
            foreach (BM_Param p in msg.paramList)
                switch (p.type)
                {
                    case BM_Param.PARAM_TYPE.Integer:
                        Helper.WriteU8(m, 0);
                        Helper.WriteU32LE(m, (uint)(int)p.data);
                        break;
                    case BM_Param.PARAM_TYPE.Float:
                        Helper.WriteU8(m, 0);
                        Helper.WriteFloatLE(m, (float)p.data);
                        break;
                    case BM_Param.PARAM_TYPE.Buffer:
                        buff = (byte[])p.data;
                        Helper.WriteU8(m, 0x80);
                        Helper.WriteU16LE(m, (ushort)buff.Length);
                        m.Write(buff, 0, buff.Length);
                        break;
                }
            buff = m.ToArray();
            m = new MemoryStream();
            Helper.WriteU16(m, (ushort)(buff.Length + 2));
            Helper.WriteU16(m, (ushort)buff.Length);
            m.Write(buff, 0, buff.Length);
            Helper.WriteU32(m, 0);
            return m.ToArray();
        }

        public static byte[] HandleMessage(ClientInfo client, Stream s)
        {
            if (Helper.ReadU16(s) < 5 || Helper.ReadU16(s) < 3)
                return null;
            byte type = Helper.ReadU8(s);
            if (type != 0xA)
                return null;
            ushort msgID = (ushort)((Helper.ReadU8(s) << 8) | Helper.ReadU8(s));
            List<byte[]> msgs = new List<byte[]>();
            switch(msgID)
            {
                case 0x96:
                    return Entitiy_CMD.HandleMsg(client, s);
                case 0x99:
                    Helper.ReadU8(s);
                    ushort size = Helper.ReadU16LE(s);
                    byte[] payload = new byte[size];
                    s.Read(payload, 0, size);
                    // [0x99 CAPTURE -- walk-fix TEMP] recover the client's SendReplicaData wire envelope.
                    // File-based (NO UI writes), RAW payload (pre-retarget), gated on clientReadyHandled so it
                    // only runs post-spawn (can't lag the spawn handshake like the removed UI capture did), and
                    // capped at 400 lines. Delete this block + the g_cap99 field after the envelope is decoded.
                    if (client.clientReadyHandled && g_cap99 < 400)
                    {
                        g_cap99++;
                        try {
                            System.IO.File.AppendAllText(@"D:\Phoenix\GRO\GRO_0x99_capture.txt",
                                "#" + g_cap99 + " len=" + size + "  " + BitConverter.ToString(payload).Replace('-', ' ') + "\r\n");
                        } catch { }
                    }
                    // (Removed the cycle-1 [0x99] capture logging: those 80 BitConverter hex dumps + UI writes
                    //  flooded the DS UI thread early, so the DS fell seconds behind and lagged the spawn
                    //  handshake. The replica wire format was obtained from the binary instead.)
                    payload[0x11] = 0;
                    payload[0x12] = 0x27;
                    msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                        0x1006,
                        new DupObj(DupObjClass.Station, 1),
                        new DupObj(DupObjClass.NET_MessageBroker, 5),
                        (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                        Make(new MSG_ID_SendReplicaData(payload))
                        ));
                    break;
                case 0xA3:
                    if (!client.playerCreateStuffSent1)
                    {
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.NET_MessageBroker, 5),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                            Make(new MSG_ID_Net_Obj_Create(0x2C, 0x15, new OCP_AbstractPlayerEntity(1).MakePayload()))
                            ));
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.NET_MessageBroker, 5),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                            Make(new MSG_ID_Entity_Cmd(client, 0x33))
                            )); 
                        client.playerCreateStuffSent1 = true;
                    }
                    msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                        0x1006,
                        new DupObj(DupObjClass.Station, 1),
                        new DupObj(DupObjClass.NET_MessageBroker, 5),
                        (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                        Make(new MSG_ID_NetRule_Synchronize(client.netRulesState))
                        ));
                    // Put the client's match object into WARMUP (BM 901 -> AI_Match.state = 1), NOT Round
                    // (BM 900 -> state 2). In Warmup the spawn-wave id is a client-local time clock
                    // (GetSpawnWaveId = floor(elapsedStateTime / spawnWaveDuration[15s]) + 1), so the player's
                    // wave arrives in ~15s and IsWaitingForWave (AI_MatchServer::bCanPlayerAbstractSpawn) then
                    // stays FALSE -> the deploy/respawn screen (cInGameMenuManager) stops holding bBlockUserInput
                    // (+0x189) -> move/ADS/fire unlock, and cNetRulesManager::bCanSpawn (also needs eStateID==4,
                    // which Synchronize already sends) lets the player legitimately spawn. In Round the wave id
                    // comes from AI_MatchRound, which the emulated DS never ticks -> the wave never arrives ->
                    // input stays blocked and the force-spawned player is reset back to WaitForSpawn ~6s later.
                    // Warmup also leaves state 0 (where GetSpawnWaveDuration hard-returns 100.0 -> "RESPAWN 01:40").
                    msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                        0x1006,
                        new DupObj(DupObjClass.Station, 1),
                        new DupObj(DupObjClass.NET_MessageBroker, 5),
                        (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                        Make(new MSG_ID_BM_Warmup())
                        ));
                    break;
                case 0x266:
                    break;
                case 0x325:
                    // Pre-ready: keep the match in WARMUP (state 1) so the spawn-wave clock advances and the
                    // player can make its FIRST spawn (deploy screen clears). Once the client is ready/alive
                    // (ClientReady handled), switch to a real ROUND (BM 900 / state 2) and keep sending
                    // StartRound -- never Warmup -- so a late/repeat 0x325 can't flip the match back to
                    // warmup (state 1) and fight the round clock. StartRound with the same roundID is
                    // idempotent: AI_MatchClient::BroadcastMessage keeps the existing AI_MatchRoundClient.
                    if (client.clientReadyHandled)
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.NET_MessageBroker, 5),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                            Make(new MSG_ID_BM_StartRound())
                            ));
                    else
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.NET_MessageBroker, 5),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                            Make(new MSG_ID_BM_Warmup())
                            ));
                    break;
            }
            // NOTE: the post-spawn Gesture (0x28) send was REMOVED. RE proved cGestureMix::Play never writes
            // the active-anim descriptor dword5CC@+0x5CC (only the mood/rosace path does), so no gesture can
            // fix the A-pose. The fix is to re-fire AI_EntityPlayer::UpdateMood @0x10076e90 via a replicated
            // m_Mood DELTA (Block1 field 30). To nail that wire envelope, cycle 1 CAPTURES the client's own
            // incremental-update format (the 0x99 it streams; logged in the 0x99 case above). postSpawnTicks
            // is retained for the delta's send-timing in cycle 2.
            if (msgs.Count > 0)
                return DO_BundleMessage.Create(client, msgs);
            else
                return null;
        }
    }
}
