using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QuazalWV
{
    public class ClassInfo_PassiveAbility
    {
        public byte memBufferSize;
        byte passiveAbilityId;

        //base passive ability modifiers
        const byte nbBaseModifiers = 2;
        const byte baseModBitmask = 0xFF;
        float teamSharingRadius;
        float partySharingRadius;

        //specific passive ability modifiers
        byte nbSpecificModifiers;
        const byte specificModBitmask = 0xFF;

        public ClassInfo_PassiveAbility(byte passiveAbilityId)
        {
            this.passiveAbilityId = passiveAbilityId;
            teamSharingRadius = 50f;
            partySharingRadius = 40f;
            switch(passiveAbilityId)
            {
                case 0:
                    memBufferSize = 21;
                    nbSpecificModifiers = 2;
                    break;
                default:
                    memBufferSize = 17;
                    nbSpecificModifiers = 1;
                    break;
            }
        }

        public byte[] MakePayload()
        {
            MemoryStream m = new MemoryStream();
            Helper.WriteU8(m, memBufferSize);
            Helper.WriteU8(m, passiveAbilityId);

            // modifier floats are BE (WriteFloatLE) — same modifier-list structure the client reads big-endian
            // in ClassInfo_Ability (proven via the GRO_Hook probe); WriteFloat was little-endian -> byte-swapped.
            //base passive ability modifier list (14)
            Helper.WriteU8(m, nbBaseModifiers);
            Helper.WriteU8(m, baseModBitmask);
            Helper.WriteFloatLE(m,teamSharingRadius);
            Helper.WriteFloatLE(m,partySharingRadius);
            //specific passive ability modifier list (15-20)
            Helper.WriteU8(m, nbSpecificModifiers);
            Helper.WriteU8(m, specificModBitmask);
            switch(passiveAbilityId)
            {
                //eAmmoSupplierModifiable
                case 0:
                    float ammoRegenInterval = 5f;
                    float ammoRegenPercentage = 35f;
                    Helper.WriteFloatLE(m,ammoRegenInterval);
                    Helper.WriteFloatLE(m,ammoRegenPercentage);
                    break;
                //eEnergySupplierModifiable
                case 1:
                    float energyRegenRate = 5f;
                    Helper.WriteFloatLE(m,energyRegenRate);
                    break;
                //eShootDetectionModifiable
                case 2:
                    float shootDetectionRadius = 50f;
                    Helper.WriteFloatLE(m,shootDetectionRadius);
                    break;
                //eHardenModifiable
                case 3:
                    float armorBoostRate = 15f;
                    Helper.WriteFloatLE(m,armorBoostRate);
                    break;
                //eHealthRegenModifiable
                case 4:
                    float healthRegenRate = 5f;
                    Helper.WriteFloatLE(m,healthRegenRate);
                    break;
                //eMoveDetectionModifiable
                case 5:
                    float moveDetectionRadius = 40f;
                    Helper.WriteFloatLE(m,moveDetectionRadius);
                    break;
            }
            return m.ToArray();
        }
    }
}
