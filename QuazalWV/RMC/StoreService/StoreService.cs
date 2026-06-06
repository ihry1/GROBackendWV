using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Collections.Generic;
using QuazalWV.DB;

namespace QuazalWV
{
    public static class StoreService
    {
        public static void ProcessStoreServiceRequest(Stream s, RMCP rmc)
        {
            switch (rmc.methodID)
            {
                case 1:
                    break;
                case 8:
                    break;
                case 9:
                    break;
                case 11:
                    break;
                case 17:
                    rmc.request = new RMCPacketRequestStoreService_InitiateBuyItem(s);
                    break;
                case 18:
                    rmc.request = new RMCPacketRequestStoreService_CompleteBuyItem(s);
                    break;
                case 20:
                    rmc.request = new RMCPacketRequestStoreService_InitiateBuyWeaponAndAttachComponents(s);
                    break;
                case 21:
                    rmc.request = new RMCPacketRequestStoreService_CompleteBuyWeaponAndAttachComponents(s);
                    break;
                case 22: // InitiateBuyAndAttachComponents (re-attach components to an OWNED weapon)
                    rmc.request = new RMCPacketRequestStoreService_InitiateBuyAndAttachComponents(s);
                    break;
                case 23: // CompleteBuyAndAttachComponents (TransactionId only -- reuse method-21's parser)
                    rmc.request = new RMCPacketRequestStoreService_CompleteBuyWeaponAndAttachComponents(s);
                    break;
                case 26:
                    rmc.request = new RMCPacketRequestStoreService_InitiateBuyAbilityWithUpgrades(s);
                    break;
                case 27:
                    rmc.request = new RMCPacketRequestStoreService_CompleteBuyAbilityWithUpgrades(s);
                    break;
                case 30:
                    rmc.request = new RMCPacketRequestStoreService_InitiateBuyArmourAndAttachInserts(s);
                    break;
                case 31:
                    rmc.request = new RMCPacketRequestStoreService_CompleteBuyArmourAndAttachInserts(s);
                    break;
                default:
                    // CUSTOMIZE-CAPTURE: dump the raw body so an unrecognized customize/store apply
                    // reveals its exact method id + bytes on the first in-game test.
                    Log.WriteLine(1, "[RMC Store][CAPTURE] Unparsed Method 0x" + rmc.methodID.ToString("X") + " body=" + HexRest(s), Color.Orange);
                    break;
            }
        }

        public static void HandleStoreServiceRequest(QPacket p, RMCP rmc, ClientInfo client)
        {
            RMCPResponse reply;
            uint trId = 0;
            switch (rmc.methodID)
            {
                case 1:
                    reply = new RMCPacketResponseStoreService_GetSKUs();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 8:
                    reply = new RMCPacketResponseStoreService_EnterCoupons();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 9:
                    reply = new RMCPResponseEmpty();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 11:
                    reply = new RMCPacketResponseStoreService_GetShoppingDetails();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 17:
                    var buyItemInitReq = (RMCPacketRequestStoreService_InitiateBuyItem)rmc.request;
                    trId = TransactionModel.SaveTransaction(
                        client.PID,
                        buyItemInitReq.CartItems[0].SkuId,
                        TransactionType.BuyItem,
                        buyItemInitReq.CartItems[0].VirtualCurrencyType
                    );
                    reply = new RMCPacketResponseStoreService_InitiateBuyItem(trId);
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    // send complete transaction notif on success
                    if (trId > 0)
                        SendCompleteNotif(client, trId);
                    break;
                case 18:
                    var buyItemComplReq = (RMCPacketRequestStoreService_CompleteBuyItem)rmc.request;
                    TransactionModel.CompleteTransaction(buyItemComplReq.TransactionId);
                    reply = new RMCPacketResponseStoreService_CompleteBuyItem();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 20:
                    var buyWeapInitReq = (RMCPacketRequestStoreService_InitiateBuyWeaponAndAttachComponents)rmc.request;
                    trId = TransactionModel.SaveTransaction(
                        client.PID, 
                        buyWeapInitReq.WeaponSkuData.SkuId,
                        TransactionType.BuyWeaponAndAttachComponents,
                        buyWeapInitReq.WeaponSkuData.VirtualCurrencyType
                    );
                    reply = new RMCPacketResponseStoreService_InitiateBuyWeaponAndAttachComponents(trId);
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    // send complete transaction notif on success
                    if (trId > 0)
                        SendCompleteNotif(client, trId);
                    break;
                case 21:
                    var buyWeapComplReq = (RMCPacketRequestStoreService_CompleteBuyWeaponAndAttachComponents)rmc.request;
                    TransactionModel.CompleteTransaction(buyWeapComplReq.TransactionId);
                    reply = new RMCPacketResponseStoreService_CompleteBuyWeaponAndAttachComponents();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 22: // InitiateBuyAndAttachComponents -- the customize "apply" on an OWNED weapon
                    {
                        var attachInit = (RMCPacketRequestStoreService_InitiateBuyAndAttachComponents)rmc.request;
                        Log.WriteLine(1, "[RMC Store][CAPTURE] (22) InitiateBuyAndAttachComponents"
                            + " pid=" + client.PID
                            + " WeaponSlotID=" + attachInit.WeaponSlotID
                            + " WeaponBagType=" + attachInit.WeaponBagType
                            + " SkuComps=[" + string.Join(",", attachInit.ComponentSkuData.Select(c => c.SkuId)) + "]"
                            + " InvComps=[" + string.Join(",", attachInit.ComponentInventorySlotIds) + "]"
                            + " Coupons=[" + string.Join(",", attachInit.CouponIds) + "]", Color.Cyan);
                        // Capture build: record a no-cost transaction so the 2-phase flow proceeds (the
                        // client gets a trId, then sends the Complete). NO persistence yet -- the encoding
                        // of InvComps is being captured first. Persist lands in the follow-up build.
                        trId = TransactionModel.SaveTransactionRaw(client.PID, TransactionType.BuyAndAttachComponents);
                        reply = new RMCPacketResponseStoreService_InitiateBuyWeaponAndAttachComponents(trId);
                        RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                        if (trId > 0)
                            SendCompleteNotif(client, trId);
                    }
                    break;
                case 23: // CompleteBuyAndAttachComponents -- returns inventory + current component lists
                    {
                        var attachCompl = (RMCPacketRequestStoreService_CompleteBuyWeaponAndAttachComponents)rmc.request;
                        Log.WriteLine(1, "[RMC Store][CAPTURE] (23) CompleteBuyAndAttachComponents pid=" + client.PID + " trId=" + attachCompl.TransactionId, Color.Cyan);
                        TransactionModel.CompleteTransaction(attachCompl.TransactionId);
                        reply = BuildAttachCompleteResponse(client);
                        RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    }
                    break;
                case 26:
                    var buyAbilityInitReq = (RMCPacketRequestStoreService_InitiateBuyAbilityWithUpgrades)rmc.request;
                    trId = TransactionModel.SaveMultiItemTransaction(
                        client.PID,
                        buyAbilityInitReq.AbilitySkuData.SkuId,
                        TransactionType.BuyAbilityWithUpgrades,
                        buyAbilityInitReq.AbilitySkuData.VirtualCurrencyType,
                        buyAbilityInitReq.UpgradeSKUIdSlots
                    );
                    reply = new RMCPacketResponseStoreService_InitiateBuyAbilityWithUpgrades(trId);
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    // send complete transaction notif on success
                    if (trId > 0)
                        SendCompleteNotif(client, trId);
                    break;
                case 27:
                    var buyAbilityComplReq = (RMCPacketRequestStoreService_CompleteBuyAbilityWithUpgrades)rmc.request;
                    TransactionModel.CompleteTransaction(buyAbilityComplReq.TransactionId);
                    reply = new RMCPacketResponseStoreService_CompleteBuyAbilityWithUpgrades();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                case 30:
                    var buyArmorWithInsertsInitReq = (RMCPacketRequestStoreService_InitiateBuyArmourAndAttachInserts)rmc.request;
                    trId = TransactionModel.SaveMultiItemTransaction(
                        client.PID,
                        buyArmorWithInsertsInitReq.ArmorSkuData.SkuId,
                        TransactionType.BuyArmourAndAttachInserts,
                        buyArmorWithInsertsInitReq.ArmorSkuData.VirtualCurrencyType,
                        buyArmorWithInsertsInitReq.InsertSKUIdSlots
                    );
                    reply = new RMCPacketResponseStoreService_InitiateBuyArmourAndAttachInserts(trId);
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    // send complete transaction notif on success
                    if (trId > 0)
                        SendCompleteNotif(client, trId);
                    break;
                case 31:
                    var buyArmorWithInsertsComplReq = (RMCPacketRequestStoreService_CompleteBuyArmourAndAttachInserts)rmc.request;
                    TransactionModel.CompleteTransaction(buyArmorWithInsertsComplReq.TransactionId);
                    reply = new RMCPacketResponseStoreService_CompleteBuyArmourAndAttachInserts();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                default:
                    // CUSTOMIZE-CAPTURE: dump the raw body so an unhandled customize/store apply reveals
                    // its exact method id + bytes on the first in-game test.
                    Log.WriteLine(1, "[RMC Store][CAPTURE] Unhandled Method 0x" + rmc.methodID.ToString("X") + " body=" + HexBody(p, rmc), Color.Orange);
                    break;
            }
        }

        // Current inventory + per-owned-weapon component lists (keyed by InventoryID), the standard
        // CompleteBuy*AndAttach* reply shape. Serves the DEFAULT component lists for now; the follow-up
        // build overrides with the persisted per-instance custom list once the wire encoding is locked.
        private static RMCPacketResponseStoreService_CompleteBuyWeaponAndAttachComponents BuildAttachCompleteResponse(ClientInfo client)
        {
            var r = new RMCPacketResponseStoreService_CompleteBuyWeaponAndAttachComponents();
            r.Inventory = DBHelper.GetAllUserItems(client.PID);
            foreach (GR5_UserItem ui in r.Inventory)
                if (ui.ItemType == 2)
                    r.UserComponentLists.Add(new Map_U32_VectorU32 { key = ui.InventoryID, vector = DBHelper.GetWeaponComponentList(ui.ItemID) });
            return r;
        }

        // Hex of the un-consumed remainder of the request stream (parse-side default).
        private static string HexRest(Stream s)
        {
            long pos = s.Position;
            int len = (int)(s.Length - pos);
            if (len <= 0) return "";
            byte[] b = new byte[len];
            s.Read(b, 0, len);
            s.Position = pos;
            return BitConverter.ToString(b).Replace("-", "");
        }

        // Hex of the RMC request body (params after callID+methodID) straight from the raw packet
        // (handle-side default, where rmc.request was never parsed).
        private static string HexBody(QPacket p, RMCP rmc)
        {
            int off = rmc._afterProtocolOffset + 8;
            if (p.payload == null || off >= p.payload.Length) return "";
            int len = p.payload.Length - off;
            byte[] b = new byte[len];
            Array.Copy(p.payload, off, b, 0, len);
            return BitConverter.ToString(b).Replace("-", "");
        }

        /// <summary>
        /// Signals transaction completion to the game to initiate a completion request.
        /// </summary>
        /// <param name="client"></param>
        /// <param name="trId"></param>
        private static void SendCompleteNotif(ClientInfo client, uint trId)
        {
            NotificationQuene.AddNotification(new NotificationQueneEntry(client, 3000, 0, 1022, 1, trId, trId, 0, ""));
        }

        public enum VirtualCurrencyType
        {
            RP = 1,
            GC = 2
        }

        /// <summary>
        /// The type of transaction request.
        /// </summary>
        /// <see cref="https://github.com/zeroKilo/GROBackendWV/wiki/RMC-Store-Service#transaction-type"/>
        public enum TransactionType
        {
            BuyItem = 0,
            BuyWeaponAndAttachComponents,
            BuyAndAttachComponents,
            BuyAndRepairItem,
            BuyAbilityWithUpgrades,
            BuyAndAttachUpgrades,
            BuyArmourAndAttachInserts,
            BuyAndAttachInserts
        }
    }
}
