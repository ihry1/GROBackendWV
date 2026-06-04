using System;
using System.IO;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QuazalWV
{
    public abstract class Entitiy_CMD
    {
        public enum CMDs
        {
            FallingDamage = 0x1C,
            Gesture = 0x28,
            GestureAnimIdx = 0x29,
            FireAction = 0x36,
            SpawnRequest = 0x34,    // abstract cmd '4' (0x34) PlayerRequestSpawn  (AI_EntityPlayerAbstract::ProcessCmd case '4')
            ClientReady = 0x35      // abstract cmd '5' (0x35) PlayerClientReady (client->server, sent after weapons async-load)
        }
        public uint handle;
        public byte cmd;
        public bool isMaster;
        public bool isServer;

        public void AppendHeader(BitBuffer buf)
        {
            buf.WriteBits(handle, 32);
            buf.WriteBits(cmd, 6);
            buf.WriteBits((uint)(isMaster ? 1 : 0), 1);
            buf.WriteBits((uint)(isServer ? 1 : 0), 1);
        }

        public abstract byte[] MakePayload();

        public static byte[] GetRawPayload(Stream s)
        {
            s.Seek(-8, SeekOrigin.Current);
            ushort size = Helper.ReadU16LE(s);
            byte[] buff = new byte[size];
            s.Read(buff, 0, size);
            return buff;
        }

        public static byte[] HandleMsg(ClientInfo client, Stream s)
        {
            List<byte[]> msgs = new List<byte[]>();
            Helper.ReadU32(s);
            uint handle = Helper.ReadU32(s);
            byte cmd = (byte)(Helper.ReadU8(s) & 0x3F);
            long pos = s.Position;
            byte[] raw = GetRawPayload(s);
            s.Seek(pos, 0);
            Log.WriteLine(2, "Received CMD 0x" + cmd.ToString("X8") + " (" + (CMDs)cmd + ") for Handle 0x" + handle.ToString("X8"), Color.Red);
            switch ((CMDs)cmd)
            {
                case CMDs.FallingDamage:
                    raw[5] = 0x1C;
                    msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                        0x1006,
                        new DupObj(DupObjClass.Station, 1),
                        new DupObj(DupObjClass.NET_MessageBroker, 5),
                        (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                        //BM_Message.Make(new MSG_ID_Entity_Cmd(client, cmd))
                        BM_Message.Make(new MSG_ID_Entity_Cmd(raw))
                        ));
                    break;
                case CMDs.Gesture:
                case CMDs.GestureAnimIdx:
                case CMDs.FireAction:
                    msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                        0x1006,
                        new DupObj(DupObjClass.Station, 1),
                        new DupObj(DupObjClass.NET_MessageBroker, 5),
                        (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                        BM_Message.Make(new MSG_ID_Entity_Cmd(raw))
                        ));
                    break;
                case CMDs.SpawnRequest:
                    if (!client.playerCreateStuffSent2)
                    {
                        client.playerAbstractState = 3;
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.NET_MessageBroker, 5),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                            BM_Message.Make(new MSG_ID_Entity_Cmd(client, 0x33))
                            ));
                        // Set ONLY ServerReady (bit 0x1000) so the client's ProcessSpawningMaster Step 4
                        // (bIsServerReady) passes. Do NOT pre-set ClientReady (0x2000) here — the real DS
                        // sets it only when the client sends ClientReady (0x35), AFTER its weapons finish
                        // async-loading (verified: AI_EntityPlayerAbstract::ProcessCmd case '5' @0x100d7120).
                        client.settings.bitField14.entries[3].word = 1;//server ready (bit 0x1000)
                        client.settings.bitField14.entries[0].word = 1;//spawnCount
                        client.settings.bitField14.entries[1].word = 1;//requestSpawn
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.SES_cl_Player_NetZ, 257),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.SetPlayerParameters,
                            client.settings.toBuffer()
                            ));
                        // Resolve a real spawn point for the session's map from Yeti.big at runtime
                        // (falls back to Global.spawn* if the archive/map/zone isn't found).
                        // Team 1 matches OCP_PlayerEntity.teamID; MSG_ID_Net_Obj_Create reads Global.spawn*.
                        float _sx, _sy, _sz;
                        if (YetiBigSpawnReader.TryGetSpawn(SessionInfosParameter.defaultMapKey, 1, out _sx, out _sy, out _sz))
                        {
                            Global.spawnX = _sx; Global.spawnY = _sy; Global.spawnZ = _sz;
                            Log.WriteLine(1, "[DS] Spawning player at (" + _sx + ", " + _sy + ", " + _sz + ") from Yeti.big");
                        }
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.NET_MessageBroker, 5),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                            BM_Message.Make(new MSG_ID_Net_Obj_Create(0x2A, 0x05, new OCP_PlayerEntity(2).MakePayload()))
                            ));
                        // HOLD at state 3 (Spawning) — do NOT walk to state 5 here. The client must run its
                        // ProcessSpawningMaster handshake (concrete pawn exists + weapons async-load ~2.4s +
                        // ServerReady) and then send ClientReady (0x35); we advance to state 5 in THAT handler.
                        // Jumping to 5 here (the old bug) skipped the handshake in ~84ms while weapons took
                        // ~2.4s -> client-ready never set -> combat-input gate (bIsClientReady) never opened
                        // -> no move / no ADS scope / no fire.
                        // Feed the pawn's cObjectHealth (handle 2): it inits to 0 HP and the create-blob
                        // health never reaches it, so the pawn renders dead/0HP. Set max+current = 100 and
                        // mark alive via the DS->client health commands the emulated DS otherwise never sends.
                        foreach (Entitiy_CMD hc in new Entitiy_CMD[] {
                            new ECMD_UpdateDefaultHealth(2, 100f),
                            new ECMD_UpdateHealth(2, 100f),
                            new ECMD_UpdateHealthState(2, true, false) })
                            msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                                0x1006,
                                new DupObj(DupObjClass.Station, 1),
                                new DupObj(DupObjClass.NET_MessageBroker, 5),
                                (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                                BM_Message.Make(new MSG_ID_Entity_Cmd(hc.MakePayload()))
                                ));
                        client.playerCreateStuffSent2 = true;
                    }
                    break;
                case CMDs.ClientReady:
                    // The client finished its state-3 Spawning handshake (concrete pawn + async-loaded
                    // weapons + ServerReady) and sent ClientReady (cmd 0x35). The real DS responds by setting
                    // the ClientReady bit 0x2000 in the player params (AI_EntityPlayerAbstract::ProcessCmd
                    // case '5' @0x100d7120). Mirror that, then ChangeState(5) Loop so the combat-input gate
                    // (AI_EntityPlayerAbstract::Spawn bIsClientReady) finally opens -> move/ADS/fire unlock.
                    if (client.playerCreateStuffSent2 && !client.clientReadyHandled)
                    {
                        client.settings.bitField14.entries[4].word = 1;//client ready (bit 0x2000)
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.SES_cl_Player_NetZ, 257),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.SetPlayerParameters,
                            client.settings.toBuffer()
                            ));
                        client.playerAbstractState = 5;
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.NET_MessageBroker, 5),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                            BM_Message.Make(new MSG_ID_Entity_Cmd(client, 0x33))
                            ));
                        client.clientReadyHandled = true;
                        Log.WriteLine(1, "[DS] Client ClientReady (0x35) -> set params bit 0x2000 + ChangeState(5) Loop");
                    }
                    break;
            }
            if (msgs.Count > 0)
                return DO_BundleMessage.Create(client, msgs);
            else
                return null;
        }
    }
}
