using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    // InventoryService method 15 RetrieveInventoryFromMailBag.
    //   Request : qvector<GR5_MailRetrievalRequest> retrievalList
    //   Response: qvector<GR5_UserItem> userItems
    //             qvector<GR5_InventoryBagSlot> slots
    //
    // Returns EMPTY lists = "nothing retrieved from mail". Wire-valid so the call responds instead of
    // hanging. Real impl (when a mail/gift bag exists): move the requested mail items into the
    // inventory and return the new user items + their slots.
    public class RMCPacketResponseInventoryService_RetrieveInventoryFromMailBag : RMCPResponse
    {
        public List<GR5_UserItem> userItems = new List<GR5_UserItem>();
        public List<GR5_InventoryBagSlot> slots = new List<GR5_InventoryBagSlot>();

        public override byte[] ToBuffer()
        {
            MemoryStream m = new MemoryStream();
            Helper.WriteU32(m, (uint)userItems.Count);
            foreach (GR5_UserItem c in userItems)
                c.toBuffer(m);
            Helper.WriteU32(m, (uint)slots.Count);
            foreach (GR5_InventoryBagSlot c in slots)
                c.toBuffer(m);
            return m.ToArray();
        }

        public override string ToString() { return "[RMCPacketResponseInventoryService_RetrieveInventoryFromMailBag]"; }
        public override string PayloadToString() { return ""; }
    }
}
