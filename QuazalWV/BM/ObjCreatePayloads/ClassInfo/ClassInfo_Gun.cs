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
            // STATIC per-weapon data — deliberately NO DB query. This constructor runs on the
            // DEDICATED SERVER while building the spawn create-blob, and the DS process has an
            // EMPTY database.sqlite (0 bytes). Any DB query here throws "no such table" and crashes
            // the spawn -> client gets "lost connection to the game server". A real per-weapon lookup
            // belongs on the backend via FetchSessionPlayerData, not in the DS create-blob path.
            // bag.weaponID / componentListID / componentIds are MAP KEYS into the client's WeaponsModel
            // (served by the backend's WeaponService 0x6B). componentListID == weapons.mapKey.
            memBufferSize = 0;
            this.weaponID = weaponID;
            switch (weaponID)
            {
                case 339: // P250 (secondary pistol)
                    componentListID = 339;
                    oasisNameID = 70920;
                    componentIds = new List<uint>() { 10414, 340, 10416 };
                    break;
                default:  // M27 D10RS (primary) + safe fallback for any other id
                    this.weaponID = 170;
                    componentListID = 170;
                    oasisNameID = 72925;
                    // 11004 = the added Burst (type 17) fire-mode component. The in-match weapon is built
                    // ONLY from this explicit list (AsyncLoadOneAdvancedWeapon resolves each mapKey via
                    // WeaponsModel->GetComponent + SetComponent into componentList[type]); it does NOT read
                    // tempcomponentlists, so an appended fire-mode component must be listed here to appear.
                    componentIds = new List<uint>() { 79, 171, 81, 82, 172, 84, 11004, 173, 86, 169 };
                    break;
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