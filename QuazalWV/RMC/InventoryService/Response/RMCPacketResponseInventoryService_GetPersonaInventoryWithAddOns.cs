using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    // InventoryService method 18 GetPersonaInventoryWithAddOns.
    //   Request : uint32 _PersonaID  (ignored -- we serve the calling client's persona)
    //   Response: qvector<GR5_UserItem> _UserItemVector
    //             qvector<GR5_InventoryBag> _InventoryBagVector
    //             std_map<uint32,qvector<uint32>> _PersonaArmorInserts
    //             std_map<uint32,qvector<uint32>> _PersonaWeaponBridges
    //             std_map<uint32,qvector<uint32>> _PersonaAbilityUpgrades
    //
    // The first two fields are the same full item/bag set as method 6 (so the inspect/persona view
    // resolves every slot). _PersonaWeaponBridges is populated with the same per-weapon component
    // map as weaponConfigurations (InventoryID -> component list) so weapon previews resolve.
    // The armor-insert and ability-upgrade maps are returned EMPTY for now (wire-valid: count 0) --
    // populating them needs the persona armorinsertslots / abilityupgradeslots join (TODO, see
    // AGENT_HANDOFF.md). Empty maps simply mean "no add-ons", which is correct for the seed data.
    public class RMCPacketResponseInventoryService_GetPersonaInventoryWithAddOns : RMCPResponse
    {
        public List<GR5_UserItem> items = new List<GR5_UserItem>();
        public List<GR5_InventoryBag> bags = new List<GR5_InventoryBag>();
        public List<GR5_WeaponConfiguration> armorInserts = new List<GR5_WeaponConfiguration>();
        public List<GR5_WeaponConfiguration> weaponBridges = new List<GR5_WeaponConfiguration>();
        public List<GR5_WeaponConfiguration> abilityUpgrades = new List<GR5_WeaponConfiguration>();

        public RMCPacketResponseInventoryService_GetPersonaInventoryWithAddOns(ClientInfo client)
        {
            items = DBHelper.GetAllUserItems(client.PID);
            bags = DBHelper.GetAllInventoryBags(client.PID);
            foreach (GR5_UserItem ui in items)
                if (ui.ItemType == 2)
                    weaponBridges.Add(new GR5_WeaponConfiguration { unk1 = ui.InventoryID, unk2 = DBHelper.GetWeaponComponentList(ui.ItemID) });
        }

        // A std_map<uint32, qvector<uint32>> serializes exactly like the weaponConfigurations map:
        // count, then per entry { key, qvector<uint32> }. GR5_WeaponConfiguration is that entry shape.
        private static void WriteMap(Stream m, List<GR5_WeaponConfiguration> map)
        {
            Helper.WriteU32(m, (uint)map.Count);
            foreach (GR5_WeaponConfiguration e in map)
                e.toBuffer(m);
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
            WriteMap(m, armorInserts);
            WriteMap(m, weaponBridges);
            WriteMap(m, abilityUpgrades);
            return m.ToArray();
        }

        public override string ToString()
        {
            return "[RMCPacketResponseInventoryService_GetPersonaInventoryWithAddOns]";
        }

        public override string PayloadToString()
        {
            return "";
        }
    }
}
