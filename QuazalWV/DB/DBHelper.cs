using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data.SQLite;
using System.Xml.Linq;
using System.Data.SqlClient;

namespace QuazalWV
{
    public class SpawnLoadoutInfo
    {
        public byte ClassID;
        public uint BagType;
        public uint KitID;
        public byte FaceID;
        public byte SkinID;

        public uint MainWeaponID;
        public uint PistolWeaponID;
        public uint GrenadeWeaponID;

        public uint MainWeaponInventoryID;
        public uint PistolWeaponInventoryID;
        public uint GrenadeWeaponInventoryID;
        public uint ArmorInventoryID;
        public uint HelmetInventoryID;
        public uint AbilityInventoryID;
        public uint PassiveAbilityInventoryID;

        public uint HelmetKey;
        public byte AbilityType;
        public byte PassiveAbilityType;
        public string Source = "";
    }

    public static class DBHelper
    {
        public static SQLiteConnection connection = new SQLiteConnection();

        // 2-player: the backend runs MANY listener threads (UDP/RDV/OnlineConfig/Auth + a thread PER TCP
        // client) that ALL share this one static SQLiteConnection. A connection allows only ONE active
        // reader at a time, so two clients querying at once (e.g. both loading character data) collide ->
        // SQLiteException -> swallowed by the packet loop -> response lost -> client hangs forever (the wv2
        // "Retrieving Character Data" hang). Serialize ALL access on dbLock. lock is re-entrant, so a write
        // helper that internally calls GetQueryResults on the same thread is safe.
        public static readonly object dbLock = new object();

        // Locked INSERT/UPDATE/CREATE. Every write goes through here (or a lock(dbLock) block) so it can
        // never race a reader on the shared connection.
        public static int ExecNonQuery(string sql)
        {
            lock (dbLock)
                using (SQLiteCommand cmd = new SQLiteCommand(sql, connection))
                    return cmd.ExecuteNonQuery();
        }

        // connStr defaults to the process's own database.sqlite (the backend). The dedicated server
        // passes the BACKEND's db path in read-only mode so it can look up loadouts/weapon components
        // at spawn (its own db is 0 bytes). Close-if-open lets a caller retry with a different source.
        public static void Init(string connStr = "Data Source=database.sqlite")
        {
            if (connection.State != System.Data.ConnectionState.Closed) connection.Close();
            connection.ConnectionString = connStr;
            connection.Open();
            Log.WriteLine(1, "DB loaded (" + connStr + ")...");
        }

        public static List<List<string>> GetQueryResults(string query)
        {
            lock (dbLock)
            {
                List<List<string>> result = new List<List<string>>();
                SQLiteCommand command = new SQLiteCommand(query, connection);
                SQLiteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    List<string> entry = new List<string>();
                    for (int i = 0; i < reader.FieldCount; i++)
                        entry.Add(reader[i].ToString());
                    result.Add(entry);
                }
                reader.Close();
                reader.Dispose();
                command.Dispose();
                return result;
            }
        }

        // Defensive numeric parse for DB string cells (each is reader[i].ToString()). The retail RMC
        // handlers called Convert.ToUInt32/ToByte directly, which THROW on an empty/NULL/non-numeric
        // cell; that exception is swallowed by the packet receive loop, so the entire response is
        // silently lost and the client hangs forever (exactly what the empty-odesc templateitems seed
        // did to "loading lobby"). These never throw: empty/NULL/garbage -> 0, decimal text ("0.0")
        // -> truncated, out-of-range -> clamped.
        public static uint SafeU32(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            uint u;
            if (uint.TryParse(s, out u)) return u;
            double d;
            if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d))
                return d <= 0 ? 0u : (d >= uint.MaxValue ? uint.MaxValue : (uint)d);
            return 0;
        }

        public static byte SafeU8(string s)
        {
            uint u = SafeU32(s);
            return u > 255 ? (byte)255 : (byte)u;
        }

        public static ClientInfo GetUserByName(string name)
        {
            ClientInfo result = null;
            List<List<string>> results = GetQueryResults("SELECT * FROM users WHERE name='" + name + "'");
            foreach(List<string> entry in results)
            {
                result = new ClientInfo();
                result.PID = Convert.ToUInt32(entry[1]);
                result.pass = entry[3];
                result.name = name;
            }
            return result;
        }

        public static GR5_Persona GetPersona(ClientInfo client)
        {
            List<List<string>> results = GetQueryResults("SELECT * FROM personas WHERE pid=" + client.PID);
            foreach (List<string> entry in results)
            {
                GR5_Persona p = new GR5_Persona();
                p.PersonaID = client.PID;
                p.Name = client.name;
                p.PortraitID = Convert.ToUInt32(entry[3]);
                p.DecoratorID = Convert.ToUInt32(entry[4]);
                p.AvatarBackgroundColor = Convert.ToUInt32(entry[5]);
                p.GRCash = Convert.ToUInt32(entry[6]);
                p.IGC = Convert.ToUInt32(entry[7]);
                p.AchievementPoints = Convert.ToUInt32(entry[8]);
                p.LastUsedCharacterID = Convert.ToByte(entry[9]);
                p.MaxInventorySlot = Convert.ToUInt32(entry[10]);
                p.MaxScrapYardSlot = Convert.ToUInt32(entry[11]);
                p.GhostRank = Convert.ToUInt32(entry[12]);
                p.Flag = Convert.ToUInt32(entry[13]);
                return p;
            }
            return null;
        }

        public static List<GR5_Character> GetCharacters(uint pid)
        {
            List<GR5_Character> result = new List<GR5_Character>();
            List<List<string>> results = GetQueryResults("SELECT * FROM characters WHERE pid=" + pid);
            foreach (List<string> entry in results)
            {
                GR5_Character c = new GR5_Character();
                c.PersonaID = pid;
                c.ClassID = Convert.ToUInt32(entry[2]);
                c.PEC = Convert.ToUInt32(entry[3]);
                c.Level = Convert.ToUInt32(entry[4]);
                c.UpgradePoints = Convert.ToUInt32(entry[5]);
                c.CurrentLevelPEC = Convert.ToUInt32(entry[6]);
                c.NextLevelPEC = Convert.ToUInt32(entry[7]);
                c.FaceID = Convert.ToByte(entry[8]);
                c.SkinToneID = Convert.ToByte(entry[9]);
                c.LoadoutKitID = Convert.ToByte(entry[10]);
                result.Add(c);
            }
            return result;
        }

        /// <summary>
        /// Gets news message headers and bodies, deprecated
        /// </summary>
        /// <param name="pid"></param>
        /// <param name="body"></param>
        /// <returns></returns>
        public static List<GR5_NewsMessage> GetNews(uint pid, string body)
        {
            List<GR5_NewsMessage> result = new List<GR5_NewsMessage>();
            List<List<string>> results = GetQueryResults("SELECT * FROM news");
            foreach (List<string> entry in results)
            {
                GR5_NewsMessage message = new GR5_NewsMessage();
                message.header = new GR5_NewsHeader();
                message.header.m_ID = Convert.ToUInt32(entry[1]);
                message.header.m_recipientID = pid;
                message.header.m_recipientType = Convert.ToUInt32(entry[2]);
                message.header.m_publisherPID = Convert.ToUInt32(entry[3]);
                message.header.m_publisherName = entry[4];
                message.header.m_displayTime = (ulong)DateTime.UtcNow.Ticks;
                message.header.m_publicationTime = (ulong)DateTime.UtcNow.Ticks;
                message.header.m_expirationTime = (ulong)DateTime.UtcNow.AddDays(5).Ticks;
                message.header.m_title = entry[5];
                message.header.m_link = entry[6];
                //m_body is in XML format, for now i hardcoded it in GR5_NewsMessage.cs
                message.m_body = body;
                result.Add(message);
            }
            return result;
        }

        public static List<GR5_TemplateItem> GetTemplateItems()
        {
            List<GR5_TemplateItem> result = new List<GR5_TemplateItem>();
            List<List<string>> results = GetQueryResults("SELECT * FROM templateitems");
            foreach (List<string> entry in results)
            {
                // Per-row guard: a single malformed row must never take down the whole response (and
                // with it the lobby). SafeU32/SafeU8 won't throw on bad numeric cells; the try/catch is
                // the backstop for anything else (e.g. a short row) -> skip that row + log it.
                try
                {
                    GR5_TemplateItem item = new GR5_TemplateItem
                    {
                        m_ItemID = SafeU32(entry[1]),
                        m_ItemType = SafeU8(entry[2]),
                        m_ItemName = entry[3] ?? "",
                        m_DurabilityType = SafeU8(entry[4]),
                        m_IsInInventory = entry[5] == "True",
                        m_IsSellable = entry[6] == "True",
                        m_IsLootable = entry[7] == "True",
                        m_IsRewardable = entry[8] == "True",
                        m_IsUnlockable = entry[9] == "True",
                        m_MaxItemInSlot = SafeU32(entry[10]),
                        m_GearScore = SafeU32(entry[11]),
                        m_IGCValue = SafeU32(entry[12]) / 100f,
                        m_OasisName = SafeU32(entry[13]),
                        m_OasisDesc = SafeU32(entry[14])
                    };
                    result.Add(item);
                }
                catch (Exception ex)
                {
                    Log.WriteLine(1, "[GetTemplateItems] SKIPPED malformed row iid=" + (entry.Count > 1 ? entry[1] : "?") + " : " + ex.Message);
                }
            }
            return result;
        }

        public static List<GR5_InventoryBag> GetInventoryBags(uint pid, byte type)
        {
            List<GR5_InventoryBag> result = new List<GR5_InventoryBag>();
            List<int> bagIDs = new List<int>();
            List<List<string>> results = GetQueryResults("SELECT * FROM inventorybags WHERE pid=" + pid + " AND bagtype =" + type);
            foreach (List<string> entry in results)
                bagIDs.Add(Convert.ToInt32(entry[0]));
            foreach(int bagID in bagIDs)
            {
                GR5_InventoryBag bag = new GR5_InventoryBag();
                bag.m_PersonaID = pid;
                bag.m_InventoryBagType = type;
                bag.m_InventoryBagSlotVector = new List<GR5_InventoryBagSlot>();
                results = GetQueryResults("SELECT * FROM inventorybagslots WHERE bagid=" + bagID);
                foreach (List<string> entry in results)
                {
                    uint invId = SafeU32(entry[2]);
                    // Skip EMPTY slots. An inventoryid==0 bag slot is built into a key-0 node in the client's
                    // per-bag slot hashmap (RDV_cl_InventoryManager::GetUserItemForSlot, keyed by inventoryid),
                    // whose GR5_UserItem has ItemID==0. The deploy 3D-preview (UI3DLootVisualContainer ->
                    // LoadTemplateAsync) then loads a null GAO -> cObjectManager.cpp:854 "Loading Wrong Type ?"
                    // hard assert at "SPAWNING INTO GAME". Empty positions must simply be absent, not item-0.
                    if (invId == 0) continue;
                    GR5_InventoryBagSlot slot = new GR5_InventoryBagSlot();
                    slot.InventoryID = invId;
                    slot.SlotID = SafeU32(entry[3]);
                    slot.Durability = SafeU32(entry[4]);
                    bag.m_InventoryBagSlotVector.Add(slot);
                }
                result.Add(bag);
            }
            return result;
        }

        public static List<GR5_UserItem> GetUserItems(uint pid, byte type)
        {
            List<GR5_UserItem> result = new List<GR5_UserItem>();
            List<List<string>> results = GetQueryResults("SELECT * FROM useritems WHERE pid=" + pid + " AND itemtype =" + type);
            foreach (List<string> entry in results)
            {
                GR5_UserItem item = new GR5_UserItem();
                item.InventoryID = Convert.ToUInt32(entry[1]);
                item.PersonaID = pid;
                item.ItemType = type;
                item.ItemID = Convert.ToUInt32(entry[4]);
                item.OasisName = Convert.ToUInt32(entry[5]);
                item.IGCPrice = Convert.ToUInt32(entry[6]);
                item.GRCashPrice = Convert.ToUInt32(entry[7]);
                result.Add(item);
            }
            return result;
        }

        // Returns ALL of a persona's items regardless of itemtype, with the REAL itemtype read
        // from the DB row (entry[3]). GetUserInventoryByBagType uses this so every loadout-bag
        // slot's InventoryID can be resolved by the client (weapons are itemtype 2, armor 8, etc.
        // -- the old per-itemtype filter could only ever return one category at a time).
        public static List<GR5_UserItem> GetAllUserItems(uint pid)
        {
            List<GR5_UserItem> result = new List<GR5_UserItem>();
            List<List<string>> results = GetQueryResults("SELECT * FROM useritems WHERE pid=" + pid);
            foreach (List<string> entry in results)
            {
                GR5_UserItem item = new GR5_UserItem();
                item.InventoryID = Convert.ToUInt32(entry[1]);
                item.PersonaID = pid;
                item.ItemType = Convert.ToByte(entry[3]);
                item.ItemID = Convert.ToUInt32(entry[4]);
                item.OasisName = Convert.ToUInt32(entry[5]);
                item.IGCPrice = Convert.ToUInt32(entry[6]);
                item.GRCashPrice = Convert.ToUInt32(entry[7]);
                result.Add(item);
            }
            return result;
        }

        // Returns ALL of a persona's inventory bags (general + loadout bags), each with its real
        // bagtype and its slots. Used so the client receives every bag at once.
        public static List<GR5_InventoryBag> GetAllInventoryBags(uint pid)
        {
            List<GR5_InventoryBag> result = new List<GR5_InventoryBag>();
            List<int> bagIDs = new List<int>();
            List<uint> bagTypes = new List<uint>();
            List<List<string>> results = GetQueryResults("SELECT * FROM inventorybags WHERE pid=" + pid);
            foreach (List<string> entry in results)
            {
                bagIDs.Add(Convert.ToInt32(entry[0]));
                bagTypes.Add(Convert.ToUInt32(entry[2]));
            }
            for (int i = 0; i < bagIDs.Count; i++)
            {
                GR5_InventoryBag bag = new GR5_InventoryBag();
                bag.m_PersonaID = pid;
                bag.m_InventoryBagType = bagTypes[i];
                bag.m_InventoryBagSlotVector = new List<GR5_InventoryBagSlot>();
                List<List<string>> slotResults = GetQueryResults("SELECT * FROM inventorybagslots WHERE bagid=" + bagIDs[i]);
                foreach (List<string> entry in slotResults)
                {
                    uint invId = SafeU32(entry[2]);
                    // Skip EMPTY slots (inventoryid==0). This is the live loadout/inventory path used by
                    // GetUserInventoryByBagType. An inventoryid==0 slot becomes a key-0 node in the client's
                    // slot hashmap (RDV_cl_InventoryManager::GetUserItemForSlot, keyed by inventoryid) whose
                    // GR5_UserItem is a default (ItemID=0, OasisName=70870). The deploy 3D-preview then loads a
                    // null GAO -> cObjectManager.cpp:854 "Loading Wrong Type ?" hard assert at "SPAWNING INTO GAME".
                    if (invId == 0) continue;
                    GR5_InventoryBagSlot slot = new GR5_InventoryBagSlot();
                    slot.InventoryID = invId;
                    slot.SlotID = SafeU32(entry[3]);
                    slot.Durability = SafeU32(entry[4]);
                    bag.m_InventoryBagSlotVector.Add(slot);
                }
                result.Add(bag);
            }
            return result;
        }

        // === InventoryService persistence helpers (methods 8/11/12/17). ===
        // Schema (verified from the read paths above):
        //   inventorybags:     id(0)  pid(1)  bagtype(2)
        //   inventorybagslots: id(0)  bagid(1)  inventoryid(2)  slotid(3)  durability(4)
        //   useritems:         id(0)  inventoryid(1)  pid(2)  itemtype(3)  itemid(4) ...
        // bagtype 4/5/6 == Assault/Recon/Specialist loadout bags. slotid == the in-bag slot index.
        // inventoryid == the equipped item.

        public static uint GetLoadoutBagType(uint classId)
        {
            return 4 + classId;
        }

        // inventorybags.id for a persona's bag of the given bagtype, or -1 if none.
        public static int GetBagId(uint pid, uint bagType)
        {
            List<List<string>> r = GetQueryResults("SELECT id FROM inventorybags WHERE pid=" + pid + " AND bagtype=" + bagType);
            return r.Count > 0 ? Convert.ToInt32(r[0][0]) : -1;
        }

        public static int GetOrCreateBagId(uint pid, uint bagType)
        {
            int bagId = GetBagId(pid, bagType);
            if (bagId >= 0) return bagId;
            try
            {
                ExecNonQuery("INSERT INTO inventorybags (pid, bagtype) VALUES (" + pid + "," + bagType + ")");
            }
            catch { }
            return GetBagId(pid, bagType);
        }

        // The inventoryid currently sitting in (bag, slot), or 0 if the slot is empty/absent.
        public static uint GetSlotInventoryId(int bagId, uint slotId)
        {
            List<List<string>> r = GetQueryResults("SELECT inventoryid FROM inventorybagslots WHERE bagid=" + bagId + " AND slotid=" + slotId);
            return r.Count > 0 ? Convert.ToUInt32(r[0][0]) : 0;
        }

        // Write the inventoryid into (bag, slot). Only touches an existing row (safe no-op otherwise).
        public static void SetSlotInventoryId(int bagId, uint slotId, uint inventoryId)
        {
            try { ExecNonQuery("UPDATE inventorybagslots SET inventoryid=" + inventoryId + " WHERE bagid=" + bagId + " AND slotid=" + slotId); }
            catch { }
        }

        public static void UpsertSlotInventoryId(int bagId, uint slotId, uint inventoryId)
        {
            if (inventoryId == 0) return;
            try
            {
                List<List<string>> row = GetQueryResults("SELECT id FROM inventorybagslots WHERE bagid=" + bagId + " AND slotid=" + slotId);
                if (row.Count > 0)
                    ExecNonQuery("UPDATE inventorybagslots SET inventoryid=" + inventoryId + " WHERE bagid=" + bagId + " AND slotid=" + slotId);
                else
                    ExecNonQuery("INSERT INTO inventorybagslots (bagid, inventoryid, slotid, durability) VALUES (" + bagId + "," + inventoryId + "," + slotId + ",100)");
            }
            catch { }
        }

        // As above, also setting durability.
        public static void SetSlotInventoryId(int bagId, uint slotId, uint inventoryId, uint durability)
        {
            try { ExecNonQuery("UPDATE inventorybagslots SET inventoryid=" + inventoryId + ", durability=" + durability + " WHERE bagid=" + bagId + " AND slotid=" + slotId); }
            catch { }
        }

        // Decrement an equipped item's durability (clamped at 0), located by inventoryid within the persona's bags.
        public static void ReduceSlotDurabilityByInventoryId(uint pid, uint inventoryId, uint amount)
        {
            try { ExecNonQuery("UPDATE inventorybagslots SET durability = MAX(0, durability - " + amount + ") WHERE inventoryid=" + inventoryId + " AND bagid IN (SELECT id FROM inventorybags WHERE pid=" + pid + ")"); }
            catch { }
        }

        // Resolve a kit's template ItemID to the persona's owned useritems.inventoryid (0 if not owned).
        public static uint GetInventoryIdForItem(uint pid, uint itemId)
        {
            List<List<string>> r = GetQueryResults("SELECT inventoryid FROM useritems WHERE pid=" + pid + " AND itemid=" + itemId);
            return r.Count > 0 ? Convert.ToUInt32(r[0][0]) : 0;
        }

        // Apply a loadout kit to the persona's class-specific loadout bag for method 17
        // (EquipPlayerWithLoadoutKit). IDA ClassInfoRdvPC::ValidatePlayerLoadout confirms slot ids:
        // 1/2/3 weapons, 7 armor, 8 helmet, 9 ability, 10 passive. The selected kit's class is also
        // persisted into personas.lastusedcid so the later DS spawn can build matching abstract/concrete
        // class ids and ClassInfo.
        public static void ApplyLoadoutKit(uint pid, uint kitId)
        {
            GR5_LoadoutKit kit = null;
            foreach (GR5_LoadoutKit k in GetLoadoutKits(pid))
                if (k.m_LoadoutKitID == kitId) { kit = k; break; }
            if (kit == null) return;
            uint classId = kit.m_ClassID;
            SetSelectedClass(pid, classId);
            SetCharacterLoadoutKit(pid, classId, kitId);
            int bagId = GetOrCreateBagId(pid, GetLoadoutBagType(classId));
            if (bagId < 0) return;
            EquipKitSlot(pid, bagId, 1, kit.m_Weapon1ID);
            EquipKitSlot(pid, bagId, 2, kit.m_Weapon2ID);
            EquipKitSlot(pid, bagId, 3, kit.m_Weapon3ID);
            EquipKitSlot(pid, bagId, 7, kit.m_ArmorID);
            EquipKitSlot(pid, bagId, 8, kit.m_HelmetID);
            EquipKitSlot(pid, bagId, 9, kit.m_PowerID != 0 ? kit.m_PowerID : GetDefaultAbilityItem(classId));
            EquipKitSlot(pid, bagId, 10, GetDefaultPassiveAbilityItem(classId));
        }

        // Resolve a kit ItemID to the persona's owned inventoryid and drop it into (loadout bag, slot).
        private static void EquipKitSlot(uint pid, int bagId, uint slotId, uint itemId)
        {
            if (itemId == 0) return;
            uint invId = GetInventoryIdForItem(pid, itemId);
            if (invId == 0) return;                       // not owned -> leave the slot untouched
            UpsertSlotInventoryId(bagId, slotId, invId);
        }

        // The equipped weapon's mapKey for a selected-class loadout slot (bagtype 4/5/6):
        // slot 1 = primary, 2 = secondary. Returns 0 if nothing is equipped / not found.
        // Lets the DS spawn field the player's actual guns.
        public static uint GetLoadoutWeapon(uint pid, uint slotId)
        {
            try
            {
                uint classId = GetSelectedClassID(pid);
                int bagId = GetBagId(pid, GetLoadoutBagType(classId));
                if (bagId < 0) return 0;
                uint invId = GetSlotInventoryId(bagId, slotId);
                if (invId == 0) return 0;
                List<List<string>> r = GetQueryResults("SELECT itemid FROM useritems WHERE inventoryid=" + invId + " AND pid=" + pid);
                return r.Count > 0 ? Convert.ToUInt32(r[0][0]) : 0;
            }
            catch { return 0; }
        }

        public static void SetSelectedClass(uint pid, uint classId)
        {
            try { ExecNonQuery("UPDATE personas SET lastusedcid=" + classId + " WHERE pid=" + pid); }
            catch { }
        }

        public static void SetCharacterLoadoutKit(uint pid, uint classId, uint kitId)
        {
            try { ExecNonQuery("UPDATE characters SET kit=" + kitId + " WHERE pid=" + pid + " AND class=" + classId); }
            catch { }
        }

        public static uint GetSelectedClassID(uint pid)
        {
            try
            {
                List<List<string>> r = GetQueryResults("SELECT lastusedcid FROM personas WHERE pid=" + pid);
                uint classId = r.Count > 0 ? SafeU32(r[0][0]) : 0;
                if (classId > 2) classId = 0;
                return classId;
            }
            catch { return 0; }
        }

        public static uint GetDefaultAbilityItem(uint classId)
        {
            List<List<string>> r = GetQueryResults("SELECT iid FROM abilities WHERE classID=" + classId + " ORDER BY iid LIMIT 1");
            return r.Count > 0 ? SafeU32(r[0][0]) : 0;
        }

        public static uint GetDefaultPassiveAbilityItem(uint classId)
        {
            List<List<string>> r = GetQueryResults("SELECT iid FROM passiveabilities WHERE classID=" + classId + " ORDER BY iid LIMIT 1");
            return r.Count > 0 ? SafeU32(r[0][0]) : 0;
        }

        // Per-shot BASE damage for a weapon, keyed by its mapKey (== useritems.itemid == templateitems.iid ==
        // SpawnLoadoutInfo.MainWeaponID == OCP_PlayerEntity.mainWeaponID). Source: the weapon's synthetic stat
        // component (tempcomponentlists.value in 700000-799999, the extracted retail per-weapon stat list) ->
        // skillmodifiers proptype 2 (damage) modtype 2; each weapon has exactly ONE such row (verified). This is the
        // in-DB RETAIL base damage vs 100 HP (e.g. M27=30.6, F2000=38.3, Mk17=47.7, Mk5=40.2, M249=32.0, P250=24.1,
        // M24=78.0 -> 2-5 hits to kill). Returns 0 on any miss (grenade/throwable/unknown) so the caller falls back
        // to the flat Global.realHitDamage. The hit cmd carries no damage -- retail's DS computed it from the weapon
        // (AI_EntityPlayer::GetDamageData), which is the role the emulated DS plays. Range falloff / headshot-crit /
        // armor mitigation are refinements layered on later (see gro-combat-wire-protocol).
        public static float GetWeaponDamage(uint weaponMapKey)
        {
            if (weaponMapKey == 0) return 0f;
            try
            {
                List<List<string>> r = GetQueryResults(
                    "SELECT s.methodval FROM tempcomponentlists t JOIN skillmodifiers s ON s.listid = t.value " +
                    "WHERE t.key = " + weaponMapKey + " AND t.value >= 700000 AND t.value < 800000 " +
                    "AND s.proptype = 2 AND s.modtype = 2 LIMIT 1");
                if (r.Count > 0)
                {
                    float dmg;
                    if (float.TryParse(r[0][0], System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out dmg) && dmg > 0f)
                        return dmg;
                }
            }
            catch { }
            return 0f;
        }

        // The persona display NAME (personas.name) for the in-match abstract m_PersonaName -> kill-feed names + name tags.
        public static string GetPersonaName(uint pid)
        {
            try { List<List<string>> r = GetQueryResults("SELECT name FROM personas WHERE pid=" + pid + " LIMIT 1"); if (r.Count > 0) return r[0][0] ?? ""; }
            catch { }
            return "";
        }

        // The client-side weaponID (weapons.weaponID, small 1-66) for a weapon mapKey -- what AI_EntityPlayer::GetWeaponByID
        // matches to select the kill-feed ICON. Passing the mapKey instead matched nothing -> the feed fell back to the
        // headshot icon. 0 on miss.
        public static uint GetWeaponWeaponID(uint weaponMapKey)
        {
            if (weaponMapKey == 0) return 0;
            try { List<List<string>> r = GetQueryResults("SELECT weaponID FROM weapons WHERE mapKey=" + weaponMapKey + " LIMIT 1"); if (r.Count > 0) return SafeU32(r[0][0]); }
            catch { }
            return 0;
        }

        public static uint GetTemplateItemType(uint itemId)
        {
            List<List<string>> r = GetQueryResults("SELECT type FROM templateitems WHERE iid=" + itemId);
            return r.Count > 0 ? SafeU32(r[0][0]) : 0;
        }

        public static uint GetItemIdForInventory(uint pid, uint inventoryId)
        {
            List<List<string>> r = GetQueryResults("SELECT itemid FROM useritems WHERE pid=" + pid + " AND inventoryid=" + inventoryId);
            return r.Count > 0 ? SafeU32(r[0][0]) : 0;
        }

        public static uint GetInventoryIdForLoadoutSlot(uint pid, uint classId, uint slotId)
        {
            int bagId = GetBagId(pid, GetLoadoutBagType(classId));
            return bagId < 0 ? 0 : GetSlotInventoryId(bagId, slotId);
        }

        public static uint GetAbilityType(uint abilityItemId)
        {
            List<List<string>> r = GetQueryResults("SELECT abilitytype FROM abilities WHERE iid=" + abilityItemId);
            return r.Count > 0 ? SafeU32(r[0][0]) : 0;
        }

        public static uint GetPassiveAbilityType(uint passiveItemId)
        {
            List<List<string>> r = GetQueryResults("SELECT type FROM passiveabilities WHERE iid=" + passiveItemId);
            return r.Count > 0 ? SafeU32(r[0][0]) : 0;
        }

        public static uint GetHelmetAssetKey(uint helmetItemId)
        {
            List<List<string>> r = GetQueryResults("SELECT assetkey FROM armoritems WHERE iid=" + helmetItemId);
            return r.Count > 0 ? SafeU32(r[0][0]) : 0;
        }

        private static bool ApplyItemIdIfType(uint pid, SpawnLoadoutInfo info, uint slotId, uint expectedType)
        {
            uint invId = GetInventoryIdForLoadoutSlot(pid, info.ClassID, slotId);
            if (invId == 0) return false;
            uint itemId = GetItemIdForInventory(pid, invId);
            if (itemId == 0 || GetTemplateItemType(itemId) != expectedType) return false;
            switch (slotId)
            {
                case 1: info.MainWeaponInventoryID = invId; info.MainWeaponID = itemId; break;
                case 2: info.PistolWeaponInventoryID = invId; info.PistolWeaponID = itemId; break;
                case 3: info.GrenadeWeaponInventoryID = invId; info.GrenadeWeaponID = itemId; break;
                case 7: info.ArmorInventoryID = invId; break;
                case 8: info.HelmetInventoryID = invId; info.HelmetKey = GetHelmetAssetKey(itemId); break;
                case 9: info.AbilityInventoryID = invId; info.AbilityType = (byte)GetAbilityType(itemId); break;
                case 10: info.PassiveAbilityInventoryID = invId; info.PassiveAbilityType = (byte)GetPassiveAbilityType(itemId); break;
            }
            return true;
        }

        public static SpawnLoadoutInfo GetSpawnLoadout(uint pid)
        {
            SpawnLoadoutInfo info = new SpawnLoadoutInfo();
            info.ClassID = (byte)GetSelectedClassID(pid);
            info.BagType = GetLoadoutBagType(info.ClassID);
            info.FaceID = 1;
            info.SkinID = 1;
            info.MainWeaponID = 170;
            info.PistolWeaponID = 339;
            info.HelmetKey = 0xF8700A85;
            info.Source = "fallback";
            bool abilityResolved = false;
            bool passiveResolved = false;

            try
            {
                List<List<string>> chars = GetQueryResults("SELECT face, skin, kit FROM characters WHERE pid=" + pid + " AND class=" + info.ClassID);
                if (chars.Count > 0)
                {
                    info.FaceID = SafeU8(chars[0][0]);
                    info.SkinID = SafeU8(chars[0][1]);
                    info.KitID = SafeU32(chars[0][2]);
                }

                uint defaultAbilityItem = GetDefaultAbilityItem(info.ClassID);
                if (defaultAbilityItem != 0)
                {
                    info.AbilityInventoryID = GetInventoryIdForItem(pid, defaultAbilityItem);
                    info.AbilityType = (byte)GetAbilityType(defaultAbilityItem);
                    abilityResolved = true;
                }
                uint defaultPassiveItem = GetDefaultPassiveAbilityItem(info.ClassID);
                if (defaultPassiveItem != 0)
                {
                    info.PassiveAbilityInventoryID = GetInventoryIdForItem(pid, defaultPassiveItem);
                    info.PassiveAbilityType = (byte)GetPassiveAbilityType(defaultPassiveItem);
                    passiveResolved = true;
                }

                GR5_LoadoutKit kit = null;
                foreach (GR5_LoadoutKit k in GetLoadoutKits(pid))
                    if (k.m_ClassID == info.ClassID && (k.m_LoadoutKitID == info.KitID || kit == null))
                        kit = k;
                if (kit != null)
                {
                    info.KitID = kit.m_LoadoutKitID;
                    if (kit.m_Weapon1ID != 0) info.MainWeaponID = kit.m_Weapon1ID;
                    if (kit.m_Weapon2ID != 0) info.PistolWeaponID = kit.m_Weapon2ID;
                    if (kit.m_Weapon3ID != 0) info.GrenadeWeaponID = kit.m_Weapon3ID;
                    if (kit.m_ArmorID != 0) info.ArmorInventoryID = GetInventoryIdForItem(pid, kit.m_ArmorID);
                    if (kit.m_HelmetID != 0)
                    {
                        info.HelmetInventoryID = GetInventoryIdForItem(pid, kit.m_HelmetID);
                        info.HelmetKey = GetHelmetAssetKey(kit.m_HelmetID);
                    }
                    uint abilityItem = kit.m_PowerID != 0 ? kit.m_PowerID : defaultAbilityItem;
                    if (abilityItem != 0)
                    {
                        info.AbilityInventoryID = GetInventoryIdForItem(pid, abilityItem);
                        info.AbilityType = (byte)GetAbilityType(abilityItem);
                        abilityResolved = true;
                    }
                    uint passiveItem = defaultPassiveItem;
                    if (passiveItem != 0)
                    {
                        info.PassiveAbilityInventoryID = GetInventoryIdForItem(pid, passiveItem);
                        info.PassiveAbilityType = (byte)GetPassiveAbilityType(passiveItem);
                        passiveResolved = true;
                    }
                    info.Source = "kit";
                }

                ApplyItemIdIfType(pid, info, 1, 2);
                ApplyItemIdIfType(pid, info, 2, 2);
                ApplyItemIdIfType(pid, info, 3, 2);
                ApplyItemIdIfType(pid, info, 7, 10);
                ApplyItemIdIfType(pid, info, 8, 8);
                if (ApplyItemIdIfType(pid, info, 9, 11)) abilityResolved = true;
                if (ApplyItemIdIfType(pid, info, 10, 13)) passiveResolved = true;
                info.Source += "+bag" + info.BagType;

                if (info.MainWeaponID == 0) info.MainWeaponID = 170;
                if (info.PistolWeaponID == 0) info.PistolWeaponID = 339;
                if (!abilityResolved) info.AbilityType = 6;
                if (!passiveResolved) info.PassiveAbilityType = 3;
                if (info.HelmetKey == 0) info.HelmetKey = 0xF8700A85;
            }
            catch { }
            return info;
        }

        // Component map-key list for a weapon, by its weapons.mapKey (== componentListID).
        // Lets the spawn create-blob build each weapon's real component set instead of a hardcoded M27.
        public static List<uint> GetWeaponComponentList(uint mapKey)
        {
            List<uint> result = new List<uint>();
            foreach (List<string> e in GetQueryResults("SELECT value FROM tempcomponentlists WHERE key=" + mapKey))
                result.Add(Convert.ToUInt32(e[0]));
            return result;
        }

        // === Weapon part-customization persistence (StoreService 22/23 "apply") ===
        // A weapon INSTANCE's chosen component set, keyed by the weapon's useritems.inventoryid, so two
        // copies of the same weapon can carry different parts. Overrides the by-mapKey DEFAULT
        // (GetWeaponComponentList) in the inventory + customize-complete responses, so the picked parts
        // show on the tile/3D-preview and survive relogin.
        private static bool _customCompTableReady = false;
        private static void EnsureCustomCompTable()
        {
            if (_customCompTableReady) return;
            try { ExecNonQuery("CREATE TABLE IF NOT EXISTS weaponcustomcomponents (pid INTEGER, inventoryid INTEGER, components TEXT, PRIMARY KEY(pid, inventoryid))"); }
            catch { }
            _customCompTableReady = true;
        }

        // itemid (== mapKey) of a useritems row by its inventoryid, or 0.
        public static uint GetItemIdByInventoryId(uint inventoryId)
        {
            List<List<string>> r = GetQueryResults("SELECT itemid FROM useritems WHERE inventoryid=" + inventoryId);
            return r.Count > 0 ? SafeU32(r[0][0]) : 0;
        }

        // The inventoryid sitting in (persona, bagtype, slotid), or 0 (generic, any-bag form).
        public static uint ResolveInventoryIdBySlot(uint pid, uint bagType, uint slotId)
        {
            int bagId = GetBagId(pid, bagType);
            if (bagId < 0) return 0;
            return GetSlotInventoryId(bagId, slotId);
        }

        // componentType keyed by component mapKey (one query), to merge a customize delta by slot/type.
        private static Dictionary<uint, uint> GetComponentTypeMap()
        {
            Dictionary<uint, uint> map = new Dictionary<uint, uint>();
            foreach (List<string> e in GetQueryResults("SELECT mapKey,type FROM components"))
                map[SafeU32(e[0])] = SafeU32(e[1]);
            return map;
        }

        // Merge a customize pick-list (the CHANGED components) over the weapon's DEFAULT component list:
        // each chosen component REPLACES the default of the same componentType (or is added if the weapon
        // had none of that type). Returns the FULL component set the weapon should now show/build with.
        public static List<uint> BuildMergedComponentList(uint weaponMapKey, List<uint> chosenComps)
        {
            List<uint> defaults = GetWeaponComponentList(weaponMapKey);
            if (chosenComps == null || chosenComps.Count == 0) return defaults;
            Dictionary<uint, uint> typeOf = GetComponentTypeMap();
            HashSet<uint> chosenTypes = new HashSet<uint>();
            foreach (uint c in chosenComps)
                chosenTypes.Add(typeOf.ContainsKey(c) ? typeOf[c] : 0xFFFFFFFFu);
            List<uint> merged = new List<uint>();
            foreach (uint d in defaults)
            {
                uint dt = typeOf.ContainsKey(d) ? typeOf[d] : 0xFFFFFFFFu;
                if (!chosenTypes.Contains(dt)) merged.Add(d);
            }
            merged.AddRange(chosenComps);
            return merged;
        }

        // Persist a weapon instance's custom component list (overwrites any prior set for that inventoryid).
        public static void SaveCustomWeaponComponents(uint pid, uint inventoryId, List<uint> components)
        {
            EnsureCustomCompTable();
            string csv = string.Join(",", components);
            try { ExecNonQuery("INSERT OR REPLACE INTO weaponcustomcomponents (pid,inventoryid,components) VALUES (" + pid + "," + inventoryId + ",'" + csv + "')"); }
            catch { }
        }

        // The component list to SERVE for a weapon instance: the persisted CUSTOM set if the player has
        // customized this exact inventoryid, else the by-mapKey default. Safe on DBs lacking the table.
        public static List<uint> GetWeaponComponentListForInstance(uint pid, uint inventoryId, uint weaponMapKey)
        {
            try
            {
                List<List<string>> r = GetQueryResults("SELECT components FROM weaponcustomcomponents WHERE pid=" + pid + " AND inventoryid=" + inventoryId);
                if (r.Count > 0 && !string.IsNullOrEmpty(r[0][0]))
                {
                    List<uint> list = new List<uint>();
                    foreach (string x in r[0][0].Split(','))
                        if (x.Length > 0) list.Add(SafeU32(x));
                    if (list.Count > 0) return list;
                }
            }
            catch { }
            return GetWeaponComponentList(weaponMapKey);
        }

        // Oasis localization name id for an item/weapon, by templateitems.iid (== weapon mapKey).
        public static uint GetItemOasisName(uint iid)
        {
            List<List<string>> r = GetQueryResults("SELECT oname FROM templateitems WHERE iid=" + iid);
            return r.Count > 0 ? Convert.ToUInt32(r[0][0]) : 0;
        }

        public static List<GR5_Ability> GetAbilities()
        {
            List<GR5_Ability> result = new List<GR5_Ability>();
            List<List<string>> results = GetQueryResults("SELECT * FROM abilities");
            foreach (List<string> entry in results)
            {
                GR5_Ability a = new GR5_Ability();
                a.Id = Convert.ToUInt32(entry[1]);
                a.SlotCount = Convert.ToByte(entry[2]);
                a.ClassID = Convert.ToByte(entry[3]);
                a.AbilityType = Convert.ToByte(entry[4]);
                a.ModifierListId = Convert.ToUInt32(entry[5]);
                result.Add(a);
            }
            return result;
        }

        public static List<GR5_LoadoutKit> GetLoadoutKits(uint pid)
        {
            List<GR5_LoadoutKit> result = new List<GR5_LoadoutKit>();
            List<List<string>> results = GetQueryResults("SELECT * FROM loadoutkits WHERE pid=" + pid);
            foreach (List<string> entry in results)
            {
                GR5_LoadoutKit kit = new GR5_LoadoutKit();
                kit.m_LoadoutKitID = Convert.ToUInt32(entry[2]);
                kit.m_ClassID = Convert.ToUInt32(entry[3]);
                kit.m_Weapon1ID = Convert.ToUInt32(entry[4]);
                kit.m_Weapon2ID = Convert.ToUInt32(entry[5]);
                kit.m_Weapon3ID = Convert.ToUInt32(entry[6]);
                kit.m_Item1ID = Convert.ToUInt32(entry[7]);
                kit.m_Item2ID = Convert.ToUInt32(entry[8]);
                kit.m_Item3ID = Convert.ToUInt32(entry[9]);
                kit.m_PowerID = Convert.ToUInt32(entry[10]);
                kit.m_HelmetID = Convert.ToUInt32(entry[11]);
                kit.m_ArmorID = Convert.ToUInt32(entry[12]);
                kit.m_OasisDesc = Convert.ToUInt32(entry[13]);
                kit.m_Flag = Convert.ToUInt32(entry[14]);
                result.Add(kit);
            }
            return result;
        }

        public static List<GR5_ArmorInsert> GetArmorInserts()
        {
            List<GR5_ArmorInsert> result = new List<GR5_ArmorInsert>();
            List<List<string>> results = GetQueryResults("SELECT * FROM armorinserts");
            foreach (List<string> entry in results)
            {
                GR5_ArmorInsert insert = new GR5_ArmorInsert();
                insert.Id = Convert.ToUInt32(entry[1]);
                insert.Type = Convert.ToByte(entry[2]);
                insert.AssetKey = Convert.ToUInt32(entry[3]);
                insert.ModifierListID = Convert.ToUInt32(entry[4]);
                insert.CharacterID = Convert.ToByte(entry[5]);
                result.Add(insert);
            }
            return result;
        }

        public static List<GR5_ArmorItem> GetArmorItems()
        {
            List<GR5_ArmorItem> result = new List<GR5_ArmorItem>();
            List<List<string>> results = GetQueryResults("SELECT * FROM armoritems");
            foreach (List<string> entry in results)
            {
                GR5_ArmorItem item = new GR5_ArmorItem();
                item.Id = Convert.ToUInt32(entry[1]);
                item.Type = Convert.ToByte(entry[2]);
                item.AssetKey = Convert.ToUInt32(entry[3]);
                item.ModifierListID = Convert.ToUInt32(entry[4]);
                item.CharacterID = Convert.ToByte(entry[5]);
                result.Add(item);
            }
            return result;
        }

        public static List<GR5_ArmorTier> GetArmorTiers()
        {
            List<GR5_ArmorTier> result = new List<GR5_ArmorTier>();
            List<List<string>> results = GetQueryResults("SELECT * FROM armortiers");
            foreach (List<string> entry in results)
            {
                GR5_ArmorTier tier = new GR5_ArmorTier();
                tier.Id = Convert.ToUInt32(entry[1]);
                tier.Type = Convert.ToByte(entry[2]);
                tier.Tier = Convert.ToByte(entry[3]);
                tier.ClassID = Convert.ToByte(entry[4]);
                tier.UnlockLevel = Convert.ToByte(entry[5]);
                tier.InsertSlots = Convert.ToByte(entry[6]);
                tier.AssetKey = Convert.ToUInt32(entry[7]);
                tier.ModifierListId = Convert.ToUInt32(entry[8]);
                result.Add(tier);
            }
            return result;
        }

        public static List<GR5_PersonaArmorTier> GetPersonaArmorTiers(uint pid, uint tier)
        {
            List<GR5_PersonaArmorTier> result = new List<GR5_PersonaArmorTier>();
            List<int> IDs = new List<int>();
            List<uint> tierIDs = new List<uint>();
            List<List<string>> results = GetQueryResults("SELECT * FROM personaarmortiers WHERE tierid=" + tier);
            foreach (List<string> entry in results)
            {
                IDs.Add(Convert.ToInt32(entry[0]));
                tierIDs.Add(Convert.ToUInt32(entry[1]));
            }
            for(int i = 0; i < IDs.Count;i++)
            {
                GR5_PersonaArmorTier pat = new GR5_PersonaArmorTier();
                pat.ArmorTierID = tierIDs[i];
                pat.Inserts = new List<GR5_ArmorInsertSlot>();
                results = GetQueryResults("SELECT * FROM armorinsertslots WHERE patid=" + IDs[i]);
                foreach (List<string> entry in results)
                {
                    GR5_ArmorInsertSlot slot = new GR5_ArmorInsertSlot();
                    slot.InsertID = Convert.ToUInt32(entry[2]);
                    slot.Durability = Convert.ToUInt32(entry[3]);
                    slot.SlotID = Convert.ToByte(entry[4]);
                    pat.Inserts.Add(slot);
                }
                result.Add(pat);
            }
            return result;
        }

        // The 12 type-21 "CombatProperty" modifiers for a player's EQUIPPED armor, resolved from their
        // loadout (inventory). The armor loadout slot holds an armortier (templateitems type 10); the tier's
        // modefierlistid -> skillmodifiers rows of modtype 21 are the combat properties, each placed at
        // slot[proptype] -- the GetCombatProperty(index) the client reads off AI_EntityPlayer+0x4B8. The values
        // are additive/fractional with a NEUTRAL identity of 0.0, so any index lacking a modifier stays 0.0.
        // Only the armor TIER carries modtype-21 in this catalog (cross-checked: armoritems lists are modtype-2,
        // armorinserts lists are modtype-12 -- neither holds a modtype-21 row), so the equipped tier alone is
        // the complete source. Returns 12 zeros on any miss (no/garbage tier, the starter tier-1 which has no
        // combat bonus by design, pid==0, or a DB hiccup) -- the safe neutral the client expects. methodval is
        // TEXT and is parsed with InvariantCulture so a non-"." locale can't corrupt it. See ClassInfo_Armor /
        // gro-combat-input-lock.
        public static float[] GetArmorCombatProperties(uint pid, uint armorInventoryId)
        {
            float[] props = new float[12];   // 0.0 = neutral identity for every combat property
            try
            {
                if (armorInventoryId == 0) return props;
                uint itemId = (pid != 0) ? GetItemIdForInventory(pid, armorInventoryId) : 0;
                if (itemId == 0) itemId = GetItemIdByInventoryId(armorInventoryId);   // pid-agnostic fallback
                if (itemId == 0) return props;
                List<List<string>> tier = GetQueryResults("SELECT modefierlistid FROM armortiers WHERE iid=" + itemId);
                if (tier.Count == 0) return props;   // equipped item isn't an armortier -> no combat properties
                uint listId = SafeU32(tier[0][0]);
                if (listId == 0) return props;
                foreach (List<string> e in GetQueryResults(
                    "SELECT proptype, methodval FROM skillmodifiers WHERE modtype=21 AND listid=" + listId))
                {
                    int idx = (int)SafeU32(e[0]);
                    if (idx < 0 || idx >= props.Length) continue;
                    float val;
                    if (float.TryParse(e[1], System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out val))
                        props[idx] += val;   // additive aggregate onto the neutral 0.0 base
                }
            }
            catch { }
            return props;
        }

        // === Per-persona armor INSERT loadout (the 4 ClassInfo_Armor defensive scalars) ===
        // Each owned armor insert (armorinserts.iid) carries a modtype-12 modifier whose proptype selects a
        // defensive stat. Inserts are equipped into the slots of the persona's armor tier instance and persisted
        // here, keyed (pid, armortieriid, slotid) -> insertiid. (The legacy personaarmortiers/armorinsertslots
        // tables aren't pid-keyed, so this clean table mirrors weaponcustomcomponents instead.)
        private static bool _armorInsertTableReady = false;
        private static void EnsureArmorInsertTable()
        {
            if (_armorInsertTableReady) return;
            try { ExecNonQuery("CREATE TABLE IF NOT EXISTS personaarmorinserts (pid INTEGER, armortieriid INTEGER, slotid INTEGER, insertiid INTEGER, PRIMARY KEY(pid, armortieriid, slotid))"); }
            catch { }   // DS opens the DB read-only -> CREATE fails here; the backend (read-write) creates it.
            _armorInsertTableReady = true;
        }

        // Equip (insertIid != 0) or clear (insertIid == 0) one insert slot on a persona's armor tier instance.
        public static void SaveArmorInsert(uint pid, uint armorTierIid, uint slotId, uint insertIid)
        {
            EnsureArmorInsertTable();
            try
            {
                if (insertIid == 0)
                    ExecNonQuery("DELETE FROM personaarmorinserts WHERE pid=" + pid + " AND armortieriid=" + armorTierIid + " AND slotid=" + slotId);
                else
                    ExecNonQuery("INSERT OR REPLACE INTO personaarmorinserts (pid,armortieriid,slotid,insertiid) VALUES (" + pid + "," + armorTierIid + "," + slotId + "," + insertIid + ")");
            }
            catch { }
        }

        // The insert item iids equipped on a persona's armor tier instance, ordered by slot.
        public static List<uint> GetEquippedArmorInsertIids(uint pid, uint armorTierIid)
        {
            List<uint> result = new List<uint>();
            EnsureArmorInsertTable();
            try
            {
                foreach (List<string> e in GetQueryResults("SELECT insertiid FROM personaarmorinserts WHERE pid=" + pid + " AND armortieriid=" + armorTierIid + " ORDER BY slotid"))
                    result.Add(SafeU32(e[0]));
            }
            catch { }
            return result;
        }

        // The 4 ClassInfo_Armor defensive scalars aggregated from the player's EQUIPPED armor inserts.
        // armorInventoryId -> tier iid -> equipped inserts -> each insert's modefierlistid -> skillmodifiers
        // (modtype 12). proptype selects the stat (inferred from the XSAPI-H/R/T/C insert names + the
        // ClassInfo_Armor field order): 1=bonusHealth, 2=bonusHealthRegen, 3=toughness, 4=criticalMitigation;
        // methodval is summed. Returns {0,0,0,0} when no inserts are equipped (the real "no bonus" baseline).
        // Index map: [0]=bonusHealth [1]=bonusHealthRegen [2]=toughness [3]=criticalMitigation.
        public static float[] GetArmorScalars(uint pid, uint armorInventoryId)
        {
            float[] sc = new float[4];
            try
            {
                if (armorInventoryId == 0) return sc;
                uint tierIid = (pid != 0) ? GetItemIdForInventory(pid, armorInventoryId) : 0;
                if (tierIid == 0) tierIid = GetItemIdByInventoryId(armorInventoryId);
                if (tierIid == 0) return sc;
                foreach (uint insertIid in GetEquippedArmorInsertIids(pid, tierIid))
                {
                    List<List<string>> lr = GetQueryResults("SELECT modefierlistid FROM armorinserts WHERE iid=" + insertIid);
                    if (lr.Count == 0) continue;
                    uint listId = SafeU32(lr[0][0]);
                    if (listId == 0) continue;
                    foreach (List<string> e in GetQueryResults("SELECT proptype, methodval FROM skillmodifiers WHERE modtype=12 AND listid=" + listId))
                    {
                        int prop = (int)SafeU32(e[0]);
                        float val;
                        if (!float.TryParse(e[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out val)) continue;
                        switch (prop)
                        {
                            case 1: sc[0] += val; break;   // bonusHealth        (XSAPI-H)
                            case 2: sc[1] += val; break;   // bonusHealthRegen   (XSAPI-R)
                            case 3: sc[2] += val; break;   // toughness          (XSAPI-T)
                            case 4: sc[3] += val; break;   // criticalMitigation (XSAPI-C)
                        }
                    }
                }
            }
            catch { }
            return sc;
        }

        // The persona's equipped-insert loadout as GR5_PersonaArmorTier rows (for the StoreService 31 /
        // ArmorService serve-back so the menu reflects equipped inserts). Built from personaarmorinserts.
        public static List<GR5_PersonaArmorTier> GetPersonaArmorTiersWithInserts(uint pid)
        {
            List<GR5_PersonaArmorTier> result = new List<GR5_PersonaArmorTier>();
            EnsureArmorInsertTable();
            try
            {
                Dictionary<uint, GR5_PersonaArmorTier> byTier = new Dictionary<uint, GR5_PersonaArmorTier>();
                foreach (List<string> e in GetQueryResults("SELECT armortieriid, slotid, insertiid FROM personaarmorinserts WHERE pid=" + pid + " ORDER BY armortieriid, slotid"))
                {
                    uint tierIid = SafeU32(e[0]);
                    GR5_PersonaArmorTier pat;
                    if (!byTier.TryGetValue(tierIid, out pat))
                    {
                        pat = new GR5_PersonaArmorTier { ArmorTierID = tierIid };
                        byTier[tierIid] = pat;
                        result.Add(pat);
                    }
                    pat.Inserts.Add(new GR5_ArmorInsertSlot { InsertID = SafeU32(e[2]), Durability = 100, SlotID = SafeU8(e[1]) });
                }
            }
            catch { }
            return result;
        }

        public static List<GR5_SkillModifier> GetSkillModifiers()
        {
            List<GR5_SkillModifier> result = new List<GR5_SkillModifier>();
            List<List<string>> results = GetQueryResults("SELECT * FROM skillmodifiers");
            foreach (List<string> entry in results)
            {
                GR5_SkillModifier mod = new GR5_SkillModifier
                {
                    m_ModifierID = Convert.ToUInt32(entry[2]),
                    m_ModifierType = Convert.ToByte(entry[3]),
                    m_PropertyType = Convert.ToByte(entry[4]),
                    m_MethodType = Convert.ToByte(entry[5]),
                    m_MethodValue = entry[6]
                };
                result.Add(mod);
            }
            return result;
        }

        public static List<GR5_SkillModifierList> GetSkillModifierLists()
        {
            List<GR5_SkillModifierList> result = new List<GR5_SkillModifierList>();
            List<List<string>> results = GetQueryResults("SELECT * FROM skillmodifiers");
            foreach (List<string> entry in results)
            {
                bool found = false;
                GR5_SkillModifierList target = null;
                foreach(GR5_SkillModifierList list in result)
                    if (list.m_ID == Convert.ToUInt32(entry[1]))
                    {
                        found = true;
                        target = list;
                        break;
                    }
                if (!found)
                {
                    target = new GR5_SkillModifierList();
                    target.m_ID = Convert.ToUInt32(entry[1]);
                    result.Add(target);
                }
                target.m_ModifierIDVector.Add(Convert.ToUInt32(entry[2]));
            }
            return result;
        }

        public static List<GR5_FaceSkinTone> GetFaceSkinTones()
        {
            List<GR5_FaceSkinTone> result = new List<GR5_FaceSkinTone>();
            List<List<string>> results = GetQueryResults("SELECT * FROM faceskintones");
            foreach (List<string> entry in results)
            {
                GR5_FaceSkinTone mod = new GR5_FaceSkinTone();
                mod.id = Convert.ToUInt32(entry[1]);
                mod.objectType= Convert.ToByte(entry[2]);
                mod.objectKey = Convert.ToUInt32(entry[3]);
                mod.oasisName = Convert.ToUInt32(entry[4]);
                result.Add(mod);
            }
            return result;
        }

        public static List<Map_U32_GR5_Weapon> GetTemplateWeaponList()
        {
            List<Map_U32_GR5_Weapon> result = new List<Map_U32_GR5_Weapon>();
            List<List<string>> results = GetQueryResults("SELECT * FROM weapons");
            foreach (List<string> entry in results)
            {
                Map_U32_GR5_Weapon pair = new Map_U32_GR5_Weapon
                {
                    key = Convert.ToUInt32(entry[1]),
                    weapon = new GR5_Weapon
                    {
                        weaponID = Convert.ToUInt32(entry[2]),
                        classTypeID = Convert.ToUInt32(entry[3]),
                        weaponType = Convert.ToUInt32(entry[4]),
                        equippableClassTypeID = Convert.ToUInt32(entry[5]),
                        flags = Convert.ToUInt32(entry[6])
                    }
                };
                result.Add(pair);
            }
            return result;
        }

        public static List<Map_U32_GR5_Component> GetComponents()
        {
            List<Map_U32_GR5_Component> result = new List<Map_U32_GR5_Component>();
            List<List<string>> results = GetQueryResults("SELECT * FROM components");
            foreach (List<string> entry in results)
            {
                Map_U32_GR5_Component pair = new Map_U32_GR5_Component
                {
                    key = Convert.ToUInt32(entry[1]),
                    component = new GR5_Component
                    {
                        componentID = Convert.ToUInt32(entry[2]),
                        componentKey = Convert.ToUInt32(entry[3]),
                        componentType = Convert.ToByte(entry[4]),
                        boneStructure = Convert.ToUInt32(entry[5]),
                        modifierListID = Convert.ToUInt32(entry[6])
                    }
                };
                result.Add(pair);
            }
            return result;
        }

        public static List<Map_U32_VectorU32> GetTemplateComponentLists()
        {
            List<Map_U32_VectorU32> result = new List<Map_U32_VectorU32>();
            List<List<string>> results = GetQueryResults("SELECT * FROM tempcomponentlists");
            foreach (List<string> entry in results)
            {
                uint key = Convert.ToUInt32(entry[1]);
                uint value = Convert.ToUInt32(entry[2]);
                bool found = false;
                foreach (Map_U32_VectorU32 pair in result)
                    if (pair.key == key)
                    {
                        found = true;
                        pair.vector.Add(value);
                        break;
                    }
                if (!found)
                {
                    Map_U32_VectorU32 pair = new Map_U32_VectorU32();
                    pair.key = key;
                    pair.vector.Add(value);
                    result.Add(pair);
                }
            }
            return result;
        }

        public static List<Map_U32_VectorU32> GetWeaponCompatibilityBridge()
        {
            List<Map_U32_VectorU32> result = new List<Map_U32_VectorU32>();
            List<List<string>> results = GetQueryResults("SELECT * FROM weaponcompatbridge");
            foreach (List<string> entry in results)
            {
                uint key = Convert.ToUInt32(entry[1]);
                uint value = Convert.ToUInt32(entry[2]);
                bool found = false;
                foreach (Map_U32_VectorU32 pair in result)
                    if (pair.key == key)
                    {
                        found = true;
                        pair.vector.Add(value);
                        break;
                    }
                if (!found)
                {
                    Map_U32_VectorU32 pair = new Map_U32_VectorU32();
                    pair.key = key;
                    pair.vector.Add(value);
                    result.Add(pair);
                }
            }
            return result;
        }

        public static List<GR5_AMM_Playlist> GetAMMPlaylists()
        {
            List<GR5_AMM_Playlist> result = new List<GR5_AMM_Playlist>();
            List<List<string>> results = GetQueryResults("SELECT * FROM amm_playlists");
            foreach (List<string> entry in results)
            {
                GR5_AMM_Playlist pl = new GR5_AMM_Playlist
                {
                    uiId = Convert.ToUInt32(entry[1]),
                    uiNodeType = Convert.ToUInt32(entry[2]),
                    uiMaxTeamSize = Convert.ToUInt32(entry[3]),
                    uiMinTeamSize = Convert.ToUInt32(entry[4]),
                    uiOasisNameId = Convert.ToUInt32(entry[5]),
                    uiOasisDescriptionId = Convert.ToUInt32(entry[6]),
                    uiIsRepeatable = Convert.ToUInt32(entry[7]),
                    uiIsRandom = Convert.ToUInt32(entry[8]),
                    uiThumbnailId = Convert.ToUInt32(entry[9]),
                    m_PlaylistEntryVector = new List<GR5_AMM_PlaylistEntry>()
                };
                List<List<string>> results2 = GetQueryResults("SELECT * FROM amm_playlistentries WHERE listID=" + pl.uiId);
                foreach (List<string> entry2 in results2)
                {
                    GR5_AMM_PlaylistEntry ple = new GR5_AMM_PlaylistEntry
                    {
                        uiMapId = Convert.ToUInt32(entry2[2]),
                        uiGameMode = Convert.ToUInt32(entry2[3]),
                        uiMatchDetail = Convert.ToUInt32(entry2[4])
                    };
                    pl.m_PlaylistEntryVector.Add(ple);
                }
                result.Add(pl);
            }
            return result;
        }

        public static List<GR5_AMM_Map> GetAMMMaps()
        {
            List<GR5_AMM_Map> result = new List<GR5_AMM_Map>();
            List<List<string>> results = GetQueryResults("SELECT * FROM amm_maps");
            foreach (List<string> entry in results)
            {
                GR5_AMM_Map map = new GR5_AMM_Map
                {
                    uiId = Convert.ToUInt32(entry[1]),
                    uiRootModifierId = Convert.ToUInt32(entry[2]),
                    uiMapKey = Convert.ToUInt32(entry[3]),
                    uiOasisNameId = Convert.ToUInt32(entry[4]),
                    uiOasisDescriptionId = Convert.ToUInt32(entry[5]),
                    uiThumbnailId = Convert.ToUInt32(entry[6]),
                    m_ModifierVector = new List<GR5_AMM_Modifier>()
                };
                List<List<string>> results2 = GetQueryResults("SELECT * FROM amm_modifiers WHERE listType=0 AND listID=" + map.uiId);
                foreach (List<string> entry2 in results2)
                {
                    GR5_AMM_Modifier mm = new GR5_AMM_Modifier
                    {
                        uiId = Convert.ToUInt32(entry2[3]),
                        uiParentId = Convert.ToUInt32(entry2[4]),
                        uiType = Convert.ToUInt32(entry2[5]),
                        uiValue = entry2[6]
                    };
                    map.m_ModifierVector.Add(mm);
                }
                result.Add(map);
            }
            return result;
        }

        public static List<GR5_AMM_GameMode> GetAMMGameModes()
        {
            List<GR5_AMM_GameMode> result = new List<GR5_AMM_GameMode>();
            List<List<string>> results = GetQueryResults("SELECT * FROM amm_gamemodes");
            foreach (List<string> entry in results)
            {
                GR5_AMM_GameMode mode = new GR5_AMM_GameMode
                {
                    uiId = Convert.ToUInt32(entry[1]),
                    uiRootModifierId = Convert.ToUInt32(entry[2]),
                    uiType = Convert.ToUInt32(entry[3]),
                    uiOasisNameId = Convert.ToUInt32(entry[4]),
                    uiOasisDescriptionId = Convert.ToUInt32(entry[5]),
                    uiThumbnailId = Convert.ToUInt32(entry[6]),
                    m_ModifierVector = new List<GR5_AMM_Modifier>()
                };
                List<List<string>> results2 = GetQueryResults("SELECT * FROM amm_modifiers WHERE listType=1 AND listID=" + mode.uiId);
                foreach (List<string> entry2 in results2)
                {
                    GR5_AMM_Modifier mm = new GR5_AMM_Modifier
                    {
                        uiId = Convert.ToUInt32(entry2[3]),
                        uiParentId = Convert.ToUInt32(entry2[4]),
                        uiType = Convert.ToUInt32(entry2[5]),
                        uiValue = entry2[6]
                    };
                    mode.m_ModifierVector.Add(mm);
                }
                result.Add(mode);
            }
            return result;
        }

        public static List<GR5_AMM_GameDetail> GetAMMGameDetails()
        {
            List<GR5_AMM_GameDetail> result = new List<GR5_AMM_GameDetail>();
            List<List<string>> results = GetQueryResults("SELECT * FROM amm_gamedetails");
            foreach (List<string> entry in results)
            {
                GR5_AMM_GameDetail detail = new GR5_AMM_GameDetail
                {
                    uiId = Convert.ToUInt32(entry[1]),
                    uiRootModifierId = Convert.ToUInt32(entry[2]),
                    uiOasisNameId = Convert.ToUInt32(entry[3]),
                    uiOasisDescriptionId = Convert.ToUInt32(entry[4]),
                    m_ModifierVector = new List<GR5_AMM_Modifier>()
                };
                List<List<string>> results2 = GetQueryResults("SELECT * FROM amm_modifiers WHERE listType=2 AND listID=" + detail.uiId);
                foreach (List<string> entry2 in results2)
                {
                    GR5_AMM_Modifier mm = new GR5_AMM_Modifier
                    {
                        uiId = Convert.ToUInt32(entry2[3]),
                        uiParentId = Convert.ToUInt32(entry2[4]),
                        uiType = Convert.ToUInt32(entry2[5]),
                        uiValue = entry2[6]
                    };
                    detail.m_ModifierVector.Add(mm);
                }
                result.Add(detail);
            }
            return result;
        }

        public static List<GR5_SKU> GetSKUs()
        {
            List<GR5_SKU> result = new List<GR5_SKU>();
            List<List<string>> results = GetQueryResults("SELECT * FROM skus");
            foreach(List<string> entry in results)
            {
                GR5_SKU sku = new GR5_SKU
                {
                    m_ID = Convert.ToUInt32(entry[1]),
                    m_Type = Convert.ToUInt32(entry[2]),
                    m_AvailableStock = Convert.ToUInt32(entry[3]),
                    m_TimeStart = Convert.ToUInt32(entry[4]),
                    m_TimeExpired = Convert.ToUInt32(entry[5]),
                    m_BuyIGCCost = Convert.ToUInt32(entry[6]),
                    m_BuyGRCashCost = Convert.ToUInt32(entry[7]),
                    m_AssetKey = Convert.ToUInt32(entry[8]),
                    m_Name = entry[9],
                    m_OasisName = Convert.ToUInt32(entry[10]),
                    m_ItemVector = new List<GR5_SKUItem>()
                };
                List<List<string>> results2 = GetQueryResults("SELECT * FROM skuitems WHERE skuid=" + sku.m_ID);
                foreach (List<string> entry2 in results2)
                {
                    GR5_SKUItem item = new GR5_SKUItem
                    {
                        m_ItemID = Convert.ToUInt32(entry2[1]),
                        m_DurabilityValue = Convert.ToUInt32(entry2[2]),
                        m_DurabilityValue2 = Convert.ToUInt32(entry2[3]),
                        m_OasisName = Convert.ToUInt32(entry2[4]),
                        m_IGCPrice = Convert.ToSingle(entry2[5]),
                        m_GRCashPrice = Convert.ToSingle(entry2[6])
                    };
                    sku.m_ItemVector.Add(item);
                }
                result.Add(sku);
            }
            return result;
        }

        public static List<GR5_Coupon> GetCoupons()
        {
            List<GR5_Coupon> result = new List<GR5_Coupon>();
            List<List<string>> results = GetQueryResults("SELECT * FROM coupons");
            foreach(List<string> entry in results)
            {
                GR5_Coupon coupon = new GR5_Coupon
                {
                    m_ID = Convert.ToUInt32(entry[1]),
                    m_SKUModifierID = Convert.ToUInt32(entry[2]),
                    m_TimeStart = Convert.ToUInt32(entry[3]),
                    m_TimeExpired = Convert.ToUInt32(entry[4])
                };
                result.Add(coupon);
            }
            return result;
        }

        public static List<GR5_SKUModifier> GetSKUModifiers()
        {
            List<GR5_SKUModifier> result = new List<GR5_SKUModifier>();
            List<List<string>> results = GetQueryResults("SELECT * FROM skumodifiers");
            foreach(List<string> entry in results)
            {
                GR5_SKUModifier mod = new GR5_SKUModifier
                {
                    m_ID = Convert.ToUInt32(entry[1]),
                    m_CouponBatchID = Convert.ToUInt32(entry[2]),
                    m_TimeStart = Convert.ToUInt32(entry[3]),
                    m_TimeExpired = Convert.ToUInt32(entry[4]),
                    m_TargetType = Convert.ToUInt32(entry[5]),
                    m_TargetValue = Convert.ToUInt32(entry[6]),
                    m_Tag = entry[7]
                };
                List<List<string>> results2 = GetQueryResults("SELECT * FROM skumodconditions WHERE modid=" + mod.m_ID);    //modid column for reference only
                foreach (List<string> entry2 in results2)
                {
                    GR5_SKUModifierCondition condition = new GR5_SKUModifierCondition();
                    condition.m_Type = Convert.ToUInt32(entry2[2]);
                    condition.m_Target = Convert.ToUInt32(entry2[3]);
                    condition.m_Value = Convert.ToUInt32(entry2[4]);
                    mod.m_ConditionVector.Add(condition);
                }
                results2.Clear();
                results2 = GetQueryResults("SELECT * FROM skumodoutput WHERE modid=" + mod.m_ID);   //same, only for reference
                foreach(List<string> entry2 in results2)
                {
                    GR5_SKUModifierOutput output = new GR5_SKUModifierOutput();
                    output.m_Type = Convert.ToUInt32(entry2[2]);
                    output.m_Target = Convert.ToUInt32(entry2[3]);
                    output.m_Value = Convert.ToUInt32(entry2[4]);
                    mod.m_OutputVector.Add(output);
                }
                result.Add(mod);
            }
            return result;
        }

        public static List<GR5_GameClass> GetGameClasses()
        {
            List<GR5_GameClass> classes = new List<GR5_GameClass>();
            List<List<string>> results = GetQueryResults("SELECT * FROM gameclasses");
            foreach (List<string> entry in results)
            {
                GR5_GameClass gclass = new GR5_GameClass
                {
                    m_ID = Convert.ToUInt32(entry[1]),
                    m_ModifierListID = Convert.ToUInt32(entry[2]),
                    m_OasisID = Convert.ToUInt32(entry[3]),
                    m_Name = entry[4],
                    m_LoadoutID = Convert.ToUInt32(entry[5])
                };
                List<uint> equipweaponids = new List<uint>();
                List<List<string>> results2 = GetQueryResults("SELECT * FROM equipweaponids WHERE classid=" + gclass.m_ID); //classid for reference
                foreach (List<string> entry2 in results2)
                {
                    equipweaponids.Add(Convert.ToUInt32(entry2[2]));
                }
                gclass.m_EquippableWeaponIDVector = equipweaponids;
                List<uint> defskillnodes = new List<uint>();
                results2.Clear();
                results2 = GetQueryResults("SELECT * FROM defskillnodes WHERE classid=" + gclass.m_ID);
                foreach (List<string> entry2 in results2)
                {
                    defskillnodes.Add(Convert.ToUInt32(entry2[2]));
                }
                gclass.m_DefaultSkillNodeIDVector = defskillnodes;
                classes.Add(gclass);
            }
            return classes;
        }

        public static List<GR5_FriendData> GetFriends(ClientInfo client)
        {
            List<GR5_FriendData> friends = new List<GR5_FriendData>();
            List<List<string>> results = GetQueryResults("SELECT * FROM friends WHERE friendofpid=" + client.PID);
            foreach (List<string> entry in results)
            {
                GR5_FriendData fd = new GR5_FriendData();
                fd.m_Person.PersonaID = Convert.ToUInt32(entry[2]);
                fd.m_Person.PersonaName = entry[3];
                fd.m_Person.PersonaStatus = GR5_BasicPersona.STATUS.Online;
                fd.m_Person.AvatarPortraitID = Convert.ToUInt32(entry[5]);
                fd.m_Person.AvatarDecoratorID = Convert.ToUInt32(entry[6]);
                fd.m_Person.AvatarBackgroundColor = Convert.ToUInt32(entry[7]);
                fd.m_Person.CurrentCharacterID = Convert.ToByte(entry[8]);
                fd.m_Person.CurrentCharacterLevel = Convert.ToByte(entry[9]);
                fd.m_Group = Convert.ToByte(entry[10]);
                friends.Add(fd);
            }
            return friends;
        }

        public static bool AddFriend(ClientInfo client, GR5_FriendData friend)
        {
            //TODO: rewrite with an ORM for sql injection and easier mapping
            SQLiteCommand cmd = new SQLiteCommand("INSERT INTO friends (friendofpid, pid, name, status, portraitid, decoratorid, background, classid, level, 'group') VALUES (" +
                client.PID + ", " +
                friend.m_Person.PersonaID + ", '" +
                friend.m_Person.PersonaName + "', " +
                friend.m_Person.PersonaStatus + ", " +
                friend.m_Person.AvatarPortraitID + ", " +
                friend.m_Person.AvatarDecoratorID + ", " +
                friend.m_Person.AvatarBackgroundColor + ", " +
                friend.m_Person.CurrentCharacterID + ", " +
                friend.m_Person.CurrentCharacterLevel + ", " +
                friend.m_Group + ");", connection);

            try {
                lock (dbLock) { return cmd.ExecuteNonQuery() > 0; }
            }
            catch {
                return false;
            }
            
        }

        public static void SetAvatarPortrait(ClientInfo client, uint portraitId, uint backgroundColor)
        {
            SQLiteCommand cmd = new SQLiteCommand($"UPDATE personas SET portraitid = {portraitId}, backcolor = {backgroundColor} WHERE pid = {client.PID};" , connection);
            try { lock (dbLock) { cmd.ExecuteNonQuery(); } }
            catch { return; }
        }

        public static void SetAvatarDecorator(ClientInfo client, uint decoratorId)
        {
            SQLiteCommand cmd = new SQLiteCommand($"UPDATE personas SET decorid = {decoratorId} WHERE pid = {client.PID};", connection);
            try { lock (dbLock) { cmd.ExecuteNonQuery(); } }
            catch { return; }
        }

        public static List<GR5_OperatorVariable> GetOperatorVariables()
        {
            List<GR5_OperatorVariable> opVars = new List<GR5_OperatorVariable>();
            List<List<string>> results = GetQueryResults("SELECT * FROM op_vars");
            GR5_OperatorVariable opVar = new GR5_OperatorVariable();
            foreach (List<string>entry in results)
            {
                opVar.m_Id = Convert.ToUInt32(entry[1]);
                opVar.m_Value = entry[2];
                opVars.Add(opVar);
            }
            return opVars;
        }
    }
}
