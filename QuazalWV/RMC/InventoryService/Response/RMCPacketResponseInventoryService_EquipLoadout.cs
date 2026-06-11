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
    // GR5_SwapSlot is 5x u32: { m_FromSlot, m_FromBagType, m_FromDurability, m_ToSlot, m_ToBagType }
    // (verified vs RE/ddl/structures_RDVDLL.json -- a slot->slot SWAP, not {newItem,targetSlot}).
    // Persist each swap by exchanging the inventoryid held in the two (bag,slot) cells, then return
    // the persona's now-current bags (empty remove list).
    public class RMCPacketResponseInventoryService_EquipLoadout : RMCPResponse
    {
        public List<GR5_InventoryBag> removeBags = new List<GR5_InventoryBag>();
        public List<GR5_InventoryBag> bags = new List<GR5_InventoryBag>();

        public RMCPacketResponseInventoryService_EquipLoadout(QPacket p, RMCP rmc, ClientInfo client)
        {
            try
            {
                MemoryStream rm = new MemoryStream(p.payload);
                rm.Seek(rmc._afterProtocolOffset + 8, 0);
                uint count = Helper.ReadU32(rm);
                for (uint i = 0; i < count; i++)
                {
                    uint fromSlot = Helper.ReadU32(rm);
                    uint fromBag  = Helper.ReadU32(rm);
                                    Helper.ReadU32(rm);   // m_FromDurability (unused)
                    uint toSlot   = Helper.ReadU32(rm);
                    uint toBag    = Helper.ReadU32(rm);
                    // ★CLASS-TO-SPAWN FIX (2026-06-09): the loadout bag being edited (4=Assault/5=Recon/6=Specialist)
                    // IS the active class. doc-16/17 wired the class-persist to InventoryService method 17
                    // (EquipPlayerWithLoadoutKit), but the live client NEVER sends 17 -- it edits loadouts via THIS
                    // method 8 (EquipLoadout) (confirmed: backend log shows method 8 firing repeatedly, 17 never;
                    // personas.lastusedcid stayed 0 so GetSpawnLoadout always resolved Assault). Persist lastusedcid
                    // from the loadout bagtype so the spawn resolves the SELECTED class.
                    uint _loadoutBag = (toBag >= 4 && toBag <= 6) ? toBag : ((fromBag >= 4 && fromBag <= 6) ? fromBag : 0);
                    if (_loadoutBag != 0)
                    {
                        DBHelper.SetSelectedClass(client.PID, _loadoutBag - 4);
                        Log.WriteLine(1, "[InventoryService] EquipLoadout -> persist active class=" + (_loadoutBag - 4) + " (loadoutBag=" + _loadoutBag + ") pid=" + client.PID);
                    }
                    int fromBagId = DBHelper.GetBagId(client.PID, fromBag);
                    int toBagId   = DBHelper.GetBagId(client.PID, toBag);
                    if (fromBagId < 0 || toBagId < 0) continue;
                    uint fromInv = DBHelper.GetSlotInventoryId(fromBagId, fromSlot);
                    uint toInv   = DBHelper.GetSlotInventoryId(toBagId, toSlot);
                    DBHelper.SetSlotInventoryId(toBagId, toSlot, fromInv);
                    DBHelper.SetSlotInventoryId(fromBagId, fromSlot, toInv);
                }
            }
            catch { }
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
