using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    // InventoryService method 8 EquipLoadout.
    //   Request : qvector<GR5_SwapSlot> _SwapSlotList   (the in-lobby slot swaps the player made)
    //   Response: qvector<GR5_InventoryBag> _RemoveBagList
    //             qvector<GR5_InventoryBag> _InventoryBagList
    //
    // MINIMAL: this does NOT yet apply the swaps to the DB. It returns an empty _RemoveBagList and
    // the persona's CURRENT bags as _InventoryBagList. That makes the call respond (it previously got
    // NO response -> the equip hung), but the swap is not persisted. Real implementation: parse the
    // GR5_SwapSlot list from the request, UPDATE inventorybagslots accordingly, then return the
    // mutated bags (and any emptied bags in _RemoveBagList). See AGENT_HANDOFF.md.
    public class RMCPacketResponseInventoryService_EquipLoadout : RMCPResponse
    {
        public List<GR5_InventoryBag> removeBags = new List<GR5_InventoryBag>();
        public List<GR5_InventoryBag> bags = new List<GR5_InventoryBag>();

        public RMCPacketResponseInventoryService_EquipLoadout(ClientInfo client)
        {
            bags = DBHelper.GetAllInventoryBags(client.PID);
        }

        public override byte[] ToBuffer()
        {
            MemoryStream m = new MemoryStream();
            Helper.WriteU32(m, (uint)removeBags.Count);
            foreach (GR5_InventoryBag c in removeBags)
                c.toBuffer(m);
            Helper.WriteU32(m, (uint)bags.Count);
            foreach (GR5_InventoryBag c in bags)
                c.toBuffer(m);
            return m.ToArray();
        }

        public override string ToString() { return "[RMCPacketResponseInventoryService_EquipLoadout]"; }
        public override string PayloadToString() { return ""; }
    }
}
