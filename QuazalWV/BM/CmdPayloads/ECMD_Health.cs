using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    // The pawn spawns "dead / 0 HP" because its cObjectHealth component (ctor @AICLASS 0x101D07D0)
    // initializes hitpoints to 0.0 and is NEVER fed by the create-replica (the create-blob health
    // lands on AI_EntityDynamic, a different object). In a real match the DS streams these health
    // commands over the Entity_Cmd (0x96) channel. The emulated DS must send them after spawn.
    //
    // Client read path: dispatcher reads handle(32); Command::ReadFromBitBuffer @0x10213d70 reads
    // cmd(6)+flags(2); then the command-specific payload. UpdateHealth/UpdateDefaultHealth read a
    // 32-bit value via BitBuffer::ReadUint @0x10092e50 -> float HP (raw IEEE bits, same as FallingDamage).

    // Command::UpdateHealth (cmd 0x1E) -> cObjectHealth current HP (float @+0x10).
    class ECMD_UpdateHealth : Entitiy_CMD
    {
        public float hp;
        public ECMD_UpdateHealth(uint h, float health, bool isM = false, bool isS = true)
        {
            handle = h; cmd = 0x1E; isMaster = isM; isServer = isS; hp = health;
        }
        public override byte[] MakePayload()
        {
            BitBuffer buf = new BitBuffer();
            AppendHeader(buf);
            int raw = BitConverter.ToInt32(BitConverter.GetBytes(hp), 0);
            buf.WriteBits((uint)raw, 32);
            return buf.toArray();
        }
    }

    // Command::UpdateDefaultHealth (cmd 0x1F) -> cObjectHealth default/max HP (float).
    class ECMD_UpdateDefaultHealth : Entitiy_CMD
    {
        public float hp;
        public ECMD_UpdateDefaultHealth(uint h, float health, bool isM = false, bool isS = true)
        {
            handle = h; cmd = 0x1F; isMaster = isM; isServer = isS; hp = health;
        }
        public override byte[] MakePayload()
        {
            BitBuffer buf = new BitBuffer();
            AppendHeader(buf);
            int raw = BitConverter.ToInt32(BitConverter.GetBytes(hp), 0);
            buf.WriteBits((uint)raw, 32);
            return buf.toArray();
        }
    }

    // Command::UpdateHealthState (cmd 0x20) -> 2 single bits read into cObjectHealth flags
    // [+0x10],[+0x11] (reader @0x10216610). bit0=alive, bit1=KO/dead (best-guess; alive => 1,0).
    class ECMD_UpdateHealthState : Entitiy_CMD
    {
        public bool flag0, flag1;
        public ECMD_UpdateHealthState(uint h, bool f0, bool f1, bool isM = false, bool isS = true)
        {
            handle = h; cmd = 0x20; isMaster = isM; isServer = isS; flag0 = f0; flag1 = f1;
        }
        public override byte[] MakePayload()
        {
            BitBuffer buf = new BitBuffer();
            AppendHeader(buf);
            buf.WriteBit(flag0);
            buf.WriteBit(flag1);
            return buf.toArray();
        }
    }
}
