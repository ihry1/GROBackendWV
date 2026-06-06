using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    public class ClassInfo_Gun
    {
        public byte memBufferSize;
        byte nbComponents;//+1
        uint componentListID;//+4
        uint weaponID;//4
        uint oasisNameID;//4
        List<uint> componentIds;

        public ClassInfo_Gun(uint weaponID)
        {
            // Builds the spawn create-blob's weapon bag. The DEDICATED SERVER now opens the BACKEND's
            // live database read-only (GRODedicatedServerWV Form1 -> DBHelper.Init), so this CAN query
            // per-weapon data -- it previously could not (the DS's own database.sqlite is 0 bytes, and a
            // query threw "no such table" -> spawn crash -> "lost connection to the game server").
            // bag.weaponID / componentListID / componentIds are MAP KEYS into the client's WeaponsModel
            // (served by WeaponService 0x6B); componentListID == weapons.mapKey. Build ANY of the 66
            // weapons from its real component list. Guarded with an M27 fallback so a missing weapon or a
            // DB hiccup degrades gracefully instead of crashing the spawn.
            memBufferSize = 0;
            this.weaponID = weaponID;
            componentListID = weaponID;
            try
            {
                componentIds = DBHelper.GetWeaponComponentList(weaponID);   // tempcomponentlists[mapKey]
                oasisNameID = DBHelper.GetItemOasisName(weaponID);          // templateitems[iid].oname
            }
            catch { componentIds = null; }
            if (componentIds == null || componentIds.Count == 0)
            {
                // M27 D10RS fallback (its tempcomponentlists already includes the burst 11004).
                this.weaponID = 170; componentListID = 170; oasisNameID = 72925;
                componentIds = new List<uint>() { 79, 171, 81, 82, 172, 84, 11004, 173, 86, 169 };
            }
            nbComponents = (byte)componentIds.Count;
        }

        public byte[] MakePayload()
        {
            MemoryStream m = new MemoryStream();
            Helper.WriteU8(m, memBufferSize);
            Helper.WriteU8(m, nbComponents);
            Helper.WriteU32LE(m, componentListID);
            Helper.WriteU32LE(m, weaponID);
            Helper.WriteU32LE(m, oasisNameID);
            foreach(uint compId in componentIds) Helper.WriteU32LE(m, compId);
            return m.ToArray();//49B
        }
    }
}