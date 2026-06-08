using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    public class RMCPacketResponseInventoryService_GetUserInventoryByBagType : RMCPResponse
    {
        public List<GR5_UserItem> items = new List<GR5_UserItem>();
        public List<GR5_InventoryBag> bags = new List<GR5_InventoryBag>();
        public List<GR5_WeaponConfiguration> weaponConfig = new List<GR5_WeaponConfiguration>();

        public RMCPacketResponseInventoryService_GetUserInventoryByBagType(ClientInfo client)
        {
            // Return the FULL inventory (all items + all bags) so EVERY loadout-bag slot's
            // InventoryID resolves on the client. The old code filtered useritems by
            // itemtype == bagType (conflating item-type with bag-type) AND parsed the bag type
            // from raw payload byte offsets, so weapons (itemtype 2) were never returned for a
            // loadout-bag (bagtype 4/5/6) request -> weapon slots had no resolvable item ->
            // spinning/loading weapon icons + empty inventory.
            items = DBHelper.GetAllUserItems(client.PID);
            bags = DBHelper.GetAllInventoryBags(client.PID);
            // Populate weaponConfigurations: the per-USER weapon component-list map. The inventory
            // weapon tile resolves its (otherwise endlessly spinning) 3D preview via
            // WeaponsModel.GetUserWeapon(InventoryID) -> this map. The old empty stub left each owned
            // weapon's InventoryID unmapped -> no component list -> no weapon model -> the perpetual
            // loading spinner. Map each owned weapon's InventoryID -> its component list
            // (tempcomponentlists, keyed by mapKey == the weapon's ItemID; e.g. 170=M27, 339=P250).
            // Per-instance CUSTOM component list when the weapon has been customized (StoreService 22/23
            // persisted it, keyed by InventoryID), else the by-mapKey default -> the tile/3D-preview shows
            // the player's chosen parts and they survive relogin.
            foreach (GR5_UserItem ui in items)
                if (ui.ItemType == 2)
                    weaponConfig.Add(new GR5_WeaponConfiguration { unk1 = ui.InventoryID, unk2 = DBHelper.GetWeaponComponentListForInstance(client.PID, ui.InventoryID, ui.ItemID) });
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
            return "[RMCPacketResponseInventoryService_GetUserInventoryByBagType]";
        }

        public override string PayloadToString()
        {
            return "";
        }
    }
}
