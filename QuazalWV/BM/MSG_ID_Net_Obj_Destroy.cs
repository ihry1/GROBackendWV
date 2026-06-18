using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    // NET_OBJ_DESTROY (msg 626 / 0x272) — the counterpart of MSG_ID_Net_Obj_Create (625/0x271). The client's
    // cObjectManager::BroadcastMessage @AICLASS 0x10040890 case 626 reads [u32 entityHandle][u8 bIsDeadBody],
    // looks the entity up by handle, and DestroyObject()s it. bIsDeadBody=1 => if the handle is already gone it
    // just logs (no "non-existant entity" assert @cObjectManager.cpp:314); =0 asserts on a missing entity.
    // Used to remove a peer's dead pawn (the PlayDead'd white corpse) on respawn before recreating a fresh one.
    public class MSG_ID_Net_Obj_Destroy : BM_Message
    {
        public MSG_ID_Net_Obj_Destroy(uint handle, bool isDeadBody)
        {
            msgID = 0x272;
            MemoryStream m = new MemoryStream();
            Helper.WriteU32(m, handle);                       // u32 entity handle (LE, same encoding as the create's owner field)
            Helper.WriteU8(m, (byte)(isDeadBody ? 1 : 0));    // u8 bIsDeadBody (1 = tolerate an already-destroyed handle, no assert)
            paramList.Add(new BM_Param(BM_Param.PARAM_TYPE.Buffer, m.ToArray()));
        }
    }
}
