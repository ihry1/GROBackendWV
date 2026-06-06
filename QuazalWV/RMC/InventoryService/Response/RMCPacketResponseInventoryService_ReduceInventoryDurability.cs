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
    // GR5_DepletionSlot is 3x u32: { m_InventoryID, m_ParentID, m_Durability }. Decrement each
    // referenced item's durability (m_Durability = amount to subtract; inferred, clamped at 0),
    // then return the persona's updated bags.
    public class RMCPacketResponseInventoryService_ReduceInventoryDurability : RMCPResponse
    {
        public List<GR5_InventoryBag> bags = new List<GR5_InventoryBag>();

        public RMCPacketResponseInventoryService_ReduceInventoryDurability(QPacket p, RMCP rmc, ClientInfo client)
        {
            try
            {
                MemoryStream rm = new MemoryStream(p.payload);
                rm.Seek(rmc._afterProtocolOffset + 8, 0);
                uint count = Helper.ReadU32(rm);
                for (uint i = 0; i < count; i++)
                {
                    uint invId = Helper.ReadU32(rm);
                                 Helper.ReadU32(rm);   // m_ParentID (unused)
                    uint dur   = Helper.ReadU32(rm);
                    DBHelper.ReduceSlotDurabilityByInventoryId(client.PID, invId, dur);
                }
            }
            catch { }
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
