using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    public static class InventoryService
    {
        // InventoryService (RMC protocol 0x69, 19 methods). See RE/protocols/69_GR5_InventoryService.md.
        //
        // Methods 1,2,3,4,6,16 = lobby bootstrap fetches (verified in-game working).
        //
        // The rest are issued by the inventory MENU / character-select as FIRE-AND-FORGET sends (the
        // client updates its UI optimistically and does not block on the reply). Before this change they
        // fell to the default case = NO response was sent, so on the next full inventory refresh the
        // server's unchanged state snapped equipped/moved gear back ("gear won't stick"). Each method
        // now returns a wire-valid reply. Methods marked MINIMAL below send the correct payload SHAPE
        // but do not yet PERSIST the mutation to the DB -- see each response class + AGENT_HANDOFF.md
        // for the real DB-mutation follow-up. Methods 5,7,14,18,19 have no observed client call site
        // (client reads inventory from local cache) and are wired defensively for completeness.
        public static void HandleInventoryServiceRequest(QPacket p, RMCP rmc, ClientInfo client)
        {
            RMCPResponse reply;
            switch (rmc.methodID)
            {
                case 1: // GetTemplateItems
                    reply = new RMCPacketResponseInventoryService_GetTemplateItems();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 2: // GetAllBoosts
                    reply = new RMCPacketResponseInventoryService_GetAllBoosts();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 3: // GetAllConsumables
                    reply = new RMCPacketResponseInventoryService_GetAllConsumables();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 4: // GetAllApplyItems
                    reply = new RMCPacketResponseInventoryService_GetAllApplyItems();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 5: // GetUserInventory (defensive: no observed client call) -> full inventory triple
                    reply = new RMCPacketResponseInventoryService_FullUserInventory(client);
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 6: // GetUserInventoryByBagType (load-bearing user-inventory fetch)
                    reply = new RMCPacketResponseInventoryService_GetUserInventoryByBagType(client);
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 7: // GetUserInventoryByInventoryIds (defensive) -> full inventory triple
                    reply = new RMCPacketResponseInventoryService_FullUserInventory(client);
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 8: // EquipLoadout (drag-equip into a loadout slot) -- MINIMAL (no DB swap yet)
                    reply = new RMCPacketResponseInventoryService_EquipLoadout(client);
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 9: // SellUserItem -- void return; success ACK closes the sell transaction
                    reply = new RMCPResponseEmpty();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 10: // BuyBackUserItem -- MINIMAL (echoes slot)
                    {
                        uint slotID = ReadRequestU32(p, rmc, 0);
                        reply = new RMCPacketResponseInventoryService_BuyBackUserItem(slotID);
                        RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    }
                    break;
                case 11: // UpdateLobbyInventoryBag -- MINIMAL (echoes slots, no DB move yet)
                    {
                        uint fromSlotID = ReadRequestU32(p, rmc, 0);
                        uint toSlotID = ReadRequestU32(p, rmc, 1);
                        reply = new RMCPacketResponseInventoryService_UpdateLobbyInventoryBag(fromSlotID, toSlotID);
                        RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    }
                    break;
                case 12: // ReduceInventoryDurability (match teardown) -- MINIMAL (no DB depletion yet)
                    reply = new RMCPacketResponseInventoryService_ReduceInventoryDurability(client);
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 13: // ApplyItem -- void return; success ACK = apply ok
                    reply = new RMCPResponseEmpty();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 14: // ExpireItems (defensive) -> empty (nothing expired)
                    reply = new RMCPacketResponseInventoryService_ExpireItems();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 15: // RetrieveInventoryFromMailBag -> empty (nothing retrieved)
                    reply = new RMCPacketResponseInventoryService_RetrieveInventoryFromMailBag();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 16: // GetAllDefaultLoadoutKits
                    reply = new RMCPacketResponseInventoryService_GetAllDefaultLoadoutKits(client);
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 17: // EquipPlayerWithLoadoutKit (char-select kit) -- returns full inv; real kit-apply TODO
                    reply = new RMCPacketResponseInventoryService_FullUserInventory(client);
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 18: // GetPersonaInventoryWithAddOns (defensive) -> triple + add-on maps
                    reply = new RMCPacketResponseInventoryService_GetPersonaInventoryWithAddOns(client);
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 19: // RemoveSlotFromInventory (defensive) -- void return
                    reply = new RMCPResponseEmpty();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                default:
                    Log.WriteLine(1, "[RMC InventoryService] Error: Unknown Method 0x" + rmc.methodID.ToString("X"));
                    break;
            }
        }

        // Read the Nth (0-based) uint32 request parameter. InventoryService requests are not parsed by
        // RMC.ProcessRequest (it's in the no-op group), so params live in the raw payload right after
        // callID+methodID, i.e. at _afterProtocolOffset + 8. Used by the methods that take uint args.
        private static uint ReadRequestU32(QPacket p, RMCP rmc, int index)
        {
            try
            {
                MemoryStream m = new MemoryStream(p.payload);
                m.Seek(rmc._afterProtocolOffset + 8 + index * 4, 0);
                return Helper.ReadU32(m);
            }
            catch { return 0; }
        }
    }
}
