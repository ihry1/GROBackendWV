using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    public class RMCPacketResponseProgressionService_GetLevels : RMCPResponse
    {
        public List<GR5_Level> levels = new List<GR5_Level>();

        public RMCPacketResponseProgressionService_GetLevels()
        {
            // Serve the full level -> cumulative-PEC table (the client's ProgressionModel / RDV::Proxy::pProgressionModel).
            // PREVIOUSLY STUBBED with a single dummy GR5_Level (id0/PEC0/level0) -> the client's ProgressionModel was
            // effectively EMPTY, so any character that was "ready to level up" (per-char PEC >= NextLevelPEC) walked an
            // empty/zeroed table and FAILED to load (e.g. a level-50 persona whose PEC == nPEC). A character that is NOT
            // ready (PEC < nPEC, the normal case) never consulted it, which is why most personas loaded fine.
            // Levels 1..60 (unlocks require up to level 57); TotalPEC strictly monotonic so the client's level-up walk
            // terminates correctly. Level L is reached at (L-1)*100 cumulative PEC (level 1 = 0). See gro-character-load-progression.
            const uint maxLevel = 60;
            for (uint lvl = 1; lvl <= maxLevel; lvl++)
                levels.Add(new GR5_Level { m_Id = lvl, m_Level = lvl, m_TotalPEC = (lvl - 1) * 100 });
        }

        public override byte[] ToBuffer()
        {
            MemoryStream m = new MemoryStream();
            Helper.WriteU32(m, (uint)levels.Count);
            foreach (GR5_Level l in levels)
                l.toBuffer(m);
            return m.ToArray();
        }

        public override string ToString()
        {
            return "[RMCPacketResponseProgressionService_GetLevels]";
        }

        public override string PayloadToString()
        {
            return "";
        }
    }
}
