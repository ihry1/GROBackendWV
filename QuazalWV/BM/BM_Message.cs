using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuazalWV
{
    public abstract class BM_Message
    {
        public byte msgType = 0xA;
        public ushort msgID;
        public List<BM_Param> paramList = new List<BM_Param>();
        public static byte[] Make(BM_Message msg)
        {
            MemoryStream m = new MemoryStream();
            Helper.WriteU8(m, msg.msgType);
            Helper.WriteU16LE(m, msg.msgID);
            byte[] buff;
            foreach (BM_Param p in msg.paramList)
                switch (p.type)
                {
                    case BM_Param.PARAM_TYPE.Integer:
                        Helper.WriteU8(m, 0);
                        Helper.WriteU32LE(m, (uint)(int)p.data);
                        break;
                    case BM_Param.PARAM_TYPE.Float:
                        Helper.WriteU8(m, 0);
                        Helper.WriteFloatLE(m, (float)p.data);
                        break;
                    case BM_Param.PARAM_TYPE.Buffer:
                        buff = (byte[])p.data;
                        Helper.WriteU8(m, 0x80);
                        Helper.WriteU16LE(m, (ushort)buff.Length);
                        m.Write(buff, 0, buff.Length);
                        break;
                }
            buff = m.ToArray();
            m = new MemoryStream();
            Helper.WriteU16(m, (ushort)(buff.Length + 2));
            Helper.WriteU16(m, (ushort)buff.Length);
            m.Write(buff, 0, buff.Length);
            Helper.WriteU32(m, 0);
            return m.ToArray();
        }

        // M2 diagnostic: file-log to m2diag.txt next to the running server exe (AppDomain.BaseDirectory).
        public static void M2Log(string s)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "m2diag.txt"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + Environment.NewLine); } catch { }
        }

        // M2: replicate every already-spawned PEER's pawn (abstract + concrete creates) into THIS client's
        // view, once per peer. Piggybacks on the client's own in-match messages (esp. the frequent 0x99
        // position update) so a peer appears within a frame -- no separate server->client push needed. Each
        // peer create carries the PEER's owner (0x05C00000|peerStation) + the peer's distinct handles, so
        // this client renders it as a remote slave it doesn't control (the create path the fake-enemy proved).
        public static void AppendPeerPawns(ClientInfo client, List<byte[]> msgs)
        {
            // Only replicate AFTER this client is fully deployed (state 5, ClientReady handled) — replicating
            // mid-deploy disrupts the tactical-map/spawn handshake. And only replicate a peer that is ITSELF
            // fully deployed (its create blob is stable). This keeps peer creates entirely out of the join path.
            if (!client.clientReadyHandled) return;
            foreach (ClientInfo other in Global.clients.ToArray())
            {
                if (other == client) continue;
                if (!other.clientReadyHandled) continue;
                if (other.pawnAbstractEntity == null || other.pawnConcreteEntity == null) continue;
                bool _shown = client.peerHandleSlot.ContainsKey(other.stationID);
                int _seenGen; client.peerLifeGen.TryGetValue(other.stationID, out _seenGen);
                if (_shown && _seenGen == other.lifeGen) continue;   // already shown + same life -> nothing to do
                // Per-viewer SLAVE handles so peers don't collide with each other or this client's own pawn (1/2):
                // slot 0 -> abstract 3/concrete 4, slot 1 -> 5/6, ... A respawn bumps other.lifeGen, so we land here
                // again to DESTROY the dead corpse + recreate a fresh concrete (the retail death->respawn model).
                uint _slot = client.GetOrAllocPeerSlot(other.stationID);
                uint _absH = 3 + _slot * 2, _conH = 4 + _slot * 2;
                // RELATIVE team for the recipient's ABSOLUTE coloring: a peer on the VIEWER's team -> 1 (blue/
                // friendly), opposing -> 2 (red/enemy). Real teams = other.team vs client.team (Global.GetMatchTeam).
                byte _relTeam = (other.team == client.team) ? (byte)1 : (byte)2;
                if (_shown)
                {
                    // RESPAWN: the peer's previous concrete here is a PlayDead'd white corpse. Destroy it (msg 626,
                    // bIsDeadBody=1 = no assert even if already gone) before recreating; the abstract (handle _absH)
                    // PERSISTS across deaths so it is NOT destroyed/recreated (the recreated concrete finds it by station).
                    msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                        0x1006, new DupObj(DupObjClass.Station, 1), new DupObj(DupObjClass.NET_MessageBroker, 5),
                        (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                        Make(new MSG_ID_Net_Obj_Destroy(_conH, true))));
                }
                else
                {
                    // FIRST SHOW: create the abstract player-object once (persists across this peer's deaths).
                    other.pawnAbstractEntity.handle = _absH;
                    other.pawnAbstractEntity.teamID = _relTeam;
                    msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                        0x1006, new DupObj(DupObjClass.Station, 1), new DupObj(DupObjClass.NET_MessageBroker, 5),
                        (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                        Make(new MSG_ID_Net_Obj_Create(0x2C, 0x15, other.pawnAbstractEntity.MakePayload(), other.pawnX, other.pawnY, other.pawnZ, other.pawnOwner))));
                }
                // (Re)create the concrete pawn -- fresh + ALIVE (its create-blob carries ragdollSyncFlags=2 + Health=100);
                // the 0x99 relay re-syncs it to this peer's live position within a frame.
                other.pawnConcreteEntity.handle = _conH;
                other.pawnConcreteEntity.teamID = _relTeam;
                msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                    0x1006, new DupObj(DupObjClass.Station, 1), new DupObj(DupObjClass.NET_MessageBroker, 5),
                    (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                    Make(new MSG_ID_Net_Obj_Create(0x2A, 0x05, other.pawnConcreteEntity.MakePayload(), other.pawnX, other.pawnY, other.pawnZ, other.pawnOwner))));
                client.peerLifeGen[other.stationID] = other.lifeGen;
                M2Log((_shown ? "RESPAWN-RECREATE" : "REPLICATED") + " peer st" + other.stationID + " (handles " + _absH + "/" + _conH + ", gen " + other.lifeGen + ", owner 0x" + other.pawnOwner.ToString("X") + ") -> me st" + client.stationID);
                Log.WriteLine(1, "[M2] " + (_shown ? "recreated(respawn)" : "replicated") + " peer st" + other.stationID + " (handles " + _absH + "/" + _conH + ") -> st" + client.stationID);
            }
        }

        // Destroy the lingering pawn copies of any peer this viewer has shown that has since DISCONNECTED (dropped
        // from Global.clients by QPacketHandler.ProcessDISCONNECT). Driven by the viewer's own bundle; frees the
        // per-viewer handle slot for reuse. Without this a leaver sits as a frozen ghost on everyone else's screen.
        public static void AppendPeerDestroys(ClientInfo client, List<byte[]> msgs)
        {
            if (!client.clientReadyHandled || client.peerHandleSlot.Count == 0) return;
            List<uint> _gone = null;
            foreach (KeyValuePair<uint, uint> kv in client.peerHandleSlot)
            {
                bool _live = false;
                foreach (ClientInfo c in Global.clients.ToArray()) if (c.stationID == kv.Key) { _live = true; break; }
                if (_live) continue;
                if (_gone == null) _gone = new List<uint>();
                _gone.Add(kv.Key);
                uint _absH = 3 + kv.Value * 2, _conH = 4 + kv.Value * 2;
                // concrete (body) then abstract (controller); bIsDeadBody=true suppresses the not-found assert
                msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                    0x1006, new DupObj(DupObjClass.Station, 1), new DupObj(DupObjClass.NET_MessageBroker, 5),
                    (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage, Make(new MSG_ID_Net_Obj_Destroy(_conH, true))));
                msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                    0x1006, new DupObj(DupObjClass.Station, 1), new DupObj(DupObjClass.NET_MessageBroker, 5),
                    (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage, Make(new MSG_ID_Net_Obj_Destroy(_absH, true))));
                M2Log("PEER-DESTROY st" + kv.Key + " (handles " + _absH + "/" + _conH + ") -> me st" + client.stationID + " (disconnected)");
            }
            if (_gone != null)
                foreach (uint _s in _gone) { client.peerHandleSlot.Remove(_s); client.peerLifeGen.Remove(_s); }
        }

        // M3 (HELD): would relay each peer's 0x99 movement replica to THIS client to move the peer pawn. Blind
        // re-targeting is shelved -- byte 7 is a dataset COUNT (changing it crashed cMemBuffer::GetStruct) and
        // byte-6-only (correct handle) was ignored by the slave. Currently CAPTURES only (no send); see below.
        private static int relayDiagCount = 0;
        private static int r99DumpCount = 0;
        public static void AppendPeerReplicas(ClientInfo client, List<byte[]> msgs)
        {
            if (!client.clientReadyHandled) return;
            foreach (ClientInfo other in Global.clients.ToArray())
            {
                if (other == client || !other.clientReadyHandled) continue;
                if (other.lastReplicaPayload == null) continue;
                uint _slot; if (!client.peerHandleSlot.TryGetValue(other.stationID, out _slot)) continue;
                byte[] relay = (byte[])other.lastReplicaPayload.Clone();
                bool ok = relay.Length > 6 && relay[6] == 2;
                if (relayDiagCount < 16)
                {
                    M2Log("relay st" + client.stationID + "<-st" + other.stationID + " len=" + relay.Length
                        + " b6=" + (relay.Length > 6 ? relay[6].ToString() : "?") + " guard=" + ok);
                    relayDiagCount++;
                }
                // ★RE-ENABLED. Wire (RE'd via cEntityManager::ReceiveReplicatedData): [sel:1][size:2][handle BE
                // u32 @bytes 3-6][bitarray][struct]; byte 6 = the handle low byte. Re-aim the peer's own-pawn
                // transform (handle 2) at its slave concrete (handle 4) in THIS client's view. byte-6 (2->4) is the
                // CORRECT crash-free re-target (the earlier crash was byte 7 = the bitarray, not the handle). It was
                // inert before only because the peer abstract was never activated; now ChangeState(3,5)+health make
                // the peer a LIVE deployed player, so ReceiveReplicatedData should apply this transform -> it MOVES.
                if (!ok) continue;
                relay[6] = (byte)(4 + _slot * 2);   // re-aim the peer's own-pawn transform (handle 2) at its per-viewer slave concrete (4/6/8.. by peer slot)
                msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                    0x1006, new DupObj(DupObjClass.Station, 1), new DupObj(DupObjClass.NET_MessageBroker, 5),
                    (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                    Make(new MSG_ID_SendReplicaData(relay))));
            }
        }

        // Fan out each peer's queued discrete action-cmds (ability 0x0E/0x0F, gesture 0x28/0x29) into THIS client's
        // bundle, re-targeted to the viewer's slave handle + master/server bits CLEARED so the slave accepts it
        // unconditionally (AI_Entity::HandleCmdFromNetwork gate v2=0 -- the same bypass the fake enemy used). The
        // entity-cmd raw layout is [u8 unusedBits][u32 handle LSB][u8 cmd+flags]..., so the handle is bytes 1-4 and
        // the isMaster/isServer bits are byte 5 (bits 6-7) -- both byte-aligned, safe to patch in place.
        public static void AppendPeerActionCmds(ClientInfo client, List<byte[]> msgs)
        {
            if (!client.clientReadyHandled) return;
            foreach (ClientInfo other in Global.clients.ToArray())
            {
                if (other == client || other.pendingPeerCmds.Count == 0) continue;
                uint slot;
                if (!client.peerHandleSlot.TryGetValue(other.stationID, out slot)) continue; // peer not shown to this viewer yet
                byte conHandle = (byte)(4 + slot * 2);
                foreach (PendingPeerCmd pc in other.pendingPeerCmds)
                {
                    if (pc.raw.Length <= 5 || !pc.needStations.Contains(client.stationID)) continue;
                    byte[] r = (byte[])pc.raw.Clone();
                    r[1] = conHandle; r[2] = 0; r[3] = 0; r[4] = 0;  // re-target the 32-bit handle to this viewer's slave pawn
                    r[5] = (byte)(r[5] & 0x3F);                       // clear isMaster/isServer bits -> v2=0 (slave accepts unconditionally)
                    msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                        0x1006, new DupObj(DupObjClass.Station, 1), new DupObj(DupObjClass.NET_MessageBroker, 5),
                        (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                        Make(new MSG_ID_Entity_Cmd(r))));
                    M2Log("ACTION relay st" + other.stationID + "->st" + client.stationID + " cmd=0x" + (r[5] & 0x3F).ToString("X2") + " handle=" + conHandle);
                    pc.needStations.Remove(client.stationID);
                }
                other.pendingPeerCmds.RemoveAll(pc => pc.needStations.Count == 0);
            }
        }

        // Flush entity-cmds queued for THIS client's OWN pawn (server-authoritative damage: UpdateHealth/State to
        // the victim, which masters its own pawn so isServer=true is accepted). Handle is as-built, NOT re-targeted.
        public static void AppendSelfCmds(ClientInfo client, List<byte[]> msgs)
        {
            if (client.pendingSelfCmds.Count == 0) return;
            foreach (byte[] raw in client.pendingSelfCmds)
                msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                    0x1006, new DupObj(DupObjClass.Station, 1), new DupObj(DupObjClass.NET_MessageBroker, 5),
                    (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                    Make(new MSG_ID_Entity_Cmd(raw))));
            client.pendingSelfCmds.Clear();
        }

        // Timed auto-respawn for a real player killed by server-authoritative damage. Poor-man's timer, checked on
        // the victim's own BM messages: after the delay, pick a FRESH spawn point and flag a proper respawn -- both
        // the own pawn (AppendSelfRespawn) and the peer copies (AppendPeerPawns) are recreated there, instead of the
        // old revive-in-place at the death spot.
        public static void CheckRespawn(ClientInfo client)
        {
            if (!client.dead || Global.realRespawnDelay <= 0) return;
            if ((DateTime.UtcNow - client.deathTime).TotalSeconds < Global.realRespawnDelay) return;
            client.dead = false; client.hp = Global.realMaxHP;
            // Pick a real spawn point (team-aware random, from Yeti.big) -- the SAME source the initial spawn uses.
            // Update pawnX/Y/Z so the peer recreate lands there; if the map has no spawn data keep the last position.
            float rx, ry, rz;
            byte team = client.pawnConcreteEntity != null ? client.pawnConcreteEntity.teamID : (byte)1;
            if (YetiBigSpawnReader.TryGetSpawn(SessionInfosParameter.defaultMapKey, team, out rx, out ry, out rz))
            { client.pawnX = rx; client.pawnY = ry; client.pawnZ = rz; }
            client.lifeGen++;                  // peers: AppendPeerPawns destroys the corpse + recreates a fresh live pawn at pawnX/Y/Z
            client.ownRespawnPending = true;   // self: AppendSelfRespawn recreates the own pawn (handle 2) at the spawn point
            Log.WriteLine(1, "[DMG] st" + client.stationID + " RESPAWNED (HP " + (int)client.hp + ", lifeGen " + client.lifeGen + ") at (" + client.pawnX + "," + client.pawnY + "," + client.pawnZ + ")");
        }

        // The respawning player's OWN view: destroy the dead concrete (handle 2) then recreate it ALIVE at the fresh
        // spawn point (was: revive-in-place at the death spot). A fresh create restarts the client's MASTER pawn at
        // the spawn point, so its 0x99 position stream now reports the spawn point -> the peer copies (AppendPeerPawns
        // + the 0x99 relay) follow there too (a server teleport would just be overwritten by the client's own next
        // 0x99). Mirrors the proven initial-spawn create, INCLUDING the cObjectHealth commands -- the master's health
        // inits to 0 and the create-blob health never reaches it, so without these the recreated pawn renders dead.
        public static void AppendSelfRespawn(ClientInfo client, List<byte[]> msgs)
        {
            if (!client.ownRespawnPending) return;
            client.ownRespawnPending = false;
            if (client.pawnConcreteEntity == null) return;
            client.pawnConcreteEntity.handle = 2;   // own concrete = proven client-local handle 2
            // ★★ HANDSHAKE RESPAWN (2026-06-17). Server-pushed ChangeState reproduced the abstract STATE but NOT the
            // HUD: 3->5 AND the full 2->3->5 both ARRIVED in-game ([RSPN-CS]) yet the HUD stayed off. PROVEN: the HUD is
            // driven by the CLIENT's real deploy flow (it re-requests spawn -> ProcessSpawningMaster -> ClientReady ->
            // deploy-complete fires the HUD UI event), NOT by server-pushed currentState. So re-run the ACTUAL deploy:
            // reset the one-shot spawn guards + drive the abstract to WaitForSpawn(2). The client's
            // ProcessWaitForSpawnMaster (or the deploy screen) then re-sends SpawnRequest(0x34) -> the EXISTING proven
            // SpawnRequest handler re-runs the FULL deploy (ChangeState 3 + Net_Obj_Create + params, then ClientReady ->
            // ChangeState 5 -> HUD), exactly like the initial spawn. We DROP the bare destroy+recreate -- the handler
            // re-creates the pawn (fresh spawn point via its own TryGetSpawn). The [RESPAWN-0x34] log (Entitiy_CMD)
            // confirms the re-request; if the client never re-requests it sits on the deploy screen (recoverable). See
            // gro-respawn-hud-fix.
            client.playerCreateStuffSent2 = false;   // let the SpawnRequest (0x34) handler run AGAIN
            client.clientReadyHandled = false;       // let the ClientReady (0x35) handler run AGAIN
            client.playerAbstractState = 2;          // WaitForSpawn -> the client re-requests spawn
            msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                0x1006, new DupObj(DupObjClass.Station, 1), new DupObj(DupObjClass.NET_MessageBroker, 5),
                (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                Make(new MSG_ID_Entity_Cmd(client, 0x33))));   // ChangeState(2): case 2 stashes the corpse (AddDeadBodyInClient) while still valid
            msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                0x1006, new DupObj(DupObjClass.Station, 1), new DupObj(DupObjClass.NET_MessageBroker, 5),
                (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                Make(new MSG_ID_Net_Obj_Destroy(2u, true))));   // free handle 2 for the handler's re-create (bIsDeadBody=1 -> safe even if re-handled)
            M2Log("SELF-RESPAWN st" + client.stationID + " -> HANDSHAKE respawn (guards reset + ChangeState 2 + destroy; awaiting client SpawnRequest 0x34)");
            Log.WriteLine(1, "[DMG] st" + client.stationID + " handshake respawn armed (WaitForSpawn; client should re-request spawn)");
        }

        // Queue a raw entity-cmd to fan out to ALL of `src`'s peers (re-targeted to each viewer's slave handle +
        // master/server bits cleared by AppendPeerActionCmds). Used for gestures + death/respawn replication.
        public static void QueuePeerCmd(ClientInfo src, byte[] raw)
        {
            HashSet<uint> need = new HashSet<uint>();
            foreach (ClientInfo o in Global.clients.ToArray())
                if (o != src && o.clientReadyHandled) need.Add(o.stationID);
            if (need.Count == 0) return;
            src.pendingPeerCmds.Add(new PendingPeerCmd { raw = raw, needStations = need });
            if (src.pendingPeerCmds.Count > 32) src.pendingPeerCmds.RemoveAt(0);
        }

        // Kill-feed fan-out: send a Kill cmd (0x39) to EVERY ready client so each renders "<killer> killed <victim>"
        // (AI_EntityHuman::ProcessCmd case 0x39 -> cLogManager::OnKill). The killer/victim NAMES resolve client-side
        // from those entities' abstracts, so the two handle FIELDS must be each recipient's OWN view of those players
        // (handle 2 = own pawn, else 4+peerSlot*2). DISPATCH handle = the KILLER's handle in the recipient's view (kH):
        // the killer just got a kill so its pawn is ALIVE on EVERY recipient, whereas the recipient's OWN pawn (2) is
        // DEAD on the VICTIM at kill time -> dispatching to 2 silently dropped the feed on the victim's client (only the
        // shooter rendered, CONFIRMED in-game). OnKill reads killer/victim from the FIELDS, not the dispatch target, so
        // any live pawn works. Skip a recipient that can't see BOTH players. v2=0 -> no gate on any receiver.
        public static void QueueKillFeed(ClientInfo killer, ClientInfo victim, uint weaponId, bool headshot = false)
        {
            foreach (ClientInfo v in Global.clients.ToArray())
            {
                if (!v.clientReadyHandled || !v.pawnSpawned) continue;
                uint kH = KillFeedHandleInView(v, killer);
                uint vH = KillFeedHandleInView(v, victim);
                if (kH == 0 || vH == 0) continue;   // recipient doesn't see both players -> skip
                v.pendingSelfCmds.Add(new ECMD_Kill(kH, kH, vH, weaponId, 0, headshot).MakePayload());   // dispatch to the killer's LIVE pawn (not the recipient's own, which is dead on the victim)
            }
        }

        // The handle `viewer` uses for `target`: its own pawn = 2, else its allocated peer concrete handle 4+slot*2
        // (0 if `target` hasn't been replicated to `viewer` yet).
        static uint KillFeedHandleInView(ClientInfo viewer, ClientInfo target)
        {
            if (viewer == target) return 2u;
            uint slot;
            return viewer.peerHandleSlot.TryGetValue(target.stationID, out slot) ? (4u + slot * 2u) : 0u;
        }

        public static byte[] HandleMessage(ClientInfo client, Stream s)
        {
            if (Helper.ReadU16(s) < 5 || Helper.ReadU16(s) < 3)
                return null;
            byte type = Helper.ReadU8(s);
            if (type != 0xA)
                return null;
            ushort msgID = (ushort)((Helper.ReadU8(s) << 8) | Helper.ReadU8(s));
            List<byte[]> msgs = new List<byte[]>();
            switch(msgID)
            {
                case 0x96:
                    return Entitiy_CMD.HandleMsg(client, s);
                case 0x99:
                    Helper.ReadU8(s);
                    ushort size = Helper.ReadU16LE(s);
                    byte[] payload = new byte[size];
                    s.Read(payload, 0, size);
                    client.lastReplicaPayload = payload;   // M3: keep latest movement replica to relay to peers
                    if (r99DumpCount < 10) { M2Log("0x99 st" + client.stationID + " size=" + size + " hex=" + BitConverter.ToString(payload, 0, Math.Min(96, payload.Length))); r99DumpCount++; }
                    // Echo the replica payload exactly as the client sent it. Older diagnostics rewrote
                    // bytes 0x11/0x12 to object 0x27 after capture, but the decoded 0x99 stream targets
                    // object 2. Retargeting here disconnects the position/orientation stream the camera
                    // and stance pipeline consume.
                    msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                        0x1006,
                        new DupObj(DupObjClass.Station, 1),
                        new DupObj(DupObjClass.NET_MessageBroker, 5),
                        (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                        Make(new MSG_ID_SendReplicaData(payload))
                        ));
                    break;
                case 0xA3:
                    if (!client.playerCreateStuffSent1)
                    {
                        SpawnLoadoutInfo _loadout = DBHelper.GetSpawnLoadout(client.PID);
                        OCP_AbstractPlayerEntity _abstract = new OCP_AbstractPlayerEntity(1);   // own pawn: proven local handle 1 (peer copy is remapped in AppendPeerPawns)
                        _abstract.teamID = 1;   // own pawn = team 1 (friendly) in this client's OWN blob (relative coloring; matches the concrete). Real team = client.team, server-side only.
                        _abstract.pid = client.PID;
                        _abstract.classID = _loadout.ClassID;
                        _abstract.abilityInventoryId = _loadout.AbilityInventoryID;
                        _abstract.passiveAbilityInventoryId = _loadout.PassiveAbilityInventoryID;
                        _abstract.desiredWeaponMainInventoryId = _loadout.MainWeaponInventoryID;
                        _abstract.desiredWeaponPistolInventoryId = _loadout.PistolWeaponInventoryID;
                        _abstract.desiredWeaponGrenadeInventoryId = _loadout.GrenadeWeaponInventoryID;
                        _abstract.helmetInventoryId = _loadout.HelmetInventoryID;
                        _abstract.armorInventoryId = _loadout.ArmorInventoryID;
                        _abstract.personaName = DBHelper.GetPersonaName(client.PID);   // kill-feed names + name tags (was empty -> blank feed)
                        client.pawnAbstractEntity = _abstract;   // M2: keep the entity to replicate to peers (remapped handle)
                        Log.WriteLine(1, "[DS] abstract spawn loadout: pid=" + client.PID + " class=" + _loadout.ClassID + " bag=" + _loadout.BagType + " source=" + _loadout.Source);
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.NET_MessageBroker, 5),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                            Make(new MSG_ID_Net_Obj_Create(0x2C, 0x15, _abstract.MakePayload(), Global.spawnX, Global.spawnY, Global.spawnZ, client.pawnOwner))
                            ));
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.NET_MessageBroker, 5),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                            Make(new MSG_ID_Entity_Cmd(client, 0x33))
                            )); 
                        client.playerCreateStuffSent1 = true;
                    }
                    msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                        0x1006,
                        new DupObj(DupObjClass.Station, 1),
                        new DupObj(DupObjClass.NET_MessageBroker, 5),
                        (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                        Make(new MSG_ID_NetRule_Synchronize(client.netRulesState))
                        ));
                    // Put the client's match object into WARMUP (BM 901 -> AI_Match.state = 1), NOT Round
                    // (BM 900 -> state 2). In Warmup the spawn-wave id is a client-local time clock
                    // (GetSpawnWaveId = floor(elapsedStateTime / spawnWaveDuration[15s]) + 1), so the player's
                    // wave arrives in ~15s and IsWaitingForWave (AI_MatchServer::bCanPlayerAbstractSpawn) then
                    // stays FALSE -> the deploy/respawn screen (cInGameMenuManager) stops holding bBlockUserInput
                    // (+0x189) -> move/ADS/fire unlock, and cNetRulesManager::bCanSpawn (also needs eStateID==4,
                    // which Synchronize already sends) lets the player legitimately spawn. In Round the wave id
                    // comes from AI_MatchRound, which the emulated DS never ticks -> the wave never arrives ->
                    // input stays blocked and the force-spawned player is reset back to WaitForSpawn ~6s later.
                    // Warmup also leaves state 0 (where GetSpawnWaveDuration hard-returns 100.0 -> "RESPAWN 01:40").
                    msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                        0x1006,
                        new DupObj(DupObjClass.Station, 1),
                        new DupObj(DupObjClass.NET_MessageBroker, 5),
                        (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                        Make(new MSG_ID_BM_Warmup())
                        ));
                    break;
                case 0x266:
                    break;
                case 0x325:
                    // ALWAYS Warmup (state 1). Was: StartRound (ROUND/state 2) once clientReady. ROUND freezes the
                    // spawn-wave clock (server-fed AI_MatchRound::GetTeamSpawnWaveID, never ticked by the emulated DS)
                    // → any re-deploy (incl the AFK/idle camera return) sticks on the respawn screen with combat input
                    // locked forever. WARMUP's client-ticked wave clock always advances, so the screen self-clears.
                    // (Matches the ClientReady handler; see [[gro-afk-return-combat-gate]].)
                    msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                        0x1006,
                        new DupObj(DupObjClass.Station, 1),
                        new DupObj(DupObjClass.NET_MessageBroker, 5),
                        (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                        Make(new MSG_ID_BM_Warmup())
                        ));
                    break;
            }
            // NOTE: the post-spawn Gesture (0x28) send was REMOVED. RE proved cGestureMix::Play never writes
            // the active-anim descriptor dword5CC@+0x5CC (only the mood/rosace path does), so no gesture can
            // fix the A-pose. The fix is to re-fire AI_EntityPlayer::UpdateMood @0x10076e90 via a replicated
            // m_Mood DELTA (Block1 field 30). To nail that wire envelope, cycle 1 CAPTURES the client's own
            // incremental-update format (the 0x99 it streams; logged in the 0x99 case above). postSpawnTicks
            // is retained for the delta's send-timing in cycle 2.
            CheckRespawn(client);
            AppendSelfRespawn(client, msgs);
            Global.SweepIdleMatchClients();          // reap deployed clients gone silent (disconnect/crash); lastSeen is stamped per-packet in QPacketHandler
            AppendPeerDestroys(client, msgs);   // clean up disconnected peers' ghost pawns before (re)replicating live ones
            AppendPeerPawns(client, msgs);
            AppendPeerReplicas(client, msgs);
            AppendPeerActionCmds(client, msgs);
            AppendSelfCmds(client, msgs);
            if (msgs.Count > 0)
                return DO_BundleMessage.Create(client, msgs);
            else
                return null;
        }
    }
}
