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

    // Command::PlayDead (cmd 0x0B=11) -> AI_EntityPlayer::ProcessCmd case 0xB -> AI_EntityPlayer::PlayDead
    // = the ragdoll death animation. The effect has NO master/server check, so sending it with BOTH gate bits 0
    // (isMaster=false,isServer=false -> Command field +0xC == 0) makes a SLAVE accept and play it. Wire format
    // (reader sub_10218810): Vec3 #1 (death pos) then Vec3 #2 (impulse), each 21-bit quantized per axis where the
    // client computes value = V*0.001 - 1000  => V = (P+1000)*1000 ; then u32 +40, u32 +44 (read as floats),
    // 8 bits +48 (hit part), then the CRITICAL 1-bit +52 = field52 (PlayDead's a2). field52 selects the branch in
    // AI_EntityPlayer::PlayDead @0x100cb442: field52=1 -> LARGE -> InitRagdoll -> RAG_SetState(gao,1) = the body goes
    // LIMP (real ragdoll); field52=0 -> SMALL -> RAG_AddImpact ONLY, never RAG_SetState -> the body is NOT put into
    // physics mode -> it freezes gray in its death pose (THE BUG we hit on both screens). RETAIL sends field52=1 (the
    // damage path ProcessCmdTakeDamage sets *(cmd+52)=1 @0x1009039c), so we send 1. Vec3#1=death pos only nudges the
    // fall DIRECTION (bounded/minor per RE); Vec3#2/+40/+44/+48 = 0 = no extra impulse / hit part.
    class ECMD_PlayDead : Entitiy_CMD
    {
        public float px, py, pz;
        public ECMD_PlayDead(uint h, float x, float y, float z, bool isM = false, bool isS = false)
        {
            handle = h; cmd = 0x0B; isMaster = isM; isServer = isS; px = x; py = y; pz = z;
        }
        static void WriteQuant21(BitBuffer buf, float p)
        {
            long v = (long)Math.Round((p + 1000.0) * 1000.0);   // inverse of reader: value = V*0.001 - 1000
            if (v < 0) v = 0; else if (v > 0x1FFFFF) v = 0x1FFFFF;   // clamp to 21 bits
            buf.WriteBits((uint)v, 21);
        }
        public override byte[] MakePayload()
        {
            BitBuffer buf = new BitBuffer();
            AppendHeader(buf);
            WriteQuant21(buf, px); WriteQuant21(buf, py); WriteQuant21(buf, pz);   // Vec3 #1 = death position
            WriteQuant21(buf, 0f); WriteQuant21(buf, 0f); WriteQuant21(buf, 0f);   // Vec3 #2 = impulse (none)
            buf.WriteBits(0u, 32);   // +40 float param = 0
            buf.WriteBits(0u, 32);   // +44 float param = 0
            buf.WriteBits(0u, 3);    // +48 = Command::SetField8 reads only 3 bits (NOT 8!) -> hit part = 0
            buf.WriteBit(true);      // +52 field52 = 1 -> PlayDead LARGE branch -> InitRagdoll -> RAG_SetState(1) = go LIMP (was 0 = SMALL = froze gray, no ragdoll)
            return buf.toArray();
        }
    }

    // Command::DamageGivenFeedback (cmd 0x0A) -> AI_EntityPlayer::ProcessCmd case 0xA -> ShowTotalDamage @0x10090ba0
    // = the SHOOTER's hitmarker / floating damage number. MUST reach the shooter's OWN pawn (handle 2): the consumer
    // asserts serializationFlags&1 ("Only master player is suppose to receive this"), so send isServer=true (v2=2,
    // master gate passes). Wire (reader sub_10219200): Vec3 impact (3x21b) + slotA(4) + damage(10b, =round(dmg)) +
    // slotB(4) + victim handle(32) + headshot(1b).
    class ECMD_DamageGivenFeedback : Entitiy_CMD
    {
        public float px, py, pz, dmg; public uint victimHandle; public bool headshot; public byte bodypart;
        public ECMD_DamageGivenFeedback(uint h, float x, float y, float z, float d, uint victimH, byte bp, bool hs, bool isM = false, bool isS = true)
        { handle = h; cmd = 0x0A; isMaster = isM; isServer = isS; px = x; py = y; pz = z; dmg = d; victimHandle = victimH; bodypart = bp; headshot = hs; }
        static void WriteQ21(BitBuffer buf, float p)
        {
            long v = (long)Math.Round((p + 1000.0) * 1000.0);
            if (v < 0) v = 0; else if (v > 0x1FFFFF) v = 0x1FFFFF;
            buf.WriteBits((uint)v, 21);
        }
        public override byte[] MakePayload()
        {
            BitBuffer buf = new BitBuffer();
            AppendHeader(buf);
            WriteQ21(buf, px); WriteQ21(buf, py); WriteQ21(buf, pz);   // impact point Vec3 (floating-number world pos)
            buf.WriteBits((uint)(bodypart & 0xF), 4);                  // +28 slot A = bodypart (client sets byte540 from it; 0=head -> HUD_HIT_Headshot marker)
            long dv = (long)Math.Round((double)dmg); if (dv < 0) dv = 0; else if (dv > 0x3FF) dv = 0x3FF;
            buf.WriteBits((uint)dv, 10);                                // +32 damage (10-bit, 0..1023)
            buf.WriteBits((uint)(bodypart & 0xF), 4);                  // +36 slot B = bodypart (second copy)
            buf.WriteBits(victimHandle, 32);                           // +40 victim handle (shooter's view; 0 = skip lookup)
            buf.WriteBit(headshot);                                     // +44 headshot/crit flag
            return buf.toArray();
        }
    }

    // Command::Kill (cmd 0x39=57) -> AI_EntityHuman::ProcessCmd case 0x39 -> cLogManager::OnKill @0x10033cc0 = the
    // kill-feed row. v2=0 (unconditional -- no gate on the consumer). Killer/victim NAMES are resolved CLIENT-SIDE
    // from those entities' abstracts (NOT in the cmd), so killerHandle/victimHandle must be the RECIPIENT's view of
    // those two players (handle 2 = own pawn, else 4+peerSlot*2). weaponID only selects the feed icon. Wire (reader
    // sub_10216F10): killerSlot(4) + causeType(4) + killer handle(32) + victim handle(32) + weaponID(32).
    class ECMD_Kill : Entitiy_CMD
    {
        public uint killerHandle, victimHandle, weaponID; public byte causeType; public bool headshot;
        public ECMD_Kill(uint dispatchHandle, uint killerH, uint victimH, uint wpn, byte cause, bool hs = false)
        { handle = dispatchHandle; cmd = 0x39; isMaster = false; isServer = false; killerHandle = killerH; victimHandle = victimH; weaponID = wpn; causeType = cause; headshot = hs; }
        public override byte[] MakePayload()
        {
            BitBuffer buf = new BitBuffer();
            AppendHeader(buf);
            buf.WriteBits(headshot ? 0u : 1u, 4);    // +16 icon gate: 0 => headshot/special icon (9); nonzero => per-weapon icon (was always 0u -> always headshot)
            buf.WriteBits((uint)causeType, 4);       // +20 cause type (0 = bullet)
            buf.WriteBits(killerHandle, 32);         // +24 killer handle (recipient's view)
            buf.WriteBits(victimHandle, 32);         // +28 victim handle (recipient's view)
            buf.WriteBits(weaponID, 32);             // +32 weapon id (feed icon)
            return buf.toArray();
        }
    }
}
