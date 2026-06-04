using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    class ClassInfo_Grenade
    {
        public byte memBufferSize;
        byte nbComponents;
        uint componentListID;
        uint weaponID;
        uint oasisNameID;
        List<uint> componentIds;
        byte someCount;

        public ClassInfo_Grenade(uint weaponID)
        {
            // Real map keys required (see ClassInfo_Gun). Using verified weapon 1000 data so the
            // slot-2 bag resolves and the spawn's weapon-load gate completes. memBufferSize is
            // recomputed by the caller (OCP_PlayerEntity) from the actual payload length.
            memBufferSize = 50;
            componentIds = new List<uint>() { 1, 4, 5, 6, 7, 8, 9 };
            nbComponents = (byte)componentIds.Count; // 7
            componentListID = 1000;
            this.weaponID = weaponID;
            oasisNameID = 70870;
            someCount = 0;
        }

        public byte[] MakePayload()
        {
            MemoryStream m = new MemoryStream();
            Helper.WriteU8(m, memBufferSize);
            Helper.WriteU8(m, nbComponents);
            Helper.WriteU32LE(m, componentListID);
            Helper.WriteU32LE(m, weaponID);
            Helper.WriteU32LE(m, oasisNameID);
            foreach (uint compId in componentIds) Helper.WriteU32LE(m, compId);
            Helper.WriteU8(m, someCount);
            return m.ToArray();
        }
    }
}