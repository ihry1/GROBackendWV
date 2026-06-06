using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    public class MSG_ID_BM_StartRound : BM_Message
    {
        public MSG_ID_BM_StartRound()
        {
            msgID = 0x384;
            paramList.Add(new BM_Param(BM_Param.PARAM_TYPE.Buffer, MakePayload()));
        }

        public byte[] MakePayload()
        {
            MemoryStream m = new MemoryStream();
            // The client clock (NET_ul_GetNetworkClock) is synced to the DS's Global.uptime (ms) via the
            // SessionClock SyncResponse (Payload_SyncResponse). AI_MatchRoundClient::UpdateRoundClock (vtbl+52,
            // @AICLASS 0x101c2080) computes each frame in state 2:
            //     currentRoundLength = NET_ul_GetNetworkClock()/1000 - roundStartTime   (clamped to roundDuration)
            // so roundStartTime MUST be the DS uptime in SECONDS at send time -> currentRoundLength starts ~0
            // and grows, exactly like warmup's elapsedStateTime. The old roundStartTime=0 made it jump to
            // netclock/1000 (huge) -> clamp to roundDuration -> round insta-ends / ~6s spawn reset.
            float now = (float)(Global.uptime.ElapsedMilliseconds / 1000.0);
            Helper.WriteFloat(m, now);   //stateStartTime -> AI_Match.float8 (Update derives float18 = netclock/1000 - this)
            Helper.WriteU8(m, 1);        //roundID (first round; a re-send with the same id keeps the existing round)
            Helper.WriteFloat(m, now);   //roundStartTime -> currentRoundLength = netclock/1000 - this (starts ~0, grows)
            Helper.WriteFloat(m, 9999);  //roundDuration (large so the round won't auto-end while we validate)
            Helper.WriteU8(m, 0);        //bContested
            Helper.WriteU8(m, 0);        //bIsCurrRoundLast
            return m.ToArray();
        }
    }
}
