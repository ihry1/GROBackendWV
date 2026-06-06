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

        public OCP_PlayerEntity(uint h)
        {
            handle = h;
        }

        public byte[] MakePayload()
        {
            MemoryStream m = new MemoryStream();

            // ---- AI_Entity::LoadFrom : handle (u32 BE) ----
            Helper.WriteU32LE(m, handle);

            // ================= Block1 (serialStruct1) : 34 fields =================
            // header: size=ceil(34/8)=5, size2=34, 5 zero mask bytes (all fields present)
            Helper.WriteU8(m, 5);
            Helper.WriteU8(m, 34);
            m.Write(new byte[5], 0, 5);

            Helper.WriteU32LE(m, 0);                       //  1 m_CritSalt              u32 BE
            Helper.WriteU32LE(m, 0);                       //  2 m_CritRandSeed          u32 BE
            Helper.WriteU32LE(m, 0);                       //  3 m_ShootTargetHandle     u32 BE (0=none)
            Helper.WriteU8(m, 0);                          //  4 m_WhistlingBullet       u8
            Helper.WriteU8(m, 0);                          //  5 m_Rush                  u8
            Helper.WriteU8(m, 0);                          //  6 m_CurrentWeaponSlot     u8 (0=main)
            Helper.WriteU8(m, 0);                          //  7 m_WantedWeaponSlot      u8
            Helper.WriteU8(m, 0);                          //  8 m_OldWeaponSlot         u8
            Helper.WriteU16(m, 0);                         //  9 m_ReplicatedCamPitch    u16 LE fixed(x1000)
            Helper.WriteU8(m, 0);                          // 10 m_GoToPosition          [u8 count]=0
            Helper.WriteU8(m, (byte)MoveMode.eMoveModeFree); // 11 m_MoveMode            u8 =1 (free)
            Helper.WriteU32LE(m, 0);                       // 12 m_FocusedEntityReplication u32 BE
            Helper.WriteU16(m, 0);                         // 13 m_CoverHeight           u16 LE fixed(x2)
            Helper.WriteU16LE(m, 0);                       // 14 m_CoverFlagWanted       u16 BE =0 (no cover/peek)
            m.Write(new byte[9], 0, 9);                    // 15 m_CoverNormal           9B compressed vec
            m.Write(new byte[] { 0x01, 0, 0, 0 }, 0, 4);   // 16 m_State                 4B [01][s3][s2][s0]
            m.Write(new byte[] { 0x01, 0, 0, 0 }, 0, 4);   // 17 m_StateServer           4B
            Helper.WriteU8(m, 0);                          // 18 m_LaserSightStateCurr   u8
            m.Write(new byte[12], 0, 12);                  // 19 m_SlideVelocity         12B compressed vel
            Helper.WriteU8(m, 0);                          // 20 m_SlideToRosaceAnim     u8
            Helper.WriteU16(m, 0);                         // 21 m_ADSDamage             u16 LE fixed(x10)
            Helper.WriteU16(m, 0);                         // 22 m_PostADSDamage         u16 LE fixed(x10)
            Helper.WriteU8(m, 0);                          // 23 m_bIsInADSCone          u8
            Helper.WriteU8(m, 1);                          // 24 m_BlitzShieldArmed      u8
            // 25 m_OrderStatus 13B  [0x0A][b12][b11][10 bytes]
            m.Write(new byte[] { 0x0A, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 0, 13);
            Helper.WriteU8(m, 0);                          // 26 m_FireModeType          u8
            Helper.WriteU8(m, 0);                          // 27 m_RollAnimIndex         u8
            Helper.WriteU8(m, 0); Helper.WriteU32LE(m, 0); // 28 m_ReplicatedPowerPC     u8 + u32 BE
            Helper.WriteU16(m, 1000);                      // 29 m_CurrentEnergyPC       u16 LE fixed(x10) = 100.0 ability charge (cPlayerPowerPC inits to 0 + regen is server-only; seed it so the HUD shows charge)
            Helper.WriteU16LE(m, 0);                       // 30 m_KikooMoveCount        u16 BE
            Helper.WriteU32LE(m, 0xCE);                    // 31 m_Mood                  u32 BE
            Helper.WriteU8(m, 0);                          // 32 m_HitPart               u8
            Helper.WriteU8(m, 0);                          // 33 m_LeftHandSide          u8
            Helper.WriteU8(m, 1);                          // 34 m_bHealthRegenActive    u8

            // ============== Block2 region: NO second bitarray header ==============
            // Runtime GetStruct trace proves the client reads these fields UNCONDITIONALLY: it reads
            // 8,2,8,1,2 starting exactly where the old [01][04][00] header sat. That header was being
            // consumed as ReplicatedPosition data, shifting every ClassInfo slot by 1 byte (the grenade
            // parser then overran the pistol slot -> cBuffer.cpp:20 "invalid size"). 21 bytes total.
            m.Write(new byte[8], 0, 8);                    //  1 m_ReplicatedPosition    8B
            Helper.WriteU16(m, 0);                         //  2 m_ReplicatedAngle       u16
            m.Write(new byte[8], 0, 8);                    //  3 m_ShootPosition         8B
            Helper.WriteU8(m, 0);                          //  4 m_PlayerFire            u8
            Helper.WriteU16(m, 0);                         //  5 trailing 2B field the client reads after PlayerFire

            // ================= AI_EntityDynamic tail (11B) =================
            // AI_EntityDynamic::LoadFrom reads teamID FIRST then classId; the ClassInfo STORE keys on the
            // SECOND read (this->classId @ +0x80). With the Block2 framing fixed (no stray byte), write
            // teamID then classID(0) so STORE key (pid, classId=0) == reader key (pid, abstract.m_Class=0).
            // (The earlier classID-first swap was only compensating for the 1-byte Block2 shift.)
            Helper.WriteU8(m, teamID);                     // teamID            u8 (read first)
            Helper.WriteU8(m, classID);                    // classId           u8 (read second; STORE keys on this == m_Class=0)
            Helper.WriteFloatLE(m, Health);                // healthPoints      float BE
            Helper.WriteU32LE(m, 0);                       // cMemBuffer field  u32 BE (=0)
            Helper.WriteU8(m, 0);                          // ragdollSyncFlags  u8

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
                        ClassInfo_Gun mainRifleInfo = new ClassInfo_Gun(mainWeaponID);
                        mainRifleInfo.memBufferSize = Convert.ToByte(mainRifleInfo.MakePayload().Length - 1);
                        buffer = mainRifleInfo.MakePayload();
                        m.Write(buffer, 0, buffer.Length);
                        break;

                    case ClassInfoMemBuffer.ePistol:
                        ClassInfo_Gun pistolInfo = new ClassInfo_Gun(pistolWeaponID);
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
                        ClassInfo_Armor armorInfo = new ClassInfo_Armor();
                        armorInfo.memBufferSize = Convert.ToByte(armorInfo.MakePayload().Length - 1);
                        buffer = armorInfo.MakePayload();
                        m.Write(buffer, 0, buffer.Length);
                        break;

                    case ClassInfoMemBuffer.eHelmetKey:
                        Helper.WriteU8(m, 4);                 // slot length
                        Helper.WriteU32LE(m, 0xF8700A85);     // helmet asset key (BE)
                        break;

                    case ClassInfoMemBuffer.eAbility:
                        ClassInfo_Ability abilityInfo = new ClassInfo_Ability(6);
                        abilityInfo.memBufferSize = Convert.ToByte(abilityInfo.MakePayload().Length - 1);
                        buffer = abilityInfo.MakePayload();
                        m.Write(buffer, 0, buffer.Length);
                        break;

                    case ClassInfoMemBuffer.ePassiveAbility:
                        ClassInfo_PassiveAbility pasAbilityInfo = new ClassInfo_PassiveAbility(3);
                        pasAbilityInfo.memBufferSize = Convert.ToByte(pasAbilityInfo.MakePayload().Length - 1);
                        buffer = pasAbilityInfo.MakePayload();
                        m.Write(buffer, 0, buffer.Length);
                        break;

                    case ClassInfoMemBuffer.eWeaponBoost:
                        buffer = new ClassInfo_Boost().MakePayload(); // const size
                        m.Write(buffer, 0, buffer.Length);
                        break;

                    case ClassInfoMemBuffer.eBody:
                        buffer = new ClassInfo_Body().MakePayload();  // const size
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
