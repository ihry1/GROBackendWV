using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    public static class DO_FetchRequestMessage
    {
        public static byte[] HandleMessage(ClientInfo client, byte[] data)
        {
            List<byte[]> msgs;
            Log.WriteLine(2, "[DO] Handling DO_FetchRequestMessage...");
            MemoryStream m = new MemoryStream(data);
            m.Seek(3, 0);
            uint dupObj = Helper.ReadU32(m);
            switch (dupObj)
            {
                case 0x5C00001:
                    msgs = new List<byte[]>();
                    if (!client.bootStrapDone)
                    {
                        foreach (DupObj obj in DO_Session.DupObjs)
                        {
                            // 2-player: never hand a client ANOTHER client's Station. A peer Station carries its
                            // own PRUDP URL; the joining client tries to open a DO connection to it at the
                            // Station-state 3->4 transition and crashes HARD (no server error) at "connecting to
                            // match server". Send base (non-Station) objects + host Station 1 + THIS client's own
                            // station only; peers are seen via the separately-replicated PAWN entity (which needs
                            // no DO participant). The (ID>=2 && ID!=stationID) guard is a no-op for one client.
                            if (obj.Class == DupObjClass.Station && obj.ID >= 2 && obj.ID != client.stationID)
                                continue;
                            // ...and never a peer's PLAYER object (SES_cl_Player_NetZ, e.g. ID=257 = player-1's
                            // NetZ avatar/params). That object was the SOLE asymmetry vs the 1st joiner's bootstrap
                            // and is the actual crash trigger at the Station-state 3->4 transition (the peer Station
                            // was a red herring). Each client creates its OWN player object AFTER bootstrap and sees
                            // peers via the separately-replicated PAWN entity, so excluding these is safe + symmetric.
                            if (obj.Class == DupObjClass.SES_cl_Player_NetZ)
                                continue;
                            msgs.Add(DO_CreateDuplicaMessage.Create(obj, 2));
                        }
                        client.bootStrapDone = true;
                    }
                    msgs.Add(DO_MigrationMessage.Create(client.callCounterDO_RMC++, new DupObj(DupObjClass.Station, 1), new DupObj(DupObjClass.Station, client.stationID), new DupObj(DupObjClass.Station, client.stationID), 3, new List<uint>() { new DupObj(DupObjClass.Station, client.stationID) }));
                    return DO_BundleMessage.Create(client, msgs);
                default:
                    Log.WriteLine(1, "[DO] Handling DO_FetchRequest unknown dupObj 0x" + dupObj.ToString("X8") + "!");
                    return new byte[0];
            }
        }

        public static byte[] Create(ushort callID, DupObj obj)
        {
            Log.WriteLine(2, "[DO] Creating DO_FetchRequestMessage");
            MemoryStream m = new MemoryStream();
            m.WriteByte(0xD);
            Helper.WriteU16(m, callID);
            Helper.WriteU32(m, obj);
            Helper.WriteU32(m, obj.Master);
            return m.ToArray();
        }
    }
}
