using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    // BM 901 (0x385) WARMUP. The client's AI_MatchClient::BroadcastMessage (@AICLASS 0x101c0ca4) case 901 sets
    // AI_Match.state (+0x54) = 1 (Warmup) and pops exactly ONE float (stateStartTime -> float8).
    //
    // WHY warmup and not round (the old BM 900 / StartRound):
    //   AI_MatchServer::GetSpawnWaveId(team) (@0x101bfd30):
    //     state 1 (warmup) = floor(elapsedStateTime / spawnWaveDuration) + 1   <-- pure CLIENT-LOCAL time clock
    //     state 2 (round)  = AI_MatchRound::GetTeamSpawnWaveID(round, team)     <-- needs a server-ticked round obj
    //   spawnWaveDuration defaults to 15.0 (AI_Match::AI_Match @0x101c0070), so in warmup the team wave id
    //   auto-advances every ~15s. The player's spawnWaveID is set ONCE to (currentWaveId + 1) when it enters
    //   WaitForSpawn (AI_EntityPlayerAbstract::SetNextSpawnWave, called only from cl_/ds_PlayerAbstractChangeState),
    //   so after ~one wave the clock catches up: GetSpawnWaveId >= spawnWaveID ->
    //   AI_MatchServer::bCanPlayerAbstractSpawn (IsWaitingForWave) goes FALSE and STAYS false (monotonic clock).
    //   That (a) lets cNetRulesManager::bCanSpawn fire (it also requires eStateID==4 LoopAdversarial, which we
    //   already send via Synchronize) so the player legitimately spawns, and (b) fails the deploy/respawn screen's
    //   show-gate in cInGameMenuManager::Update (IsWaitingForWave && abstract.currentState==2), which is what holds
    //   bBlockUserInput (+0x189) set -> releasing it unlocks move / ADS / fire.
    //   In ROUND the wave id comes from the un-ticked AI_MatchRound, so the wave never arrives, the deploy screen
    //   keeps re-blocking input, and the player force-spawned to state 5 gets reset back to WaitForSpawn ~6s later.
    //
    // The stateStartTime float is cosmetic for the wave math: the client tracks its own elapsedStateTime (+0x18),
    // reset on state entry, so the value below does not change the ~one-wave deploy wait.
    public class MSG_ID_BM_Warmup : BM_Message
    {
        public MSG_ID_BM_Warmup()
        {
            msgID = 0x385;
            paramList.Add(new BM_Param(BM_Param.PARAM_TYPE.Buffer, MakePayload()));
        }

        public byte[] MakePayload()
        {
            MemoryStream m = new MemoryStream();
            Helper.WriteFloat(m, 0);    //stateStartTime (cosmetic; client uses its own elapsedStateTime clock)
            return m.ToArray();
        }
    }
}
