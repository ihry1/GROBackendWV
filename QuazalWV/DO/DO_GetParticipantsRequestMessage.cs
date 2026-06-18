using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    public static class DO_GetParticipantsRequestMessage
    {
        public static byte[] HandleMessage(ClientInfo client, byte[] data)
        {
            Log.WriteLine(2, "[DO] Handling DO_GetParticipantsRequestMessage...");
            // ★★ 2-CLIENT IDENTITY FIX (2026-06-17). The DEDICATED SERVER never auth-logs-in a match
            // connection: AuthenticationService method-2 (client.PID = user.PID) reaches only the BACKEND
            // (port 21030), NOT the DS (21032). So on the DS, client.PID is still the SYN-arrival COUNTER
            // value (Global.pidCounter++, QPacketHandler.cs) -- players get PIDs by CONNECTION ORDER, not
            // identity -> GetSpawnLoadout(client.PID) (Entitiy_CMD.cs/BM_Message.cs) reads the WRONG
            // persona -> the two clients spawn with each other's class + loadout (the "swapped class" bug).
            // The client DOES send its own authenticated PID to the DS, here, inside the participant URL
            // ("prudps:/...;PID=<n>;..."). This is the FIRST DO message the connection sends (before spawn),
            // and it is dispatched after Global.GetClientByIDrecv resolved the right per-connection client,
            // so adopt that PID onto this connection. On the backend client.PID is already correct, so the
            // value matches and this is a no-op there. (See gro-2client-plan: routing/identity/roster.)
            try
            {
                string ascii = Encoding.ASCII.GetString(data);
                int idx = ascii.IndexOf("PID=", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    int start = idx + 4, end = start;
                    while (end < ascii.Length && char.IsDigit(ascii[end])) end++;
                    uint pid;
                    if (end > start && uint.TryParse(ascii.Substring(start, end - start), out pid) && pid != 0)
                    {
                        if (client.PID != pid)
                            Log.WriteLine(1, "[DO] DS connection PID rebind: counter PID " + client.PID +
                                             " -> authenticated PID " + pid + " (station " + client.stationID + ")");
                        client.PID = pid;
                    }
                }
            }
            catch (Exception ex) { Log.WriteLine(1, "[DO] GetParticipants PID-parse failed: " + ex.Message); }
            return DO_GetParticipantsResponseMessage.Create(data);
        }
    }
}
