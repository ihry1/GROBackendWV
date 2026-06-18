using System;
using System.IO;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QuazalWV
{
    public abstract class Entitiy_CMD
    {
        public enum CMDs
        {
            HasHitATarget = 0x01,   // client->server: a fired round's raycast hit an entity (client dispatcher case 0 + 1). M2 server-authoritative damage.
            DamageTaken = 0x09,             // server->victim: hit reaction
            DamageGivenFeedback = 0x0A,     // server->shooter: hitmarker
            PlayDead = 0x0B,                // server->all: ragdoll death
            PlayerRefillAmmo = 0x0C,        // RELOAD-inference candidate
            PlayerRepAmmoInfo = 0x0D,       // master->server when clip/ammo changes — RELOAD-inference candidate
            PowerButtonStateChangePC = 0x0E,// ability key press/release — ABILITY candidate
            ForcePowerStatePC = 0x0F,       // force m_ReplicatedPowerPC.value1 — ABILITY candidate (the cleaner carrier)
            FallingDamage = 0x1C,
            UpdateHealth = 0x1E,
            UpdateDefaultHealth = 0x1F,
            UpdateHealthState = 0x20,
            Gesture = 0x28,
            GestureAnimIdx = 0x29,
            SpawnRequest = 0x34,    // abstract cmd '4' (0x34) PlayerRequestSpawn  (AI_EntityPlayerAbstract::ProcessCmd case '4')
            ClientReady = 0x35,     // abstract cmd '5' (0x35) PlayerClientReady (client->server, sent after weapons async-load)
            FireAction = 0x36,
        }
        public uint handle;
        public byte cmd;
        public bool isMaster;
        public bool isServer;

        public void AppendHeader(BitBuffer buf)
        {
            buf.WriteBits(handle, 32);
            buf.WriteBits(cmd, 6);
            buf.WriteBits((uint)(isMaster ? 1 : 0), 1);
            buf.WriteBits((uint)(isServer ? 1 : 0), 1);
        }

        public abstract byte[] MakePayload();

        public static byte[] GetRawPayload(Stream s)
        {
            s.Seek(-8, SeekOrigin.Current);
            ushort size = Helper.ReadU16LE(s);
            byte[] buff = new byte[size];
            s.Read(buff, 0, size);
            return buff;
        }

        // M2 helper: wrap a server->client entity command (Entity_Cmd 0x96) in the DO RMC ProcessMessage
        // envelope (the same path the spawn block uses) so it can be pushed into the HandleMsg response bundle.
        static byte[] WrapEntityCmd(ClientInfo client, Entitiy_CMD ec)
        {
            return DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                0x1006,
                new DupObj(DupObjClass.Station, 1),
                new DupObj(DupObjClass.NET_MessageBroker, 5),
                (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                BM_Message.Make(new MSG_ID_Entity_Cmd(ec.MakePayload())));
        }

        // M2 respawn: re-create the concrete fake-enemy pawn (handle 3) at the enemy spawn point. The death
        // (HP=0 + dead state) makes the client DESTROY the slave pawn, so health cmds can't revive it; a fresh
        // Net_Obj_Create does. The ABSTRACT (handle 4, station 0x5c00003) is NOT re-sent: it persists across the
        // pawn's death, and the re-created pawn finds its abstract by that station (AI_EntityPlayer::LoadFrom).
        static void AppendFakeEnemyPawnCreate(ClientInfo client, List<byte[]> msgs)
        {
            float dx = Global.spawnX + 2f, dy = Global.spawnY, dz = Global.spawnZ;
            OCP_PlayerEntity enemy = new OCP_PlayerEntity(Global.fakeEnemyHandle) { teamID = 2, classID = 0, pid = 0 };
            msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                0x1006,
                new DupObj(DupObjClass.Station, 1),
                new DupObj(DupObjClass.NET_MessageBroker, 5),
                (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                BM_Message.Make(new MSG_ID_Net_Obj_Create(0x2A, 0x05, enemy.MakePayload(), dx, dy, dz, Global.fakeEnemyOwner))));
        }

        // Resolve the victim of a HasHitATarget from `shooter`. The hit cmd carries the TARGET ENTITY HANDLE as a
        // u32 at raw bit 178 -- in the SHOOTER's view, so a peer pawn = concrete handle 4+slot*2. (The 4-bit field at
        // bit 174, which an earlier decode mislabeled "target slot", is actually the BODYPART [0=head]; the u32 right
        // after it is the real target.) Resolving by handle scales to 3+ players. Falls back to the only-other-client
        // in a 2-player match if the handle doesn't resolve; returns null otherwise (never apply damage to a guess).
        static ClientInfo ResolveHitVictim(ClientInfo shooter, byte[] raw)
        {
            List<ClientInfo> others = new List<ClientInfo>();
            foreach (ClientInfo c in Global.clients.ToArray())
                if (c != shooter && c.clientReadyHandled && c.pawnSpawned && !c.dead) others.Add(c);
            if (others.Count == 0) return null;
            // The hit cmd carries the TARGET concrete handle as a u32 at raw bit 210 (shooter's view: a peer's
            // concrete pawn = 4+slot*2). The bit-178 u32 is a separate field that reads 0; bit 210 carries the real
            // handle (verified: it varies 4/6 with the target, both valid peer handles). This is the PRECISE target,
            // so a shot at a TEAMMATE resolves to the teammate (friendly-fire then drops it) instead of the nearest
            // enemy -- and it works regardless of how clustered the players are.
            uint hitHandle = (uint)ReadBitsLE(raw, 210, 32);
            foreach (ClientInfo o in others)
            {
                uint slot;
                if (shooter.peerHandleSlot.TryGetValue(o.stationID, out slot) && (hitHandle == 4u + slot * 2u || hitHandle == 3u + slot * 2u)) return o;
            }
            if (others.Count == 1) return others[0];   // 2-player fallback: unambiguous if the handle didn't resolve
            BM_Message.M2Log("[DMG] UNRESOLVED hit from st" + shooter.stationID + " hitHandle=0x" + hitHandle.ToString("X") + " (others=" + others.Count + ") -- matched no peer (4+slot*2 / 3+slot*2)");
            return null;
        }

        // Read n bits LSB-first from a raw entity-cmd payload (same bit-indexing as DecodeHitTargetSlot).
        static long ReadBitsLE(byte[] raw, int startBit, int n)
        {
            long v = 0;
            for (int k = 0; k < n; k++)
            {
                int bit = startBit + k, by = bit >> 3, off = bit & 7;
                if (by < raw.Length) v |= (long)((raw[by] >> off) & 1) << k;
            }
            return v;
        }
        // A 21-bit quantized world coord from a HasHitATarget payload: client value = V*0.001 - 1000.
        // HasHitATarget impact Vec3 = raw bits 48/69/90; sourceVec 111/132/153; bodypart 4b @174; target handle u32 @178; surface u32 @210.
        static float DequantPos(byte[] raw, int startBit) { return (float)(ReadBitsLE(raw, startBit, 21) * 0.001 - 1000.0); }

        public static byte[] HandleMsg(ClientInfo client, Stream s)
        {
            List<byte[]> msgs = new List<byte[]>();
            Helper.ReadU32(s);
            uint handle = Helper.ReadU32(s);
            byte cmd = (byte)(Helper.ReadU8(s) & 0x3F);
            long pos = s.Position;
            byte[] raw = GetRawPayload(s);
            s.Seek(pos, 0);
            Log.WriteLine(2, "Received CMD 0x" + cmd.ToString("X8") + " (" + (CMDs)cmd + ") for Handle 0x" + handle.ToString("X8"), Color.Red);
            // [CMDIN] diag: dump every incoming entity command (sender st/pid + cmd name + raw payload) so we can
            // decode the 3-client capture — RELOAD (which 0x96 cmd, if any, e.g. 0x0D PlayerRepAmmoInfo), ABILITY
            // (0x0E PowerButtonStateChangePC vs 0x0F ForcePowerStatePC), and HasHitATarget (0x01) target-slot when a
            // REAL player shoots another. Sender attribution is essential: every client uses handle 2 for its OWN
            // pawn, so cmd name + sender pid (4660=wv, 4661=wv2, 4662=wv3) is what tells the players apart.
            Log.WriteLine(1, "[CMDIN] from=st" + client.stationID + " pid" + client.PID + " cmd=0x" + cmd.ToString("X2") + " (" + (CMDs)cmd + ")"
                + " handle=0x" + handle.ToString("X8") + " len=" + raw.Length
                + " raw=" + BitConverter.ToString(raw).Replace("-", ""), Color.Cyan);
            // M2 respawn — revive the fake enemy a few seconds after it dies. Poor-man's timer: checked on each
            // incoming entity-cmd (the player keeps shooting/acting). Revive = full HP + alive, again sent
            // isMaster=false,isServer=false so the slave accepts it (gate v2=0).
            // RESPAWN TEMPORARILY DISABLED: re-creating handle 3 trips "duplicate entity" (AI_Entity.cpp:111)
            // because our fake death HIDES the pawn but does NOT destroy it (handle 3 still lives in the client's
            // cObjectManager). Proper respawn must first send a Net_Obj DESTROY of the old pawn, then re-create.
            // That destroy message is the next RE target. Until then the enemy dies (vanishes) and stays down.
            // (AppendFakeEnemyPawnCreate is kept for when the destroy lands.)
            switch ((CMDs)cmd)
            {
                case CMDs.HasHitATarget:
                    // M2 — server-authoritative damage. The client raycast-confirmed a hit on an entity and
                    // reported it (this IS the cmd 0x01 the [CMDIN] diag saw reaching the DS 22x when the player
                    // shot the dummy). There is exactly one target (the fake enemy), so we don't need to decode
                    // the bit-packed target slot yet — a hit report from the player IS a hit on the enemy. Apply
                    // flat placeholder damage; when HP reaches 0 the SERVER declares the kill (authoritative) and
                    // resets HP for the next life.
                    // SCOPE: this TRACKS damage only. It does NOT yet make the enemy visibly die/respawn on the
                    // client — reflecting a SLAVE's death needs a replica health UPDATE (DO Update) or a Net_Obj
                    // destroy+respawn, NOT a master-only health entity-cmd (those trip AI_Entity.cpp:350). That
                    // reflection is the next step; this proves the authoritative receive->damage->kill loop.
                    if (Global.enableFakeEnemy && !Global.fakeEnemyDead)
                    {
                        Global.fakeEnemyHP -= Global.fakeEnemyHitDamage;
                        if (Global.fakeEnemyHP > 0f)
                            Log.WriteLine(1, "[M2] hit -> fake enemy HP = " + (int)Global.fakeEnemyHP + "/" + (int)Global.fakeEnemyMaxHP, Color.Orange);
                        else
                        {
                            // SERVER declares the kill AND pushes the death to the client. SLAVE-safe: the health/
                            // state cmds go out isMaster=false,isServer=false -> the 2 header bits read into Command
                            // +0xC == 0 -> AI_Entity::HandleCmdFromNetwork runs ProcessCmd WITHOUT the master/server
                            // gate (no AI_Entity.cpp:350 assert). Revive is handled at the top of HandleMsg.
                            Global.fakeEnemyKills++;
                            Global.fakeEnemyDead = true;
                            Global.fakeEnemyDeathTime = DateTime.UtcNow;
                            // RAGDOLL death: PlayDead (cmd 0x0B) plays the death animation in place, instead of the
                            // abrupt vanish from UpdateHealthState(dead). Sent master=0/server=0 (gate v2=0) so the
                            // slave plays it. Vec3#1 = the enemy's position; impulse + params = 0 (ragdoll, no force).
                            msgs.Add(WrapEntityCmd(client, new ECMD_PlayDead(Global.fakeEnemyHandle, Global.spawnX + 2f, Global.spawnY, Global.spawnZ)));
                            Log.WriteLine(1, "[M2] *** FAKE ENEMY DOWN (kill #" + Global.fakeEnemyKills + ") *** death pushed to client (slave gate v2=0)", Color.Lime);
                        }
                    }
                    else if (!Global.enableFakeEnemy)
                    {
                        // ★ REAL peer-vs-peer server-authoritative damage. The shooter raycast-confirmed a hit +
                        // reported it; the DS adjudicates: drop the victim's HP and push UpdateHealth to the VICTIM's
                        // OWN pawn (handle 2). The victim MASTERS its own pawn (&1 set), so the cmd sent isServer=true
                        // (v2=2) PASSES the gate -- the inverse of the slave assert that blocked the fake enemy +
                        // abilities. No out-of-band push: queue on the victim's ClientInfo, flushed on its next bundle
                        // (BM_Message.AppendSelfCmds).
                        ClientInfo victim = ResolveHitVictim(client, raw);
                        // FRIENDLY FIRE OFF: a hit on a SAME-(real-)team player does nothing -- no damage, hitmarker,
                        // or kill feed (GR:O PvP convention). No-op for 1v1 (always opposing); gates 2v2 teammates.
                        if (victim != null && victim.team == client.team)
                        {
                            BM_Message.M2Log("[DMG] friendly-fire blocked: st" + client.stationID + " -> teammate st" + victim.stationID + " (team " + client.team + ")");
                            victim = null;
                        }
                        if (victim != null && !victim.dead)
                        {
                            // ★ REAL PER-WEAPON DAMAGE: look up the SHOOTER's equipped primary weapon base damage
                            // (mainWeaponID == weapon mapKey) from the DB. The hit cmd carries NO damage -- retail's
                            // DS computed it from the weapon (AI_EntityPlayer::GetDamageData), which is exactly this
                            // role. Falls back to the flat tunable on a DB miss (grenade / unknown weapon). MVP = primary
                            // base damage; headshot/falloff/armor-mitigation are refinements (see gro-combat-wire-protocol).
                            uint _wpnKey = client.pawnConcreteEntity != null ? client.pawnConcreteEntity.mainWeaponID : 0;
                            float _dmg = DBHelper.GetWeaponDamage(_wpnKey);
                            if (_dmg <= 0f) _dmg = Global.realHitDamage;   // fallback: unknown weapon / grenade / DB miss
                            // Parse the real IMPACT point + the BODYPART from the incoming hit cmd. ★ BODYPART = the 4-bit
                            // field at raw bit 174 (RE'd: byte40/this[10]; the field earlier MISLABELED "target slot"):
                            // 0=HEAD, {1,2,3,4,7,8,13}=torso, {5,6}=arms, {9,10}=legs. (The 8b @254 was a material byte ~0,
                            // which is why the earlier bp log was always 0.) Impact Vec3 = raw bits 48/69/90 (real hit point).
                            float _ix = DequantPos(raw, 48), _iy = DequantPos(raw, 69), _iz = DequantPos(raw, 90);
                            int _bodypart = (int)ReadBitsLE(raw, 174, 4);
                            bool _headshot = (_bodypart == 0);
                            if (_headshot) _dmg *= Global.headshotMultiplier;   // ★ HEADSHOT damage bonus (retail scales via GetBodyPartMultiplier @vtable+0x2A0)
                            victim.hp -= _dmg;
                            // ★ HITMARKER: tell the SHOOTER its shot landed (DamageGivenFeedback -> floating dmg number +
                            // the HUD hit marker). To the shooter's OWN pawn (handle 2; isServer=true -> v2=2 master gate).
                            // victimHandle = the shooter's view of the victim (4+slot*2). bodypart -> slotA(+28): the client
                            // sets byte540 from it and byte540==0 (head) -> HUD_HIT_Headshot. Fires on EVERY confirmed hit.
                            {
                                uint _vslot; uint _vH = client.peerHandleSlot.TryGetValue(victim.stationID, out _vslot) ? (4u + _vslot * 2u) : 0u;
                                client.pendingSelfCmds.Add(new ECMD_DamageGivenFeedback(2, _ix, _iy, _iz, _dmg, _vH, (byte)_bodypart, _headshot).MakePayload());
                            }
                            if (victim.hp > 0f)
                            {
                                victim.pendingSelfCmds.Add(new ECMD_UpdateHealth(2, victim.hp).MakePayload());   // handle 2 = victim's OWN pawn; isServer=true (default)
                                Log.WriteLine(1, "[DMG] st" + client.stationID + " (wpn " + _wpnKey + " dmg " + _dmg.ToString("0.0") + " bp=" + _bodypart + (_headshot ? " HEADSHOT" : "") + ") hit st" + victim.stationID + " -> HP " + (int)victim.hp + "/" + (int)Global.realMaxHP, Color.Orange);
                            }
                            else
                            {
                                victim.hp = 0f; victim.dead = true; victim.deathTime = DateTime.UtcNow;
                                victim.pendingSelfCmds.Add(new ECMD_UpdateHealth(2, 0f).MakePayload());
                                victim.pendingSelfCmds.Add(new ECMD_UpdateHealthState(2, false, true).MakePayload());   // self: dead/KO (the camera-drop death the victim already saw)
                                // SELF ragdoll RE-ENABLED 2026-06-17: ragdoll the victim's OWN pawn on death (limp on the
                                // victim's own screen). Deferred earlier because the OLD bare destroy+recreate respawn
                                // couldn't recover a ragdolled master (came back no-HUD/half-dead). NOW AppendSelfRespawn
                                // re-runs the FULL client deploy handshake (reset guards + ChangeState(2) -> client re-requests
                                // SpawnRequest 0x34 -> the proven deploy -> loadout/deploy screen + HUD, CONFIRMED in-game),
                                // which fully re-deploys a fresh pawn and stashes the ragdolled corpse via case-2
                                // AddDeadBodyInClient -- so a ragdolled master is recovered cleanly. field52=1 (LARGE branch ->
                                // InitRagdoll -> limp); isServer=true (v2=2) accepted via the &1 master bit. See gro-peer-ragdoll-re.
                                victim.pendingSelfCmds.Add(new ECMD_PlayDead(2, victim.pawnX, victim.pawnY, victim.pawnZ, false, true).MakePayload());
                                // PEERS see the victim die: UpdateHealthState dead -> ragdollSyncFlags bit1 clear ->
                                // AI_EntityHuman::Replica early-returns -> the body stops following movement; PlayDead =
                                // the death indicator. Fanned out + re-targeted to each viewer's slave handle + v2=0.
                                BM_Message.QueuePeerCmd(victim, new ECMD_UpdateHealthState(2, false, true).MakePayload());
                                BM_Message.QueuePeerCmd(victim, new ECMD_PlayDead(2, victim.pawnX, victim.pawnY, victim.pawnZ).MakePayload());
                                // ★ KILL FEED: "<killer> killed <victim>" on every client's feed (cmd 0x39 -> cLogManager::OnKill).
                                // Per-viewer handle remap (names resolve client-side); weaponID = weapons.weaponID (real icon);
                                // headshot (the fatal hit's bodypart==0) -> ECMD_Kill field16=0 -> the headshot kill-feed icon.
                                BM_Message.QueueKillFeed(client, victim, DBHelper.GetWeaponWeaponID(_wpnKey), _headshot);
                                Log.WriteLine(1, "[DMG] *** st" + victim.stationID + " KILLED by st" + client.stationID + " (bp=" + _bodypart + (_headshot ? " HEADSHOT" : "") + ") ***", Color.Lime);
                            }
                        }
                    }
                    break;
                case CMDs.FallingDamage:
                    raw[5] = 0x1C;
                    msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                        0x1006,
                        new DupObj(DupObjClass.Station, 1),
                        new DupObj(DupObjClass.NET_MessageBroker, 5),
                        (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                        //BM_Message.Make(new MSG_ID_Entity_Cmd(client, cmd))
                        BM_Message.Make(new MSG_ID_Entity_Cmd(raw))
                        ));
                    break;
                case CMDs.FireAction:
                    // Firing already replicates to peers via the 0x99 m_PlayerFire stream; this echo back to the
                    // sender is inert. Left as-is so the proven firing path stays untouched.
                    msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                        0x1006,
                        new DupObj(DupObjClass.Station, 1),
                        new DupObj(DupObjClass.NET_MessageBroker, 5),
                        (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                        BM_Message.Make(new MSG_ID_Entity_Cmd(raw))
                        ));
                    break;
                // ABILITY cmds 0x0E PowerButtonStateChangePC / 0x0F ForcePowerStatePC are NOT relayed: both ASSERT
                // on a slave (AI_EntityHuman::ProcessCmd @0x10088820 case 0x0E -> serializationFlags&2 @cpp:503; case
                // 0x0F -> &1 @cpp:516) -> a blocking "AI ASSERTION FAILED" dialog on the peer (CONFIRMED in-game:
                // wv2 crashed when wv popped Blitz). The power cmds are SERVER-ONLY receive handlers; a peer can't
                // process them. Abilities must be replicated as the m_ReplicatedPowerPC FIELD (Block1) via a
                // selector-0 SendReplicaData, not an entity-cmd. TODO -- see [[gro-action-replication]].
                case CMDs.Gesture:                   // one-shot animations (jump/grenade/melee) — case 0x28 has NO slave gate (IDA-confirmed), relay-safe
                case CMDs.GestureAnimIdx:            // case 0x29 same (no gate)
                    // FAN OUT to PEERS. The old code relayed these back to the SENDER with the original handle 2, so
                    // they reached no one (the relay-to-self bug). Queue the raw cmd; each peer pulls + re-targets it
                    // on its next bundle (BM_Message.AppendPeerActionCmds) -> the peer's slave pawn plays the FX.
                    BM_Message.QueuePeerCmd(client, (byte[])raw.Clone());
                    break;
                case CMDs.SpawnRequest:
                    // [RESPAWN-0x34] confirm whether the client RE-SENDS SpawnRequest on respawn (the handshake-respawn
                    // in BM_Message.AppendSelfRespawn resets playerCreateStuffSent2 + drives ChangeState(2) to provoke it).
                    // sent2=False here on a respawn = the guard was reset + the handler is about to re-run the full deploy.
                    Log.WriteLine(1, "[RESPAWN-0x34] st" + client.stationID + " SpawnRequest(0x34) received (sent2=" + client.playerCreateStuffSent2 + ")", System.Drawing.Color.Cyan);
                    if (!client.playerCreateStuffSent2)
                    {
                        client.playerAbstractState = 3;
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.NET_MessageBroker, 5),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                            BM_Message.Make(new MSG_ID_Entity_Cmd(client, 0x33))
                            ));
                        // Set ONLY ServerReady (bit 0x1000) so the client's ProcessSpawningMaster Step 4
                        // (bIsServerReady) passes. Do NOT pre-set ClientReady (0x2000) here — the real DS
                        // sets it only when the client sends ClientReady (0x35), AFTER its weapons finish
                        // async-loading (verified: AI_EntityPlayerAbstract::ProcessCmd case '5' @0x100d7120).
                        client.settings.bitField14.entries[3].word = 1;//server ready (bit 0x1000)
                        client.settings.bitField14.entries[0].word = 1;//spawnCount
                        client.settings.bitField14.entries[1].word = 1;//requestSpawn
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.SES_cl_Player_NetZ, 257),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.SetPlayerParameters,
                            client.settings.toBuffer()
                            ));
                        // Spawn the player with the same class/loadout chosen in the lobby. The DS reads the
                        // backend DB read-only, resolving the selected class's bag (4/5/6) plus kit defaults.
                        OCP_PlayerEntity _pe = new OCP_PlayerEntity(2);   // own pawn: proven local handle 2 (peer copy is remapped in AppendPeerPawns)
                        // ★ RELATIVE team for the client's ABSOLUTE coloring (team 1 = own/blue, team 2 = enemy/red,
                        // per-viewer). Each client's OWN pawn must be team 1 in ITS OWN blob, else the real-team-2
                        // player (wv) saw its own pawn red. The REAL team (client.team) drives only the server-side
                        // spawn zone (below) + friendly-fire; PEER copies get the per-viewer relative team in
                        // AppendPeerPawns (same team -> 1, opposing -> 2).
                        _pe.teamID = 1;
                        SpawnLoadoutInfo _loadout = DBHelper.GetSpawnLoadout(client.PID);
                        _pe.classID = _loadout.ClassID;
                        _pe.mainWeaponID = _loadout.MainWeaponID;
                        _pe.pistolWeaponID = _loadout.PistolWeaponID;
                        _pe.armorInventoryID = _loadout.ArmorInventoryID;
                        _pe.helmetKey = _loadout.HelmetKey;
                        _pe.abilityType = _loadout.AbilityType;
                        _pe.passiveAbilityType = _loadout.PassiveAbilityType;
                        _pe.faceID = _loadout.FaceID;
                        _pe.skinID = _loadout.SkinID;
                        _pe.pid = client.PID;   // build the player's PERSISTED custom weapon parts (StoreService 22/23) in-match; 0-safe -> defaults
                        Log.WriteLine(1, "[DS] concrete spawn loadout: pid=" + client.PID + " class=" + _loadout.ClassID + " bag=" + _loadout.BagType +
                            " primary=" + _loadout.MainWeaponID + " pistol=" + _loadout.PistolWeaponID +
                            " armorInv=" + _loadout.ArmorInventoryID + " helmetKey=0x" + _loadout.HelmetKey.ToString("X8") +
                            " abilityType=" + _loadout.AbilityType + " passiveType=" + _loadout.PassiveAbilityType +
                            " body=" + _loadout.FaceID + "/" + _loadout.SkinID + " source=" + _loadout.Source);
                        // Resolve a real spawn point for the session's map from Yeti.big at runtime, picking a
                        // RANDOM zone for the player's team (was: always team-1's first point). Uses the entity's
                        // teamID so it follows team assignment; MSG_ID_Net_Obj_Create reads Global.spawn* below.
                        // Falls back to the current Global.spawn* if the map has no spawn zones (the 8 non-PvP maps).
                        float _sx, _sy, _sz;
                        if (Global.forceSharedSpawn && Global.sharedSpawnSet)
                        {
                            // TEST AID: reuse the first spawner's captured point so ALL players land together.
                            Global.spawnX = Global.sharedSpawnX; Global.spawnY = Global.sharedSpawnY; Global.spawnZ = Global.sharedSpawnZ;
                            Log.WriteLine(1, "[DS] TEST shared spawn: pid=" + client.PID + " at (" + Global.spawnX + ", " + Global.spawnY + ", " + Global.spawnZ + ")");
                        }
                        else if (YetiBigSpawnReader.TryGetSpawn(SessionInfosParameter.defaultMapKey, client.team, out _sx, out _sy, out _sz))
                        {
                            Global.spawnX = _sx; Global.spawnY = _sy; Global.spawnZ = _sz;
                            if (Global.forceSharedSpawn) { Global.sharedSpawnX = _sx; Global.sharedSpawnY = _sy; Global.sharedSpawnZ = _sz; Global.sharedSpawnSet = true; }
                            Log.WriteLine(1, "[DS] Spawning team " + client.team + " player at (" + _sx + ", " + _sy + ", " + _sz + ") from Yeti.big" + (Global.forceSharedSpawn ? " [captured as shared]" : ""));
                        }
                        else
                            Log.WriteLine(1, "[DS] no Yeti.big spawn for map 0x" + SessionInfosParameter.defaultMapKey.ToString("X8")
                                + " team " + client.team + " - using fallback (" + Global.spawnX + ", " + Global.spawnY + ", " + Global.spawnZ + ")");
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.NET_MessageBroker, 5),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                            BM_Message.Make(new MSG_ID_Net_Obj_Create(0x2A, 0x05, _pe.MakePayload(), Global.spawnX, Global.spawnY, Global.spawnZ, client.pawnOwner))
                            ));
                        // HOLD at state 3 (Spawning) — do NOT walk to state 5 here. The client must run its
                        // ProcessSpawningMaster handshake (concrete pawn exists + weapons async-load ~2.4s +
                        // ServerReady) and then send ClientReady (0x35); we advance to state 5 in THAT handler.
                        // Jumping to 5 here (the old bug) skipped the handshake in ~84ms while weapons took
                        // ~2.4s -> client-ready never set -> combat-input gate (bIsClientReady) never opened
                        // -> no move / no ADS scope / no fire.
                        // Feed the pawn's cObjectHealth (handle 2): it inits to 0 HP and the create-blob
                        // health never reaches it, so the pawn renders dead/0HP. Set max+current = 100 and
                        // mark alive via the DS->client health commands the emulated DS otherwise never sends.
                        foreach (Entitiy_CMD hc in new Entitiy_CMD[] {
                            new ECMD_UpdateDefaultHealth(2, 100f),
                            new ECMD_UpdateHealth(2, 100f),
                            new ECMD_UpdateHealthState(2, true, false) })
                            msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                                0x1006,
                                new DupObj(DupObjClass.Station, 1),
                                new DupObj(DupObjClass.NET_MessageBroker, 5),
                                (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                                BM_Message.Make(new MSG_ID_Entity_Cmd(hc.MakePayload()))
                                ));
                        // ---- FAKE ENEMY (server-spawned dummy player) — MVP opponent for server-authoritative
                        // combat. A second player entity the DS owns (owner 0x5c00003 = remote station, so the
                        // client renders it as a SLAVE not itself), team 2 (enemy) at handle 3, ~2m from the
                        // player so it's immediately visible. M1 goal: does the retail client render it?
                        // Toggle Global.enableFakeEnemy=false to revert to clean single-player.
                        if (Global.enableFakeEnemy)
                        {
                            float _dx = Global.spawnX + 2f, _dy = Global.spawnY, _dz = Global.spawnZ;
                            // (1) ABSTRACT enemy FIRST. AI_EntityPlayer::LoadFrom asserts "Must have player abstract
                            // by this point" — it does GetPlayerAbstractByID(GetPlayerStationID(pawn)) and bails if
                            // absent. So register the abstract under the SAME station (owner 0x5c00003) before the
                            // concrete pawn (mirrors the local player's abstract=1 / concrete=2 pairing). The pawn's
                            // inline ClassInfo is keyed on abstract->pid, so give the abstract a distinct fake pid.
                            OCP_AbstractPlayerEntity _enemyAbs = new OCP_AbstractPlayerEntity(Global.fakeEnemyAbstractHandle) { teamID = 2, classID = 0, pid = Global.dummyFriendPidCounter };
                            msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                                0x1006,
                                new DupObj(DupObjClass.Station, 1),
                                new DupObj(DupObjClass.NET_MessageBroker, 5),
                                (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                                BM_Message.Make(new MSG_ID_Net_Obj_Create(0x2C, 0x15, _enemyAbs.MakePayload(), _dx, _dy, _dz, Global.fakeEnemyOwner))
                                ));
                            // (2) CONCRETE enemy pawn.
                            OCP_PlayerEntity _enemy = new OCP_PlayerEntity(Global.fakeEnemyHandle) { teamID = 2, classID = 0, pid = 0 };
                            Log.WriteLine(1, "[DS] FAKE ENEMY spawn: handle=" + Global.fakeEnemyHandle + " team=2 owner=0x"
                                + Global.fakeEnemyOwner.ToString("X8") + " at (" + _dx + ", " + _dy + ", " + _dz + ")");
                            msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                                0x1006,
                                new DupObj(DupObjClass.Station, 1),
                                new DupObj(DupObjClass.NET_MessageBroker, 5),
                                (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                                BM_Message.Make(new MSG_ID_Net_Obj_Create(0x2A, 0x05, _enemy.MakePayload(), _dx, _dy, _dz, Global.fakeEnemyOwner))
                                ));
                            Global.fakeEnemyHP = 100f;
                            // NO health entity-commands for the enemy. It's a SLAVE on the client (server-owned),
                            // and UpdateHealth/UpdateHealthState are MASTER-ONLY -> "Shouldn't receive this command
                            // if I'm not the master" assert (AI_Entity::ProcessCmdFromNetwork, AI_Entity.cpp:350).
                            // A slave's health comes from its create-replica (OCP_PlayerEntity.Health=100, already
                            // in the blob). If the enemy renders dead/0HP, its slave health needs a replica UPDATE
                            // (the same path M2 damage will use), not an entity command.
                        }
                        // M2: record this client's spawned-pawn (object + position) + mark it replicable to peers.
                        client.pawnConcreteEntity = _pe;
                        client.pawnX = Global.spawnX; client.pawnY = Global.spawnY; client.pawnZ = Global.spawnZ;
                        client.pawnSpawned = true;
                        client.playerCreateStuffSent2 = true;
                    }
                    break;
                case CMDs.ClientReady:
                    // The client finished its state-3 Spawning handshake (concrete pawn + async-loaded
                    // weapons + ServerReady) and sent ClientReady (cmd 0x35). The real DS responds by setting
                    // the ClientReady bit 0x2000 in the player params (AI_EntityPlayerAbstract::ProcessCmd
                    // case '5' @0x100d7120). Mirror that, then ChangeState(5) Loop so the combat-input gate
                    // (AI_EntityPlayerAbstract::Spawn bIsClientReady) finally opens -> move/ADS/fire unlock.
                    if (client.playerCreateStuffSent2 && !client.clientReadyHandled)
                    {
                        client.settings.bitField14.entries[4].word = 1;//client ready (bit 0x2000)
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.SES_cl_Player_NetZ, 257),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.SetPlayerParameters,
                            client.settings.toBuffer()
                            ));
                        client.playerAbstractState = 5;
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.NET_MessageBroker, 5),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                            BM_Message.Make(new MSG_ID_Entity_Cmd(client, 0x33))
                            ));
                        client.clientReadyHandled = true;
                        BM_Message.M2Log("READY: st" + client.stationID + " pid=" + client.PID + " pawnSpawned=" + client.pawnSpawned + " entity=" + (client.pawnConcreteEntity != null) + " totalClients=" + Global.clients.Count);
                        Log.WriteLine(1, "[DS] Client ClientReady (0x35) -> set params bit 0x2000 + ChangeState(5) Loop");
                        // Player is now ready/alive (abstract state 5). Drive a real ROUND (BM 900 ->
                        // AI_Match.state 2): the round self-ticks client-side (AI_MatchClient::Update @0x101bfea0
                        // -> AI_MatchRoundClient::UpdateRoundClock @0x101c2080, currentRoundLength =
                        // netclock/1000 - roundStartTime), giving real wave respawns + a round timer. Warmup
                        // (state 1) still covers the FIRST spawn; this fires AFTER the player is alive so the
                        // deploy-screen gate (IsWaitingForWave && abstract.currentState==2) can't re-engage.
                        // AFK-RETURN FIX 2026-06-15: was StartRound (ROUND/state 2). The round's spawn-wave id is
                        // server-fed (AI_MatchRound::GetTeamSpawnWaveID) and the emulated DS never advances it, so ANY
                        // re-deploy — death, OR the AFK/idle camera dropping the player back to WaitForSpawn — got
                        // stuck on the respawn screen with combat input (ADS/stance/cover/fire) blocked FOREVER
                        // (bBlockUserInput held by cInGameMenuManager while IsWaitingForWave && currentState==2).
                        // WARMUP (state 1) uses a CLIENT-ticked wave clock (floor(elapsed/15)+1) that always advances,
                        // so the respawn screen self-clears. Trade-off: no round timer/scoring (round respawns were
                        // broken anyway). Proper fix later = drive the round wave. See [[gro-afk-return-combat-gate]].
                        msgs.Add(DO_RMCRequestMessage.Create(client.callCounterDO_RMC++,
                            0x1006,
                            new DupObj(DupObjClass.Station, 1),
                            new DupObj(DupObjClass.NET_MessageBroker, 5),
                            (ushort)DO_RMCRequestMessage.DOC_METHOD.ProcessMessage,
                            BM_Message.Make(new MSG_ID_BM_Warmup())
                            ));
                        Log.WriteLine(1, "[DS] ClientReady -> Warmup (BM 901) state 1 (client-ticked wave clock; avoids the AFK-return respawn-screen input lock)");
                    }
                    break;
            }
            if (msgs.Count > 0)
                return DO_BundleMessage.Create(client, msgs);
            else
                return null;
        }
    }
}
