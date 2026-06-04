using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    public static class DO_CreateAndPromoteDuplicaMessage
    {
        // REVERTED to the original no-op. The implemented version (parse + DO_Session.DupObjs.Add +
        // reply DO_Outcome.Create(callID, 0)) was suspected of crashing the client during
        // connect/session setup and is NOT dispatched from DO.cs. The full implementation + the
        // expected wire format are preserved in RE/plan/05-createandpromote-duplica.md; re-enable
        // only after confirming the correct response against a packet capture.
        public static byte[] HandleMessage(ClientInfo client, byte[] data)
        {
            Log.WriteLine(2, "[DO] Handling DO_CreateAndPromoteDuplicaMessage... TODO!");
            return new byte[0];
        }

        public static byte[] Create(ushort callID, DupObj obj, byte version)
        {
            Log.WriteLine(2, "[DO] Creating DO_CreateAndPromoteDuplicaMessage");
            MemoryStream m = new MemoryStream();
            m.WriteByte(0x13);
            Helper.WriteU16(m, callID);
            Helper.WriteU32(m, obj);
            Helper.WriteU32(m, obj.Master);
            m.WriteByte(version);
            Helper.WriteU32(m, 0);
            byte[] payload = obj.getPayload();
            m.Write(payload, 0, payload.Length);
            return m.ToArray();
        }
    }
}
