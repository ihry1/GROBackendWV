using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    // Shared response for every InventoryService method whose reply is the standard
    // "whole inventory" triple:  qvector<GR5_UserItem> + qvector<GR5_InventoryBag> +
    // std_map<uint32, qvector<uint32>> weaponConfigurations.
    //
    // Used by:
    //   - method  5  GetUserInventory               (req: _PersonaIDList   -- ignored, we serve the caller)
    //   - method  7  GetUserInventoryByInventoryIds (req: _PersonaID,_InventoryIdList -- ignored, full set returned)
    //   - method 17  EquipPlayerWithLoadoutKit      (req: _LoadoutKitID    -- see note below)
    //
    // The payload shape is byte-identical to the (verified, in-game working) method 6
    // GetUserInventoryByBagType reply, so we reuse the exact same build + serialization here.
    // Returning the FULL inventory (all items + all bags) guarantees every loadout-bag slot's
    // InventoryID resolves on the client, and weaponConfigurations maps each owned weapon's
    // InventoryID -> its component list so the weapon tile's 3D preview resolves (no spinner).
    //
    // NOTE on method 17: a fully-correct EquipPlayerWithLoadoutKit would APPLY the chosen kit to
    // the persona's loadout bags in the DB first, then return the mutated inventory. This minimal
    // implementation skips the mutation and returns the current inventory, which unblocks the menu
    // (previously the call got NO response -> the kit-select hung). The real kit-apply is a
    // documented follow-up (see AGENT_HANDOFF.md): map GR5_LoadoutKit slots -> bag-slot-list ids
    // (0->1 primary,4->2 secondary,3->3 armor,2->9 ability,6->10 passive,5->8 helmet-fam,7->4 helmet)
    // and UPDATE inventorybagslots for the loadout bag, then return the refreshed bags.
    public class RMCPacketResponseInventoryService_FullUserInventory : RMCPResponse
    {
        public List<GR5_UserItem> items = new List<GR5_UserItem>();
        public List<GR5_InventoryBag> bags = new List<GR5_InventoryBag>();
        public List<GR5_WeaponConfiguration> weaponConfig = new List<GR5_WeaponConfiguration>();

        public RMCPacketResponseInventoryService_FullUserInventory(ClientInfo client)
        {
            items = DBHelper.GetAllUserItems(client.PID);
            bags = DBHelper.GetAllInventoryBags(client.PID);
            foreach (GR5_UserItem ui in items)
                if (ui.ItemType == 2)
                    weaponConfig.Add(new GR5_WeaponConfiguration { unk1 = ui.InventoryID, unk2 = DBHelper.GetWeaponComponentList(ui.ItemID) });
        }

        public override byte[] ToBuffer()
        {
            MemoryStream m = new MemoryStream();
            Helper.WriteU32(m, (uint)items.Count);
            foreach (GR5_UserItem c in items)
                c.toBuffer(m);
            Helper.WriteU32(m, (uint)bags.Count);
            foreach (GR5_InventoryBag c in bags)
                c.toBuffer(m);
            Helper.WriteU32(m, (uint)weaponConfig.Count);
            foreach (GR5_WeaponConfiguration u in weaponConfig)
                u.toBuffer(m);
            return m.ToArray();
        }

        public override string ToString()
        {
            return "[RMCPacketResponseInventoryService_FullUserInventory]";
        }

        public override string PayloadToString()
        {
            return "";
        }
    }
}
