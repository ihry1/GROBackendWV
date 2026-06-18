using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    public class ClassInfo_Armor
    {
        public byte memBufferSize;
        uint armorItemId;
        byte camoId;
        byte pairCount;//max 6
        List<Tuple<uint, uint>> pairs;//thats another mistake but we dont send this yet, gonna correct anyway
        float bonusHealth;
        float bonusHealthRegen;
        float toughness;
        float criticalMitigation;
        //combat property modifiers
        byte nbModifiers;
        ushort bitmask;
        List<float> propertyList;

        public ClassInfo_Armor()
        {
            memBufferSize = 73;
            armorItemId = 1;//COM-01-Helm00
            camoId = 1;
            pairCount = 0;
            pairs = new List<Tuple<uint, uint>>();
            bonusHealth = 10.0f;
            bonusHealthRegen = 0.5f;
            toughness = 5.0f;
            criticalMitigation = 3.0f;
            nbModifiers = 12;
            bitmask = 0xFFFF;
            propertyList = new List<float>();
            // The 12 type-21 CombatProperty modifiers (client AI_EntityPlayer.propModifier_21_CombatProperties
            // @+0x4B8; idx 0 Mobility, 1/2/4 Damage/Crit, 3 Reticule, 5 WeaponSwap, 6 ReloadTime, 11 FallDamage...).
            // They are additive/fractional modifiers whose NEUTRAL identity is 0.0 (client applies (cp+1.0)x or
            // (1.0-cp)x base). This base ctor fills the NEUTRAL 0.0 for all 12 -- the safe DEFAULT/FALLBACK used
            // for the fake enemy, pid==0, or any DB miss. The (armorInventoryId, pid) overload below REPLACES
            // these with the player's REAL combat properties resolved from their equipped armor TIER (loadout/
            // inventory) via DBHelper.GetArmorCombatProperties.
            // (Historic bug: the original code sent the array INDEX as the value [0,1,..,11]; once the WriteFloatLE
            // endianness fix landed, index 5 arrived as a clean 5.0 -> (1.0-5.0) = -4.0 -> NEGATIVE weapon-swap
            // draw time -> the swap draw gesture played backward / never completed -> bIsSwitchingGuns stuck ->
            // combat locked to walk/run + the gun never swapped. See gro-combat-input-lock.)
            for (byte b = 0; b < nbModifiers; b++)
                propertyList.Add(0.0f);
        }

        public ClassInfo_Armor(uint armorInventoryId) : this()
        {
            if (armorInventoryId != 0) armorItemId = armorInventoryId;
        }

        // Per-persona overload: fills the 12 combat-property modifiers from the player's EQUIPPED armor tier
        // (loadout/inventory) instead of the neutral 0.0 placeholders. The DEDICATED SERVER opens the backend DB
        // read-only (same as ClassInfo_Gun), so this CAN query at spawn; DBHelper.GetArmorCombatProperties already
        // returns 12 zeros for pid==0 / a non-tier armor slot / the starter tier-1 / any DB hiccup, so this stays
        // a no-op (neutral) in every degraded case -- it can never re-introduce the negative-stretch combat lock.
        // nbModifiers (12), bitmask (0xFFFF), the payload size, and the WriteFloatLE serializer are all unchanged,
        // so the create-blob framing is byte-identical -- only the 12 float VALUES differ.
        public ClassInfo_Armor(uint armorInventoryId, uint pid) : this(armorInventoryId)
        {
            try
            {
                float[] cp = DBHelper.GetArmorCombatProperties(pid, armorInventoryId);
                if (cp != null && cp.Length == nbModifiers)
                {
                    propertyList = new List<float>(cp);
                    Log.WriteLine(1, "[DS] armor combat properties inv=" + armorInventoryId + " pid=" + pid +
                        " cp=[" + string.Join(",", propertyList) + "]", System.Drawing.Color.Lime);
                }
            }
            catch { }

            // Defensive scalars from the persona's EQUIPPED armor inserts (modtype-12). No inserts -> {0,0,0,0}
            // (the real "no bonus" baseline), which replaces the old 10/0.5/5/3 placeholders. nbModifiers/bitmask/
            // payload size are untouched, so framing stays byte-identical -- only these 4 float VALUES change.
            try
            {
                float[] sc = DBHelper.GetArmorScalars(pid, armorInventoryId);
                if (sc != null && sc.Length == 4)
                {
                    bonusHealth = sc[0];
                    bonusHealthRegen = sc[1];
                    toughness = sc[2];
                    criticalMitigation = sc[3];
                    Log.WriteLine(1, "[DS] armor inserts inv=" + armorInventoryId + " pid=" + pid +
                        " health=" + bonusHealth + " regen=" + bonusHealthRegen + " tough=" + toughness +
                        " crit=" + criticalMitigation, System.Drawing.Color.Lime);
                }
            }
            catch { }
        }

        public byte[] MakePayload()
        {
            MemoryStream m = new MemoryStream();
            Helper.WriteU8(m, memBufferSize);
            Helper.WriteU32LE(m, armorItemId);
            Helper.WriteU8(m, camoId);
            Helper.WriteU8(m, pairCount);
            if(pairCount>0)
            {
                foreach(Tuple<uint, uint> pair in pairs)
                {
                    Helper.WriteU32LE(m, pair.Item1);
                    Helper.WriteU32LE(m, pair.Item2);
                }
            }
            Helper.WriteFloatLE(m, bonusHealth);
            Helper.WriteFloatLE(m, bonusHealthRegen);
            Helper.WriteFloatLE(m, toughness);
            Helper.WriteFloatLE(m, criticalMitigation);
            Helper.WriteU8(m, nbModifiers);
            Helper.WriteU16(m, bitmask);
            // BE like the bonusHealth/toughness scalars above (the client reads this payload big-endian);
            // WriteFloat was little-endian -> byte-swapped modifiers. See ClassInfo_Ability for the proven case.
            foreach (float mod in propertyList) Helper.WriteFloatLE(m, mod);
            return m.ToArray();//73B
        }
    }
}
