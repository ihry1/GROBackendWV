using System;
using System.IO;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    public static class WeaponService
    {
        public static void HandleWeaponServiceRequest(QPacket p, RMCP rmc, ClientInfo client)
        {
            RMCPResponse reply;
            switch (rmc.methodID)
            {
                case 3:
                    reply = new RMCPacketResponseWeaponService_GetTemplateWeaponMaps();
                    RMC.SendResponseWithACK(client.udp, p, rmc, client, reply);
                    break;
                default:
                    // CUSTOMIZE-CAPTURE: the weapon-customize page may call GetUserWeaponByID(1) /
                    // GetUserWeaponMaps(2) / RemoveAttachments(4); WeaponService is in RMC.ProcessRequest's
                    // no-op group, so the body isn't pre-parsed -- dump it raw from the packet so the first
                    // in-game customize reveals which verbs fire and their exact bytes.
                    Log.WriteLine(1, "[RMC WeaponService][CAPTURE] Method 0x" + rmc.methodID.ToString("X") + " body=" + HexBody(p, rmc), Color.Cyan);
                    break;
            }
        }

        // Hex of the RMC request body (params after callID+methodID) straight from the raw packet.
        private static string HexBody(QPacket p, RMCP rmc)
        {
            int off = rmc._afterProtocolOffset + 8;
            if (p.payload == null || off >= p.payload.Length) return "";
            int len = p.payload.Length - off;
            byte[] b = new byte[len];
            Array.Copy(p.payload, off, b, 0, len);
            return BitConverter.ToString(b).Replace("-", "");
        }
    }
}
