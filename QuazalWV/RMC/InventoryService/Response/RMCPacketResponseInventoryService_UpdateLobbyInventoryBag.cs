using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    // InventoryService method 11 UpdateLobbyInventoryBag.
    //   Request : uint32 _FromSlotID, uint32 _ToSlotID, uint32 _ItemCount
    //   Response: GR5_InventoryBagSlot _FromSlot, GR5_InventoryBagSlot _ToSlot
    //
    // Called on a lobby-bag move/reorder and on the drag-equip "overflow" path (an equipped item gets
    // bumped back to the lobby bag). The client send is fire-and-forget (UI already updated
    // optimistically), so this response only needs to be wire-valid to avoid an "unknown method" gap.
    //
    // MINIMAL: echoes the requested slot ids back (InventoryID/Durability 0). It does NOT persist the
    // move to inventorybagslots, so the move reverts on the next full inventory refresh. Real impl:
    // swap inventoryid/durability between the two inventorybagslots rows, then return their new values.
    public class RMCPacketResponseInventoryService_UpdateLobbyInventoryBag : RMCPResponse
    {
        public GR5_InventoryBagSlot fromSlot = new GR5_InventoryBagSlot();
        public GR5_InventoryBagSlot toSlot = new GR5_InventoryBagSlot();

        public RMCPacketResponseInventoryService_UpdateLobbyInventoryBag(uint fromSlotID, uint toSlotID)
        {
            fromSlot.SlotID = fromSlotID;
            toSlot.SlotID = toSlotID;
        }

        public override byte[] ToBuffer()
        {
            MemoryStream m = new MemoryStream();
            fromSlot.toBuffer(m);
            toSlot.toBuffer(m);
            return m.ToArray();
        }

        public override string ToString() { return "[RMCPacketResponseInventoryService_UpdateLobbyInventoryBag]"; }
        public override string PayloadToString() { return ""; }
    }
}
