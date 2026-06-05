using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    // InventoryService method 12 ReduceInventoryDurability.
    //   Request : qvector<GR5_DepletionSlot> _DurabilityDepletionList
    //   Response: qvector<GR5_InventoryBag> _InventoryBagList
    //
    // MINIMAL: does NOT yet decrement durability in the DB. Returns the persona's current bags so the
    // client gets a valid reply (previously: no response). Real impl: for each depletion slot, lower
    // inventorybagslots.durability, then return the updated bags. See AGENT_HANDOFF.md.
    public class RMCPacketResponseInventoryService_ReduceInventoryDurability : RMCPResponse
    {
        public List<GR5_InventoryBag> bags = new List<GR5_InventoryBag>();

        public RMCPacketResponseInventoryService_ReduceInventoryDurability(ClientInfo client)
        {
            bags = DBHelper.GetAllInventoryBags(client.PID);
        }

        public override byte[] ToBuffer()
        {
            MemoryStream m = new MemoryStream();
            Helper.WriteU32(m, (uint)bags.Count);
            foreach (GR5_InventoryBag c in bags)
                c.toBuffer(m);
            return m.ToArray();
        }

        public override string ToString() { return "[RMCPacketResponseInventoryService_ReduceInventoryDurability]"; }
        public override string PayloadToString() { return ""; }
    }
}
