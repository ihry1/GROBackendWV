using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    // InventoryService method 10 BuyBackUserItem.
    //   Request : uint32 _SlotID          (scrap-yard slot the player buys back from)
    //   Response: uint32 _BagSlotId, uint32 _BagType   (where the item landed in the inventory)
    //
    // MINIMAL: does NOT yet move an item out of the scrap yard. Echoes the requested slot id as
    // _BagSlotId and 0 as _BagType so the call responds (previously: no response -> buyback hung).
    // Real impl: re-add the scrap-yard item to the general inventory bag and return its real slot/bag.
    public class RMCPacketResponseInventoryService_BuyBackUserItem : RMCPResponse
    {
        public uint bagSlotId;
        public uint bagType;

        public RMCPacketResponseInventoryService_BuyBackUserItem(uint slotID)
        {
            bagSlotId = slotID;
            bagType = 0;
        }

        public override byte[] ToBuffer()
        {
            MemoryStream m = new MemoryStream();
            Helper.WriteU32(m, bagSlotId);
            Helper.WriteU32(m, bagType);
            return m.ToArray();
        }

        public override string ToString() { return "[RMCPacketResponseInventoryService_BuyBackUserItem]"; }
        public override string PayloadToString() { return ""; }
    }
}
