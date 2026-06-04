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

        public ClassInfo_Gun(uint weaponID )
        {
            // bag.weaponID / componentListID / componentIds are MAP KEYS into the client's
            // WeaponsModel (served by the emulator's WeaponService 0x6B from DB tables
            // weapons/components/tempcomponentlists). They MUST be real or the async weapon
            // load never resolves and the spawn stalls/crashes. Verified starter "Test" weapon,
            // mapKey 1000 (in equipweaponids for all classes): compList 1000 = {1,4,5,6,7,8,9},
            // oasisNameID 70870. compCount must be > 0 (hard gate in AsyncLoadOneAdvancedWeapon).
            memBufferSize = 0;
            // M27 D10RS (Assault default rifle): real rate-of-fire (weapon prop 41) + tracer-speed (prop 84)
            // so tracers are fast/steady. The "Test" weapon 1000 (classType/weaponType 0) carries ~zero
            // values -> sporadic & slow tracers. Internally-consistent triple in database.sqlite.
            componentIds = new List<uint>() { 79, 171, 81, 82, 172, 84, 173, 86, 169 };
            nbComponents = (byte)componentIds.Count; // 9
            componentListID = 170;
            this.weaponID = weaponID; // pass 170 (mapKey)
            oasisNameID = 72925;
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