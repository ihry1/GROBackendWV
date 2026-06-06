using QuazalWV.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    // StoreService method 22 InitiateBuyAndAttachComponents -- the "apply" the weapon-customize page
    // sends when re-attaching components to an ALREADY-OWNED weapon (method 20 is the variant that
    // BUYS a NEW weapon). Sourced from the customize helper's existing-weapon branch
    // (AI_WeaponCustomizeHelper::InitiateBuyWeaponAndAttachComponents -> pStoreModel VMT slot). Wire
    // field order per RE/protocols/77_GR5_StoreService.md (method 22):
    //   uint32 TicketId, uint32 WeaponSlotID, uint32 WeaponBagType,
    //   qvector<GR5_SingleCartItem> ComponentSKUDataList   (components purchased from the store),
    //   qvector<uint32>             ComponentInventorySlotIdList (components attached from inventory),
    //   qvector<uint32>             CouponIdVector.
    // The numeric id space of ComponentInventorySlotIdList is a runtime UI3D-object value, so the
    // handler logs it verbatim for the one-shot in-game capture that locks the encoding.
    public class RMCPacketRequestStoreService_InitiateBuyAndAttachComponents : RMCPRequest
    {
        public uint TicketId { get; set; }
        public uint WeaponSlotID { get; set; }
        public uint WeaponBagType { get; set; }
        public List<GR5_SingleCartItem> ComponentSkuData { get; set; }
        public List<uint> ComponentInventorySlotIds { get; set; }
        public List<uint> CouponIds { get; set; }

        public RMCPacketRequestStoreService_InitiateBuyAndAttachComponents(Stream s)
        {
            ComponentSkuData = new List<GR5_SingleCartItem>();
            ComponentInventorySlotIds = new List<uint>();
            CouponIds = new List<uint>();

            TicketId = Helper.ReadU32(s);
            WeaponSlotID = Helper.ReadU32(s);
            WeaponBagType = Helper.ReadU32(s);

            uint count = Helper.ReadU32(s);
            for (uint i = 0; i < count; i++)
                ComponentSkuData.Add(new GR5_SingleCartItem(s));

            count = Helper.ReadU32(s);
            for (uint i = 0; i < count; i++)
                ComponentInventorySlotIds.Add(Helper.ReadU32(s));

            count = Helper.ReadU32(s);
            for (uint i = 0; i < count; i++)
                CouponIds.Add(Helper.ReadU32(s));
        }

        public override byte[] ToBuffer()
        {
            throw new NotImplementedException();
        }

        public override string ToString()
        {
            return "[InitiateBuyAndAttachComponents Request]";
        }

        public override string PayloadToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"\t[Ticket: {TicketId}]");
            sb.AppendLine($"\t[WeaponSlotID: {WeaponSlotID}]");
            sb.AppendLine($"\t[WeaponBagType: {WeaponBagType}]");
            sb.AppendLine($"\t[SkuComps: {string.Join(", ", ComponentSkuData.Select(c => c.SkuId))}]");
            sb.AppendLine($"\t[InvComps: {string.Join(", ", ComponentInventorySlotIds)}]");
            sb.AppendLine($"\t[Coupons: {string.Join(", ", CouponIds)}]");
            return sb.ToString();
        }
    }
}
