using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    // OCP_AbstractPlayerEntity — the abstract player create-replica (bank 0x2C, elem 0x15),
    // i.e. the SaveTo blob parsed by AI_EntityPlayerAbstract::LoadFrom @ AICLASS 0x100d50e0.
    //
    // BYTE-EXACT layout per RE/plan/04 (SaveTo 0x100d51b0) confirmed against the read path.
    // Endianness via QuazalWV.Helper INVERTED naming: *LE = BIG-endian, plain = LITTLE-endian.
    // Block framing: [u8 size][u8 size2][size mask bytes], mask bit i==0 => field present.
    //
    // The pawn's ClassInfo is keyed by (pid, m_Class); m_Class here MUST match
    // OCP_PlayerEntity.classID for the selected class. m_SpawnBlockingReasons MUST be 0
    // (non-zero blocks deploy).
    public class OCP_AbstractPlayerEntity
    {
        public uint handle;
        public byte teamID = 0x1;
        public byte classID = 0;
        public uint pid = 0x1234;
        public uint dsGameMode = 0;
        public uint abilityInventoryId = 0;
        public uint passiveAbilityInventoryId = 0;
        public uint desiredWeaponMainInventoryId = 0;
        public uint desiredWeaponPistolInventoryId = 0;
        public uint desiredWeaponGrenadeInventoryId = 0;
        public uint helmetInventoryId = 0;
        public uint armorInventoryId = 0;

        public OCP_AbstractPlayerEntity(uint h)
        {
            handle = h;
        }

        public byte[] MakePayload()
        {
            MemoryStream m = new MemoryStream();

            // ---- AI_Entity::LoadFrom : handle (u32 BE) ----
            Helper.WriteU32LE(m, handle);

            // ================= Block1 (serialStruct1) : 12 fields =================
            // header: size=ceil(12/8)=2, size2=12, 2 zero mask bytes (all present)
            Helper.WriteU8(m, 2);
            Helper.WriteU8(m, 12);
            m.Write(new byte[2], 0, 2);

            Helper.WriteU32LE(m, 0);     //  1 m_DeathCount                u32 BE
            Helper.WriteU32LE(m, abilityInventoryId);     //  2 m_AbilityInventoryId        u32 BE
            Helper.WriteU32LE(m, passiveAbilityInventoryId); // 3 m_PassiveAbilityInventoryId u32 BE
            Helper.WriteU32LE(m, desiredWeaponMainInventoryId);    //  4 m_DesiredWeaponIds[Main]    u32 BE (3xU32)
            Helper.WriteU32LE(m, desiredWeaponPistolInventoryId);  //    m_DesiredWeaponIds[Pistol]  u32 BE
            Helper.WriteU32LE(m, desiredWeaponGrenadeInventoryId); //    m_DesiredWeaponIds[Grenade] u32 BE
            Helper.WriteU32LE(m, 0);     //  5 m_AchievementPoints         u32 BE
            Helper.WriteU32LE(m, helmetInventoryId);      //  6 m_HelmetInventoryId         u32 BE
            Helper.WriteU32LE(m, armorInventoryId);       //  7 m_ArmorTierInventoryId      u32 BE
            Helper.WriteU16LE(m, 0);     //  8 m_SpawnBlockingReasons      u16 BE =0 (deployable)
            Helper.WriteU8(m, classID);  //  9 m_Class                     u8
            Helper.WriteU16LE(m, 0);     // 10 m_ClassLevel                u16 BE
            Helper.WriteU32LE(m, 0);     // 11 m_PortraitId                u32 BE
            Helper.WriteU32(m, 0);       // 12 m_PersonaName length        u32 LE (=0 -> empty)

            // ================= Block2 (serialStruct2) : empty =================
            Helper.WriteU8(m, 0);        // size  = 0
            Helper.WriteU8(m, 0);        // size2 = 0

            // ================= Abstract tail =================
            Helper.WriteU8(m, 0);        // playerLocalIndex u8
            Helper.WriteU8(m, 0);        // padID            u8
            Helper.WriteU8(m, teamID);   // team             u8
            Helper.WriteU32LE(m, pid);   // pid              u32 BE
            Helper.WriteU32LE(m, dsGameMode); // ds_GameMode  u32 BE

            return m.ToArray();
        }
    }
}
