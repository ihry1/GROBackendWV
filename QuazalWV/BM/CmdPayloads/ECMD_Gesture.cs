using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    // Command::Gesture (Entity_Cmd cmd 0x28). Client path: AI_Entity::ProcessCmdFromNetwork @0x100935d9
    // case 39 -> HandleCmdFromNetwork @0x10092680 -> ProcessCmd case 0x28 -> AI_GestureMixManager::Play.
    // This DIRECTLY sets the active anim descriptor cGestureMix.dword5CC (@+0x5CC), which is otherwise null
    // because UpdateMood @0x10076e90 ran at InitEntity BEFORE the model's anim-banks finished async-loading
    // (UpdateAsyncLoadVisuals @0x100c9800) and the emulated DS never re-fires it. A null dword5CC = A-pose
    // (and zero root-motion -> can't move, since AI_EntityHuman::Master @0x10086a60 takes velocity from the
    // animation refbox). Sending a locomotion gesture establishes dword5CC -> idle/walk anims + movement.
    //
    // Wire (after AppendHeader = [u32 handle][cmd=0x28,6b][isMaster,1b][isServer,1b]):
    //   gestureID  : 8 bits  (sub_10216160 @0x10216160, 2x4 bits)
    //   stretch    : 10 bits (sub_10217EE0, value*0.01 - 1.0 ; 0 -> -1.0 default)
    //   orderID    : 5 bits  (sub_10216440 @0x10216440)
    //   bPlay      : 1 bit
    // gesture 162 / order 21 = the default locomotion set (sub_1014D890 @0x1014d921). isServer=1 required
    // so HandleCmdFromNetwork accepts it for the local-master pawn (@0x100926a0).
    class ECMD_Gesture : Entitiy_CMD
    {
        public byte gestureID;
        public byte orderID;
        public ushort stretch;
        public bool play;

        public ECMD_Gesture(uint h, byte gesture, byte order, ushort stretchCoeff = 0, bool bPlay = true,
                            bool isM = false, bool isS = true)
        {
            handle = h; cmd = 0x28; isMaster = isM; isServer = isS;
            gestureID = gesture; orderID = order; stretch = stretchCoeff; play = bPlay;
        }

        public override byte[] MakePayload()
        {
            BitBuffer buf = new BitBuffer();
            AppendHeader(buf);
            buf.WriteBits(gestureID, 8);
            buf.WriteBits(stretch, 10);
            buf.WriteBits(orderID, 5);
            buf.WriteBit(play);
            return buf.toArray();
        }
    }
}
