using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace QuazalWV
{
    // StoreService method 32 (0x20): attach inserts to an armor the persona ALREADY OWNS -- the analog of the
    // weapon-customize method 22 (InitiateBuyAndAttachComponents). Same shape as method 30
    // (InitiateBuyArmourAndAttachInserts) EXCEPT it carries the owned armor's INVENTORY id (u32) instead of an
    // ArmorSkuData cart item, because nothing is being purchased. Decoded from a live capture (body began
    // B8000000 C20B0000 = TicketId 184, ArmorInventoryId 3010, ...):
    //   [u32 TicketId][u32 ArmorInventoryId][list InsertSKUIdSlots][list InsertInventoryIdSlots]
    //   [list RemoveInventory][list CouponIds]      (each GR5_IdSlotPair = Id,Slot,Currency = 3x u32)
    public class RMCPacketRequestStoreService_InitiateBuyAndAttachInserts : RMCPRequest
    {
        public uint TicketId { get; set; }
        public uint ArmorInventoryId { get; set; }
        public List<GR5_IdSlotPair> InsertSKUIdSlots { get; set; }
        public List<GR5_IdSlotPair> InsertInventoryIdSlots { get; set; }
        public List<GR5_IdSlotPair> RemoveInventory { get; set; }
        public List<uint> CouponIds { get; set; }

        public RMCPacketRequestStoreService_InitiateBuyAndAttachInserts(Stream s)
        {
            InsertSKUIdSlots = new List<GR5_IdSlotPair>();
            InsertInventoryIdSlots = new List<GR5_IdSlotPair>();
            RemoveInventory = new List<GR5_IdSlotPair>();
            CouponIds = new List<uint>();

            TicketId = Helper.ReadU32(s);
            ArmorInventoryId = Helper.ReadU32(s);

            uint count = Helper.ReadU32(s);
            for (uint idx = 0; idx < count; idx++)
                InsertSKUIdSlots.Add(new GR5_IdSlotPair(s));

            count = Helper.ReadU32(s);
            for (uint idx = 0; idx < count; idx++)
                InsertInventoryIdSlots.Add(new GR5_IdSlotPair(s));

            count = Helper.ReadU32(s);
            for (uint idx = 0; idx < count; idx++)
                RemoveInventory.Add(new GR5_IdSlotPair(s));

            count = Helper.ReadU32(s);
            for (uint idx = 0; idx < count; idx++)
                CouponIds.Add(Helper.ReadU32(s));
        }

        public override byte[] ToBuffer()
        {
            throw new NotImplementedException();
        }

        public override string ToString()
        {
            return "[InitiateBuyAndAttachInserts Request]";
        }

        public override string PayloadToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"\t[Ticket: {TicketId}]");
            sb.AppendLine($"\t[ArmorInventoryId: {ArmorInventoryId}]");
            return sb.ToString();
        }
    }
}
