using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace QuazalWV
{
    /// <summary>
    /// Reads real player spawn coordinates out of the game's Yeti.big archive at runtime,
    /// so the dedicated server can place players at valid in-map positions instead of (0,0,0).
    ///
    /// Format + offsets were reverse-engineered and validated against the shipped Yeti.big
    /// (see RE/plan/07-yetibig-format-and-api.md and RE/tools/yetibig_names.py):
    ///   - Archive: magic 0x47494259 ("YBIG") at 0; u32 infoOffset at file+16.
    ///   - At infoOffset: u16 version(0x86), u16 folderCount, u32 fileCount, 120 bytes pad,
    ///     then fileCount x 100-byte file entries, then folderCount x 64-byte folder entries
    ///     (8-byte aligned), then the data section (8-byte aligned).
    ///   - File entry (100 bytes): u32 offset(+0, in 8-byte units), u32 key(+4), u16 type(+12),
    ///     u16 folder(+14), u32 zip(+96, 0=stored/non-0=zlib), name[60](+32).
    ///   - Object body: seek dataOffset + offset*8; if zip!=0 read u32 compSize, u32 decompSize,
    ///     then a zlib stream; else u32 size + raw. Decompressed buffer = [u32 refCount]
    ///     [refCount x u32 refKeys][body].
    ///   - Game object ('gao', type 0x0D) body: 12-byte header + 3 pad bytes, then a 4x4
    ///     column-major float matrix at body+15. The world position is the 4th column
    ///     (floats 12/13/14) at body+63/67/71 (w=1.0 at +75) — NOT the M41..M43 bottom row.
    ///   - Spawn points are 'gao' named "SpawnZone..." under the map's
    ///     ".../<MapName>/.../Modelisation/Game object" folder; team is encoded in the name.
    ///   - mapKey (SessionInfosParameter.defaultMapKey) is the key of the map's 'World' object;
    ///     its folder path yields the map name used to scope the spawn search.
    /// </summary>
    public static class YetiBigSpawnReader
    {
        private const uint MAGIC = 0x47494259;
        private const ushort TYPE_GAO = 0x000D;

        private struct Entry
        {
            public uint Offset;   // in 8-byte units, relative to data section
            public uint Key;
            public ushort Type;
            public ushort Folder;
            public uint Zip;
            public string Name;
        }

        private static readonly object _lock = new object();
        private static bool _loaded;
        private static bool _ok;
        private static string _loadedPath;
        private static long _dataOffset;
        private static List<Entry> _entries;
        private static Dictionary<uint, int> _byKey;     // key -> index in _entries
        private static byte[] _folderBytes;
        private static ushort _folderCount;

        // mapKey -> resolved spawn list, cached
        private static readonly Dictionary<uint, List<Spawn>> _spawnCache = new Dictionary<uint, List<Spawn>>();

        public struct Spawn
        {
            public string Name;
            public int Team;          // 1, 2, or 0 (unknown)
            public float X, Y, Z;
        }

        /// <summary>Try to get a spawn position for the given map and team. Falls back gracefully.</summary>
        public static bool TryGetSpawn(uint mapKey, int team, out float x, out float y, out float z)
        {
            x = y = z = 0f;
            try
            {
                List<Spawn> spawns = GetSpawns(mapKey);
                if (spawns == null || spawns.Count == 0)
                    return false;
                // prefer requested team, else any
                Spawn s = spawns.FirstOrDefault(p => p.Team == team);
                if (s.Name == null)
                    s = spawns[0];
                x = s.X; y = s.Y; z = s.Z;
                return true;
            }
            catch (Exception ex)
            {
                Log.WriteLine(1, "[YetiBig] spawn lookup failed: " + ex.Message);
                return false;
            }
        }

        public static List<Spawn> GetSpawns(uint mapKey)
        {
            lock (_lock)
            {
                if (!EnsureLoaded())
                    return null;
                List<Spawn> cached;
                if (_spawnCache.TryGetValue(mapKey, out cached))
                    return cached;

                var result = new List<Spawn>();
                // 1) resolve the map's folder name from the world object's folder path
                int mi;
                if (!_byKey.TryGetValue(mapKey, out mi))
                {
                    Log.WriteLine(1, "[YetiBig] mapKey 0x" + mapKey.ToString("X8") + " not found in Yeti.big");
                    _spawnCache[mapKey] = result;
                    return result;
                }
                string mapName = MapNameFromPath(FolderPath(_entries[mi].Folder));
                if (mapName == null)
                {
                    Log.WriteLine(1, "[YetiBig] could not derive map name for key 0x" + mapKey.ToString("X8"));
                    _spawnCache[mapKey] = result;
                    return result;
                }
                string scope = "/" + mapName + "/";
                // 2) find SpawnZone gao objects scoped to that map, read their transforms
                using (FileStream fs = File.OpenRead(_loadedPath))
                {
                    foreach (Entry e in _entries)
                    {
                        if (e.Type != TYPE_GAO || e.Name == null) continue;
                        if (!e.Name.StartsWith("SpawnZone", StringComparison.OrdinalIgnoreCase)) continue;
                        if (FolderPath(e.Folder).IndexOf(scope, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        float px, py, pz;
                        if (TryReadGaoPosition(fs, e, out px, out py, out pz))
                        {
                            result.Add(new Spawn
                            {
                                Name = e.Name,
                                Team = TeamFromName(e.Name),
                                X = px, Y = py, Z = pz
                            });
                        }
                    }
                }
                Log.WriteLine(1, "[YetiBig] map '" + mapName + "' (key 0x" + mapKey.ToString("X8") + "): "
                                 + result.Count + " spawn points");
                _spawnCache[mapKey] = result;
                return result;
            }
        }

        private static int TeamFromName(string n)
        {
            if (n.IndexOf("Team1", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (n.IndexOf("Team2", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            return 0;
        }

        private static string MapNameFromPath(string path)
        {
            // path like /Data/- 14 - Maps GRO/03_MoscowUB_City/03_MoscowUB_City_LD/Modelisation/World
            string[] parts = path.Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
                if (parts[i].IndexOf("Maps GRO", StringComparison.OrdinalIgnoreCase) >= 0)
                    return parts[i + 1];
            return null;
        }

        private static bool TryReadGaoPosition(FileStream fs, Entry e, out float x, out float y, out float z)
        {
            x = y = z = 0f;
            byte[] body = ReadObjectBody(fs, e);
            if (body == null || body.Length < 76) // 15-byte header + 64-byte matrix - 3
                return false;
            // matrix at body+15, column-major; translation = 4th column (floats 12/13/14)
            int baseMat = 15;
            x = BitConverter.ToSingle(body, baseMat + 48);
            y = BitConverter.ToSingle(body, baseMat + 52);
            z = BitConverter.ToSingle(body, baseMat + 56);
            float w = BitConverter.ToSingle(body, baseMat + 60);
            // sanity: affine row should be w==1 and coords finite/reasonable
            if (Math.Abs(w - 1.0f) > 0.01f) return false;
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return false;
            if (Math.Abs(x) > 1e5f || Math.Abs(y) > 1e5f || Math.Abs(z) > 1e5f) return false;
            return true;
        }

        /// <summary>Reads + decompresses an object and strips the reference-key header, returning the body.</summary>
        private static byte[] ReadObjectBody(FileStream fs, Entry e)
        {
            long pos = _dataOffset + (long)e.Offset * 8L;
            fs.Seek(pos, SeekOrigin.Begin);
            byte[] decompressed;
            using (BinaryReader br = new BinaryReader(fs, Encoding.ASCII, true))
            {
                if (e.Zip == 0)
                {
                    int size = br.ReadInt32();
                    if (size < 0 || size > 8 * 1024 * 1024) return null;
                    decompressed = br.ReadBytes(size);
                }
                else
                {
                    int compSize = br.ReadInt32();
                    int decompSize = br.ReadInt32();
                    if (decompSize < 0 || decompSize > 8 * 1024 * 1024 || compSize < 2 || compSize > 8 * 1024 * 1024)
                        return null;
                    byte[] comp = br.ReadBytes(compSize);
                    decompressed = ZlibInflate(comp, decompSize);
                    if (decompressed == null) return null;
                }
            }
            if (decompressed.Length < 4) return null;
            int refCount = BitConverter.ToInt32(decompressed, 0);
            if (refCount < 0 || refCount > 4096) return null;
            int bodyOff = 4 + refCount * 4;
            if (bodyOff > decompressed.Length) return null;
            int len = decompressed.Length - bodyOff;
            byte[] body = new byte[len];
            Array.Copy(decompressed, bodyOff, body, 0, len);
            return body;
        }

        /// <summary>Inflate a zlib stream using the built-in DeflateStream (skip the 2-byte zlib header).</summary>
        private static byte[] ZlibInflate(byte[] zlibData, int expectedSize)
        {
            // zlib = [CMF][FLG] + raw deflate + [adler32]. DeflateStream wants raw deflate.
            using (var ms = new MemoryStream(zlibData, 2, zlibData.Length - 2))
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            {
                byte[] outBuf = new byte[expectedSize];
                int read = 0;
                while (read < expectedSize)
                {
                    int r = ds.Read(outBuf, read, expectedSize - read);
                    if (r == 0) break;
                    read += r;
                }
                if (read != expectedSize) return null;
                return outBuf;
            }
        }

        private static string FolderPath(ushort folder)
        {
            var parts = new List<string>();
            ushort idx = folder;
            int guard = 0;
            while (idx != 0xFFFF && guard++ < 64)
            {
                int b = idx * 0x40;
                if (b + 0x40 > _folderBytes.Length) break;
                ushort parent = BitConverter.ToUInt16(_folderBytes, b + 4);
                int nlen = 0;
                while (nlen < 0x36 && _folderBytes[b + 10 + nlen] != 0) nlen++;
                string name = Encoding.ASCII.GetString(_folderBytes, b + 10, nlen);
                if (name.Length > 0 && name != "/")
                    parts.Add(name);
                idx = parent;
            }
            parts.Reverse();
            return "/" + string.Join("/", parts);
        }

        private static bool EnsureLoaded()
        {
            string path = Global.yetiBigPath;
            if (_loaded && _ok && _loadedPath == path)
                return true;
            if (_loaded && _loadedPath == path)
                return false; // already tried this path and failed
            _loaded = true;
            _loadedPath = path;
            _ok = false;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Log.WriteLine(1, "[YetiBig] Yeti.big not found at '" + path + "' - spawns will be (0,0,0). Set Global.yetiBigPath.");
                    return false;
                }
                using (FileStream fs = File.OpenRead(path))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    if (br.ReadUInt32() != MAGIC)
                    {
                        Log.WriteLine(1, "[YetiBig] bad magic in " + path);
                        return false;
                    }
                    fs.Seek(16, SeekOrigin.Begin);
                    long infoOffset = br.ReadUInt32();
                    fs.Seek(infoOffset, SeekOrigin.Begin);
                    br.ReadUInt16();                       // version (0x86)
                    _folderCount = br.ReadUInt16();
                    uint fileCount = br.ReadUInt32();
                    fs.Seek(0x78, SeekOrigin.Current);     // 120-byte pad
                    long entriesOff = infoOffset + 0x80;

                    _entries = new List<Entry>((int)fileCount);
                    _byKey = new Dictionary<uint, int>((int)fileCount);
                    byte[] eb = br.ReadBytes((int)fileCount * 100);
                    for (int i = 0; i < fileCount; i++)
                    {
                        int o = i * 100;
                        var e = new Entry
                        {
                            Offset = BitConverter.ToUInt32(eb, o + 0),
                            Key = BitConverter.ToUInt32(eb, o + 4),
                            Type = BitConverter.ToUInt16(eb, o + 12),
                            Folder = BitConverter.ToUInt16(eb, o + 14),
                            Zip = BitConverter.ToUInt32(eb, o + 96),
                            Name = ReadName(eb, o + 32, 60)
                        };
                        _entries.Add(e);
                        if (!_byKey.ContainsKey(e.Key))
                            _byKey[e.Key] = i;
                    }

                    long folderOff = entriesOff + (long)fileCount * 100;
                    folderOff = Align8(folderOff);
                    fs.Seek(folderOff, SeekOrigin.Begin);
                    _folderBytes = br.ReadBytes(_folderCount * 0x40);

                    _dataOffset = Align8(folderOff + (long)_folderCount * 0x40);
                }
                _ok = true;
                Log.WriteLine(1, "[YetiBig] loaded file table from " + path + " (" + _entries.Count + " objects)");
                return true;
            }
            catch (Exception ex)
            {
                Log.WriteLine(1, "[YetiBig] failed to load " + path + ": " + ex.Message);
                return false;
            }
        }

        private static long Align8(long v)
        {
            while ((v & 8) != 0) v++;   // matches GROExplorerWV/YETIFile.cs alignment
            return v;
        }

        private static string ReadName(byte[] b, int off, int len)
        {
            int n = 0;
            while (n < len && b[off + n] != 0) n++;
            return Encoding.ASCII.GetString(b, off, n);
        }
    }
}
