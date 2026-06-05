using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    // InventoryService method 14 ExpireItems.
    //   Request : qvector<uint32> _InventoryIdList
    //   Response: qvector<GR5_InventoryBag> _ExpiredItemList
    //
    // Returns an EMPTY list = "nothing expired", which is correct for the seed inventory (items have
    // no expiry). Wire-valid reply so the call no longer hangs. If timed/rental items are added later,
    // populate the expired bags here.
    public class RMCPacketResponseInventoryService_ExpireItems : RMCPResponse
    {
        public List<GR5_InventoryBag> expired = new List<GR5_InventoryBag>();

        public override byte[] ToBuffer()
        {
            MemoryStream m = new MemoryStream();
            Helper.WriteU32(m, (uint)expired.Count);
            foreach (GR5_InventoryBag c in expired)
                c.toBuffer(m);
            return m.ToArray();
        }

        public override string ToString() { return "[RMCPacketResponseInventoryService_ExpireItems]"; }
        public override string PayloadToString() { return ""; }
    }
}
