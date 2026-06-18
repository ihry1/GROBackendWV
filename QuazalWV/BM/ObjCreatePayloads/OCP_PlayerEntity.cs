using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Linq.Expressions;
using System.Runtime.Remoting;

namespace QuazalWV
{
    // OCP_PlayerEntity — the concrete player pawn create-replica (bank 0x2A, elem 0x05),
    // i.e. the SaveTo blob parsed by AI_EntityPlayer::LoadFrom @ AICLASS 0x100c8320.
    //
    // BYTE-EXACT layout, reverse-engineered field-by-field from the client's read path
    // (LoadFrom -> AI_Entity::LoadFrom 0x100942c0 -> DeserializeReplicatedData 0x10093f40
    //  -> BitArray LoadFrom_10 0x10084ac0; ClassInfo via cClassInfoPC::Deserialize 0x101c7a20
    //  -> DeserializeMemBuffers 0x101cd9b0). Cross-checked against RE/plan/04 (SaveTo side).
    //
    // Endianness via QuazalWV.Helper INVERTED naming:
    //   WriteU16LE/WriteU32LE/WriteFloatLE  -> BIG-endian   (byteswapped)
    //   WriteU16  /WriteU32  /WriteFloat    -> LITTLE-endian (raw)
    //
    // Replicated-data block framing (confirmed): [u8 size][u8 size2][size mask bytes],
    //   size = ceil(fieldCount/8), size2 = fieldCount, mask bit i == 0 => field i present.
    //   To send every field, all mask bytes are 0x00.
    //
    // The 9 ClassInfo mem-buffers are each length-prefixed [u8 len][len bytes], so the
    // client consumes exactly `len` bytes per slot regardless of inner content.
    public class OCP_PlayerEntity
    {
        public enum MoveMode
        {
            eMoveModeStop = 0,
            eMoveModeFree = 1,
            eMoveModeCover = 2,
            eMoveModeGoToPosition = 3,
            eMoveModeCross = 4,
            eMoveModeAnimControl = 5,
            eMoveModeSlide = 6,
            eMoveModeTeleport = 7,
        }

        public enum ClassInfoMemBuffer
        {
            eMainWeapon = 0,
            ePistol = 1,
            eGrenade = 2,
            eArmor = 3,
            eHelmetKey = 4,
            eAbility = 5,
            ePassiveAbility = 6,
            eWeaponBoost = 7,
            eBody = 8
        }

        public uint handle;
        public byte teamID = 0x1;
        public byte classID = 0;
        public float Health = 100f;
        public uint mainWeaponID = 170;  // M27 D10RS (Assault default rifle) — real RoF/tracer props; "Test" 1000 had ~zero -> sporadic/slow tracers
        public uint pistolWeaponID = 339; // P250 (secondary). Was reusing mainWeaponID, so both slots were the M27.
        public uint armorInventoryID = 1;
        public uint helmetKey = 0xF8700A85;
        public byte abilityType = 6;
        public byte passiveAbilityType = 3;
        public byte faceID = 1;
        public byte skinID = 1;
        public uint pid = 0;               // owner persona, set by Entitiy_CMD; drives per-instance custom weapon parts (0 = defaults). NOT serialized.

        public OCP_PlayerEntity(uint h)
        {
            handle = h;
        }

        public byte[] MakePayload()
        {
            MemoryStream m = new MemoryStream();

            // ---- AI_Entity::LoadFrom : handle (u32 BE) ----
            Helper.WriteU32LE(m, handle);

            // ================= Block1 (serialStruct1) : 33 fields =================
            // ★ m_ReplicatedCamPitch is NOT a Block1 field. Its RDC flag bit3(0x8) is CLEAR at
            //   registration (AI_EntityHuman ctor @0x1008530e: m_ReplicatedCamPitch.flags &= 0xFFFFFFC0),
            //   and AI_Entity::RegisterReplicatedData(_0) @0x10094020/0x100940b0 routes every
            //   (flags & 8)==0 field to serialStruct2 (Block2). So campitch lives in Block2 AFTER
            //   m_PlayerFire (it IS the "trailing 2B" a trace saw). Writing it HERE (the old bug) put
            //   2 extra bytes in Block1 that shifted m_Mood exactly 2 bytes early -> the client
            //   deserialized m_Mood = 0x0 (it read [kikoo,kikoo,moodHi,moodHi]=00 00 00 00, with the
            //   real 00 00 00 CE 2 bytes further on) -> InitEntity's UpdateMood was a no-op -> dword5CC
            //   stayed null -> A-pose + SetWeaponSide null-deref crash. Proven via [RDR] Read_NR probe
            //   (val=0x0) + IDA (ctor flags + RegisterReplicatedData block routing).
            //   FIX is net-zero for ClassInfo: drop campitch here (-2B, count 34->33) and add the
            //   2-byte Block2 mask header below (+2B) -> every byte from m_ReplicatedPosition onward
            //   is byte-identical, so weapons/ClassInfo stay aligned while m_Mood now lands 0xCE.
            // header: size=ceil(33/8)=5, size2=33, 5 zero mask bytes (all fields present)
            Helper.WriteU8(m, 5);
            Helper.WriteU8(m, 33);
            m.Write(new byte[5], 0, 5);

            Helper.WriteU32LE(m, 0);                       //  1 m_CritSalt              u32 BE
            Helper.WriteU32LE(m, 0);                       //  2 m_CritRandSeed          u32 BE
            Helper.WriteU32LE(m, 0);                       //  3 m_ShootTargetHandle     u32 BE (0=none)
            Helper.WriteU8(m, 0);                          //  4 m_WhistlingBullet       u8
            Helper.WriteU8(m, 0);                          //  5 m_Rush                  u8
            Helper.WriteU8(m, 0);                          //  6 m_CurrentWeaponSlot     u8 (0=main)
            Helper.WriteU8(m, 0);                          //  7 m_WantedWeaponSlot      u8
            Helper.WriteU8(m, 0);                          //  8 m_OldWeaponSlot         u8
            Helper.WriteU8(m, 0);                          //  9 m_GoToPosition          [u8 count]=0  (m_ReplicatedCamPitch removed -> Block2)
            Helper.WriteU8(m, (byte)MoveMode.eMoveModeFree); // 10 m_MoveMode            u8 =1 (free)
            Helper.WriteU32LE(m, 0);                       // 11 m_FocusedEntityReplication u32 BE
            Helper.WriteU16(m, 0);                         // 12 m_CoverHeight           u16 LE fixed(x2)
            Helper.WriteU16LE(m, 0);                       // 13 m_CoverFlagWanted       u16 BE =0 (no cover/peek)
            m.Write(new byte[9], 0, 9);                    // 14 m_CoverNormal           9B compressed vec (RDC_Vector3D, GetStruct 9)
            m.Write(new byte[] { 0x01, 0, 0, 0 }, 0, 4);   // 15 m_State                 4B [01][s3][s2][s0]
            m.Write(new byte[] { 0x01, 0, 0, 0 }, 0, 4);   // 16 m_StateServer           4B
            Helper.WriteU8(m, 0);                          // 17 m_LaserSightStateCurr   u8
            m.Write(new byte[12], 0, 12);                  // 18 m_SlideVelocity         12B compressed vel (GetStruct 0xC)
            Helper.WriteU8(m, 0);                          // 19 m_SlideToRosaceAnim     u8
            Helper.WriteU16(m, 0);                         // 20 m_ADSDamage             u16 LE fixed(x10)
            Helper.WriteU16(m, 0);                         // 21 m_PostADSDamage         u16 LE fixed(x10)
            Helper.WriteU8(m, 0);                          // 22 m_bIsInADSCone          u8
            Helper.WriteU8(m, 0);                          // 23 m_BlitzShieldArmed      u8  <-- was 1: every spawn started shield-ARMED without the arm logic running -> blitz deploy state inconsistent -> rosace stretch coef out of [0.1,10] -> cGestureMix::StretchRosace "invalid stretch" __debugbreak crash on Blitz activate. 0 = stowed (correct spawn state).
            // 24 m_OrderStatus 13B  [0x0A][b12][b11][10 bytes]
            m.Write(new byte[] { 0x0A, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 0, 13);
            Helper.WriteU8(m, 0);                          // 25 m_FireModeType          u8
            Helper.WriteU8(m, 0);                          // 26 m_RollAnimIndex         u8
            Helper.WriteU8(m, 0); Helper.WriteU32LE(m, 0); // 27 m_ReplicatedPowerPC     u8 + u32 BE
            Helper.WriteU16(m, 1000);                      // 28 m_CurrentEnergyPC       u16 LE fixed(x10) = 100.0 ability charge (cPlayerPowerPC inits to 0 + regen is server-only; seed it so the HUD shows charge)
            Helper.WriteU16LE(m, 0);                       // 29 m_KikooMoveCount        u16 BE
            Helper.WriteU32LE(m, 0xCE);                    // 30 m_Mood                  u32 BE  <-- NOW lands at the offset the client reads (campitch no longer shifts it)
            Helper.WriteU8(m, 0);                          // 31 m_HitPart               u8
            Helper.WriteU8(m, 0);                          // 32 m_LeftHandSide          u8
            Helper.WriteU8(m, 1);                          // 33 m_bHealthRegenActive    u8

            // ============== Block2 region (serialStruct2) : 5 fields, WITH 2-byte header ==============
            // Block2 = the (flags & 8)==0 fields, in registration order: m_ReplicatedPosition,
            // m_ReplicatedAngle, m_ShootPosition, m_PlayerFire, m_ReplicatedCamPitch. The client's
            // DeserializeReplicatedData(0) reads a bitarray header here too: [u8 size][u8 size2]
            // [size mask bytes]. size=0 => 0 mask bytes => all 5 present.
            // The OLD "no header" build only worked because the campitch-in-Block1 bug left 2 stray
            // bytes here (m_LeftHandSide=0, m_bHealthRegenActive=1) that the client consumed as this
            // header (size=0). With campitch moved out of Block1 those stray bytes are gone, so we
            // supply [00][05] explicitly -> m_ReplicatedPosition starts at the SAME absolute offset as
            // before -> the AI_EntityDynamic tail + 9 ClassInfo mem-buffers are byte-identical.
            // 23 bytes total (2 header + 8,2,8,1,2). Field widths confirmed by GetStruct trace.
            Helper.WriteU8(m, 0);                          // Block2 mask size = 0 (no mask bytes => all present)
            Helper.WriteU8(m, 5);                          // Block2 size2 = field count (loop uses client's serialStruct2.replicationCount)
            m.Write(new byte[8], 0, 8);                    //  1 m_ReplicatedPosition    8B
            Helper.WriteU16(m, 0);                         //  2 m_ReplicatedAngle       u16
            m.Write(new byte[8], 0, 8);                    //  3 m_ShootPosition         8B
            Helper.WriteU8(m, 0);                          //  4 m_PlayerFire            u8
            Helper.WriteU16(m, 0);                         //  5 m_ReplicatedCamPitch    u16 (Block2 field, was double-written in Block1)

            // ================= AI_EntityDynamic tail (11B) =================
            // AI_EntityDynamic::LoadFrom reads teamID FIRST then classId; the ClassInfo STORE keys on the
            // SECOND read (this->classId @ +0x80). With the Block2 framing fixed (no stray byte), write
            // teamID then selected classID so STORE key (pid, classId) == reader key (pid, abstract.m_Class).
            // (The earlier classID-first swap was only compensating for the 1-byte Block2 shift.)
            Helper.WriteU8(m, teamID);                     // teamID            u8 (read first)
            Helper.WriteU8(m, classID);                    // classId           u8 (read second; STORE keys on this == m_Class=0)
            Helper.WriteFloatLE(m, Health);                // healthPoints      float BE
            Helper.WriteU32LE(m, 0);                       // cMemBuffer field  u32 BE (=0)
            // ragdollSyncFlags u8 = 2 (bit1 "alive" SET). ★★ THE 2-CLIENT PEER-FREEZE FIX (2026-06-15).
            // This byte becomes AI_EntityPlayer+0xB4 (AI_EntityDynamic::LoadFrom @0x1009544a reads it last in the
            // dynamic tail). For a SLAVE (the peer pawn on the OTHER player's screen) AI_EntityHuman::Replica
            // @0x10087b40 gates ALL body movement behind CheckRagdollFlag1 @0x10095570 = ((+0xB4 & 2)==0):
            // bit1 CLEAR -> early-return BEFORE OBJ_SetMatrix -> body FROZEN/A-posed at spawn; bit1 SET -> runs
            // sub_1008CC40 (copies m_ReplicatedPosition.repValue -> replNetPos every frame) -> OBJ_SetMatrix ->
            // body follows the relayed 0x99 transform. The whole replication chain (relay -> m_ReplicatedPosition
            // -> replNetPos -> body) was already proven working; this gate was the ONLY broken link. Retail set
            // bit1 via ECMD_UpdateHealthState(alive=true) right after spawn (ProcessCmd case 0x20: flag0->bit1),
            // but that cmd is MASTER-ONLY (AI_Entity::ProcessCmdFromNetwork asserts for a slave -> crash) so a
            // PEER can NEVER receive it -> bake the alive bit into the create-blob. Was 0 = the exact reason the
            // peer A-posed while its own (master) pawn moved fine. Local pawn unaffected (master path doesn't use
            // this gate, and its own UpdateHealthState rewrites it to 2 anyway).
            Helper.WriteU8(m, 2);                          // ragdollSyncFlags  u8 (2 = alive; see above)

            // ================= AI_EntityPlayer tail =================
            Helper.WriteU8(m, 0);                          // actionProcessCount u8
            Helper.WriteU8(m, 0);                          // extraByte          u8

            // ClassInfo: 9 mem-buffers, each [u8 len][len bytes]
            byte[] buffer;
            for (int i = 0; i < 9; i++)
            {
                switch ((ClassInfoMemBuffer)i)
                {
                    case ClassInfoMemBuffer.eMainWeapon:
                        ClassInfo_Gun mainRifleInfo = new ClassInfo_Gun(mainWeaponID, pid, 1, (uint)(4 + classID)); // loadout slot 1 = primary -> its custom parts, from THIS class's loadout bag (4+classID), not hardcoded Assault
                        mainRifleInfo.memBufferSize = Convert.ToByte(mainRifleInfo.MakePayload().Length - 1);
                        buffer = mainRifleInfo.MakePayload();
                        m.Write(buffer, 0, buffer.Length);
                        break;

                    case ClassInfoMemBuffer.ePistol:
                        ClassInfo_Gun pistolInfo = new ClassInfo_Gun(pistolWeaponID, pid, 2, (uint)(4 + classID)); // loadout slot 2 = secondary -> its custom parts, from THIS class's loadout bag (4+classID), not hardcoded Assault
                        pistolInfo.memBufferSize = Convert.ToByte(pistolInfo.MakePayload().Length - 1);
                        buffer = pistolInfo.MakePayload();
                        m.Write(buffer, 0, buffer.Length);
                        break;

                    case ClassInfoMemBuffer.eGrenade:
                        // Fixed placeholder grenade (Test components -- see ClassInfo_Grenade); pin weaponID
                        // to 170 so making mainWeaponID dynamic from the real loadout doesn't alter this slot.
                        ClassInfo_Grenade nadeInfo = new ClassInfo_Grenade(170);
                        nadeInfo.memBufferSize = Convert.ToByte(nadeInfo.MakePayload().Length - 1);
                        buffer = nadeInfo.MakePayload();
                        m.Write(buffer, 0, buffer.Length);
                        break;

                    case ClassInfoMemBuffer.eArmor:
                        ClassInfo_Armor armorInfo = new ClassInfo_Armor(armorInventoryID, pid); // pid -> resolve THIS persona's equipped-armor-tier combat properties (0-safe: pid==0 / DB miss -> neutral 0.0)
                        armorInfo.memBufferSize = Convert.ToByte(armorInfo.MakePayload().Length - 1);
                        buffer = armorInfo.MakePayload();
                        m.Write(buffer, 0, buffer.Length);
                        break;

                    case ClassInfoMemBuffer.eHelmetKey:
                        Helper.WriteU8(m, 4);                 // slot length
                        Helper.WriteU32LE(m, helmetKey);      // helmet asset key (BE)
                        break;

                    case ClassInfoMemBuffer.eAbility:
                        ClassInfo_Ability abilityInfo = new ClassInfo_Ability(abilityType);
                        abilityInfo.memBufferSize = Convert.ToByte(abilityInfo.MakePayload().Length - 1);
                        buffer = abilityInfo.MakePayload();
                        m.Write(buffer, 0, buffer.Length);
                        break;

                    case ClassInfoMemBuffer.ePassiveAbility:
                        ClassInfo_PassiveAbility pasAbilityInfo = new ClassInfo_PassiveAbility(passiveAbilityType);
                        pasAbilityInfo.memBufferSize = Convert.ToByte(pasAbilityInfo.MakePayload().Length - 1);
                        buffer = pasAbilityInfo.MakePayload();
                        m.Write(buffer, 0, buffer.Length);
                        break;

                    case ClassInfoMemBuffer.eWeaponBoost:
                        buffer = new ClassInfo_Boost().MakePayload(); // const size
                        m.Write(buffer, 0, buffer.Length);
                        break;

                    case ClassInfoMemBuffer.eBody:
                        buffer = new ClassInfo_Body(faceID, skinID).MakePayload();  // const size
                        m.Write(buffer, 0, buffer.Length);
                        break;
                }
            }

            // DOB float (BE)
            Helper.WriteFloatLE(m, 0f);
            return m.ToArray();
        }
    }
}
