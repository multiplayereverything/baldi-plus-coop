using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    // Client item effects on host NPCs and world. Owned by one feature; called from NpcFeatures.cs.
    // Ops: ItemsToClient= 82 (host to clients), ItemsToHost= 83 (client to host).
    //
    // A client's item runs natively on its own machine (its own view, sounds and self effects), but replica NPCs
    // have no AI, so anything that must move or stop a real NPC is reported to the host with a one-time token.
    // The host validates it and runs the native effect against its real NPCs: it spawns the native BSODA spray,
    // nana peel, alarm clock, chalk cloud or WD-40 object at the user's pose, blows the whistle for its Principals,
    // stuns its 1st Prize, gives its Baldi the apple, and inserts lock/key/quarter/tape items into its acceptors.
    // Host item objects travel in this file's world section; other clients spawn inert native copies for the view.
    // Rule while "friendly item effects" is not an option yet: an item affects NPCs and its user only, never teammates.
    public sealed partial class TogetherRuntime
    {
        const byte ItemSpray = 1, ItemPeel = 2, ItemClock = 3, ItemCloud = 4, ItemNoSquee = 5, ItemWhistle = 6;
        const byte AcceptSwingLock = 1, AcceptDetentionKey = 2, AcceptCoinDoor = 3, AcceptTape = 4;
        const byte NpcItemCut = 1, NpcItemApple = 2, NpcItemAppleContact = 3;
        const int MaxItemEffects = 32;

        sealed class HostItemEffect { public int Owner, Token; public byte Kind; public string Prefab; public Vector3 Origin, Dir; public Item Obj; public float EndsAt; public bool Relayed; }
        sealed class ClientItemEffect { public int Owner, Token; public byte Kind; public Item Obj; public float At, LastWind = -10; public bool Acked, PeelReported; }
        sealed class ItemWorldEntry { public int Owner, Token; public byte Kind; public string Prefab; public Vector3 Origin, Dir; public float Value; }
        sealed class BaldiLure { public Vector3 Position; public float Until; }

        readonly List<HostItemEffect> hostItemEffects = new List<HostItemEffect>();
        readonly Dictionary<int, ClientItemEffect> ownItemEffects = new Dictionary<int, ClientItemEffect>();
        readonly Dictionary<long, ClientItemEffect> copiedItemEffects = new Dictionary<long, ClientItemEffect>();
        readonly HashSet<long> spawnedItemCopies = new HashSet<long>();
        readonly HashSet<Object> inertItemObjects = new HashSet<Object>();
        readonly HashSet<Object> relayedItemObjects = new HashSet<Object>();
        readonly Dictionary<int, int> itemTokens = new Dictionary<int, int>();
        readonly Dictionary<int, float> itemActionAt = new Dictionary<int, float>();
        readonly Dictionary<Baldi, BaldiLure> baldiLures = new Dictionary<Baldi, BaldiLure>();
        readonly Dictionary<string, Item> itemPrefabs = new Dictionary<string, Item>();
        readonly HashSet<string> missingItemPrefabs = new HashSet<string>();
        readonly Dictionary<NPC, float> npcItemCooldown = new Dictionary<NPC, float>();
        int hostItemToken, clientItemToken, pendingAppleToken;
        float nextAppleContact, nextItemPrune, pendingAppleAt;
        bool itemsLabStarted, itemsLabMirror;
        string itemRejectReason = "";
        int itemRelays, itemRejects, itemLures, itemAcceptors, itemMirrors, itemNpcActions, itemApplesConsumed, itemCopySpawns;

        static readonly FieldInfo sodaEc = AccessTools.Field(typeof(ITM_BSODA), "ec"), sodaEntity = AccessTools.Field(typeof(ITM_BSODA), "entity"),
            sodaMove = AccessTools.Field(typeof(ITM_BSODA), "moveMod"), sodaSprite = AccessTools.Field(typeof(ITM_BSODA), "spriteRenderer"),
            sodaMods = AccessTools.Field(typeof(ITM_BSODA), "activityMods");
        static readonly FieldInfo peelThrow = AccessTools.Field(typeof(ITM_NanaPeel), "throwSpeed"), peelAudio = AccessTools.Field(typeof(ITM_NanaPeel), "audioManager"),
            peelEnd = AccessTools.Field(typeof(ITM_NanaPeel), "audEnd"), peelSlipping = AccessTools.Field(typeof(ITM_NanaPeel), "slipping"),
            peelDying = AccessTools.Field(typeof(ITM_NanaPeel), "dying");
        static readonly FieldInfo clockEc = AccessTools.Field(typeof(ITM_AlarmClock), "ec"), clockEntity = AccessTools.Field(typeof(ITM_AlarmClock), "entity"),
            clockTime = AccessTools.Field(typeof(ITM_AlarmClock), "time"), clockFinished = AccessTools.Field(typeof(ITM_AlarmClock), "finished"),
            clockSetTimes = AccessTools.Field(typeof(ITM_AlarmClock), "setTime"), clockInitial = AccessTools.Field(typeof(ITM_AlarmClock), "initSetTime");
        static readonly MethodInfo clockTimer = AccessTools.Method(typeof(ITM_AlarmClock), "Timer");
        static readonly FieldInfo eraserCloud = AccessTools.Field(typeof(ChalkEraser), "cloud"), eraserTime = AccessTools.Field(typeof(ChalkEraser), "setTime");
        static readonly FieldInfo squeeCells = AccessTools.Field(typeof(ITM_NoSquee), "silencedCells"), squeeEmitters = AccessTools.Field(typeof(ITM_NoSquee), "sparkleParticelEmitters"),
            squeePrefab = AccessTools.Field(typeof(ITM_NoSquee), "sparkleParticlesPre"), squeeTime = AccessTools.Field(typeof(ITM_NoSquee), "time"),
            squeeDistance = AccessTools.Field(typeof(ITM_NoSquee), "distance");
        static readonly MethodInfo squeeTimer = AccessTools.Method(typeof(ITM_NoSquee), "Timer");
        static readonly FieldInfo baldiAudio = AccessTools.Field(typeof(Baldi), "audMan"), baldiThanks = AccessTools.Field(typeof(Baldi), "audAppleThanks"),
            principalAudio = AccessTools.Field(typeof(Principal), "audMan"), principalComing = AccessTools.Field(typeof(Principal), "audComing"),
            coinSwing = AccessTools.Field(typeof(CoinDoor), "swingDoor"), ecSilent = AccessTools.Field(typeof(EnvironmentController), "silent");

        static long ItemCopyKey(int owner, int token) { return ((long)owner << 32) | (uint)token; }

        static byte ItemKind(Item item)
        {
            if (item is ITM_BSODA) return ItemSpray;
            if (item is ITM_NanaPeel) return ItemPeel;
            if (item is ITM_AlarmClock) return ItemClock;
            if (item is ChalkEraser) return ItemCloud;
            if (item is ITM_NoSquee) return ItemNoSquee;
            if (item is ITM_PrincipalWhistle) return ItemWhistle;
            return 0;
        }

        static string ItemPrefabName(Item item)
        { string name = item.name; return name.EndsWith("(Clone)", StringComparison.Ordinal) ? name.Substring(0, name.Length - 7) : name; }

        Item ResolveItemPrefab(byte kind, string name)
        {
            Item prefab; string key = kind + ":" + name;
            if (itemPrefabs.TryGetValue(key, out prefab) && prefab != null) return prefab;
            // Unknown names are remembered so a bad client cannot force an asset scan per packet.
            if (missingItemPrefabs.Contains(key)) return null;
            prefab = Resources.FindObjectsOfTypeAll<Item>().FirstOrDefault(i => i != null && !i.gameObject.scene.IsValid() && i.name == name && ItemKind(i) == kind);
            if (prefab != null) itemPrefabs[key] = prefab;
            else { if (missingItemPrefabs.Count >= 256) missingItemPrefabs.Clear(); missingItemPrefabs.Add(key); }
            return prefab;
        }

        // Native durations: the clock's first setting, the chalk cloud's lifetime and WD-40's silence.
        static float ItemDuration(byte kind, Item item)
        {
            if (item == null) return 0;
            if (kind == ItemClock) { var times = (float[])clockSetTimes.GetValue(item); int index = (int)clockInitial.GetValue(item); return times != null && index >= 0 && index < times.Length ? times[index] : 30; }
            if (kind == ItemCloud) return (float)eraserTime.GetValue(item);
            if (kind == ItemNoSquee) return (float)squeeTime.GetValue(item);
            return 0;
        }

        void ResetItems()
        {
            ReleaseBaldiLures();
            foreach (var effect in hostItemEffects) if (effect.Relayed) CleanupItemEffect(effect.Obj, effect.Kind);
            foreach (var copy in copiedItemEffects.Values) CleanupItemEffect(copy.Obj, copy.Kind);
            hostItemEffects.Clear(); ownItemEffects.Clear(); copiedItemEffects.Clear(); spawnedItemCopies.Clear();
            inertItemObjects.Clear(); relayedItemObjects.Clear(); itemTokens.Clear(); itemActionAt.Clear();
            hostItemToken = clientItemToken = pendingAppleToken = 0; nextAppleContact = nextItemPrune = pendingAppleAt = 0;
            npcItemCooldown.Clear(); missingItemPrefabs.Clear();
            itemsLabMirror = false;
        }

        void ReleaseBaldiLures()
        {
            foreach (var baldi in baldiLures.Keys) { ChaseTarget chase; if (baldi != null && chaseTargets.TryGetValue(baldi, out chase)) chase.Next = 0; }
            baldiLures.Clear();
        }

        // Native cleanup the object would otherwise only do at the end of its own timer.
        void CleanupItemEffect(Item item, byte kind)
        {
            if (item == null) return;
            if (kind == ItemSpray)
            {
                var mods = sodaMods.GetValue(item) as List<ActivityModifier>; var mod = sodaMove.GetValue(item) as MovementModifier;
                if (mods != null) { foreach (var m in mods) if (m != null) m.moveMods.Remove(mod); mods.Clear(); }
            }
            else if (kind == ItemNoSquee)
            {
                var cells = squeeCells.GetValue(item) as List<Cell>; var emitters = squeeEmitters.GetValue(item) as List<Transform>;
                if (cells != null) { foreach (var cell in cells) if (cell != null) cell.SetSilence(false); cells.Clear(); }
                if (emitters != null) { foreach (var emitter in emitters) if (emitter != null) Object.Destroy(emitter.gameObject); emitters.Clear(); }
            }
            inertItemObjects.Remove(item); relayedItemObjects.Remove(item);
            Object.Destroy(item.gameObject);
        }

        // Runs the native Use body without a PlayerManager: same fields, same native calls, at a reported pose.
        Item SpawnItemEffect(byte kind, Item prefab, Vector3 origin, Vector3 dir, float value, bool inert, EnvironmentController ec)
        {
            Item item = Object.Instantiate(prefab);
            if (inert) inertItemObjects.Add(item); else relayedItemObjects.Add(item);
            if (kind == ItemSpray)
            {
                sodaEc.SetValue(item, ec);
                item.transform.position = origin; item.transform.forward = dir;
                ((Entity)sodaEntity.GetValue(item)).Initialize(ec, origin);
                var sprite = sodaSprite.GetValue(item) as SpriteRenderer;
                if (sprite != null) sprite.SetSpriteRotation(UnityEngine.Random.Range(0f, 360f));
                ((MovementModifier)sodaMove.GetValue(item)).priority = 1;
            }
            else if (kind == ItemPeel)
            {
                var peel = (ITM_NanaPeel)item;
                peel.Spawn(ec, origin, dir, (float)peelThrow.GetValue(peel));
                var audio = peelAudio.GetValue(peel) as AudioManager; var sound = peelEnd.GetValue(peel) as SoundObject;
                if (audio != null && sound != null) audio.PlaySingle(sound);
            }
            else if (kind == ItemClock)
            {
                clockEc.SetValue(item, ec);
                item.transform.position = origin;
                ((Entity)clockEntity.GetValue(item)).Initialize(ec, origin);
                item.StartCoroutine((IEnumerator)clockTimer.Invoke(item, new object[] { Mathf.Max(0.01f, value) }));
            }
            else if (kind == ItemCloud)
            {
                var eraser = (ChalkEraser)item; IntVector2 grid = IntVector2.GetGridPosition(origin);
                eraser.ec = ec;
                item.transform.localPosition = new Vector3(grid.x * 10f + 5f, ec.transform.position.y + 5f, grid.z * 10f + 5f);
                var cloud = (CoverCloud)eraserCloud.GetValue(item);
                if (inert) inertItemObjects.Add(cloud); else relayedItemObjects.Add(cloud);
                cloud.Ec = ec; cloud.StartEndTimer(Mathf.Max(0.01f, value));
            }
            else if (kind == ItemNoSquee)
            {
                Cell start = ec.CellFromPosition(origin);
                if (start == null || start.Null) { CleanupItemEffect(item, kind); return null; }
                var cells = (List<Cell>)squeeCells.GetValue(item); var emitters = (List<Transform>)squeeEmitters.GetValue(item);
                var sparkle = (Transform)squeePrefab.GetValue(item); int distance = (int)squeeDistance.GetValue(item);
                cells.Add(start);
                var map = new DijkstraMap(ec, PathType.Nav, int.MaxValue);
                map.Calculate(distance + 1, true, start.position);
                cells.AddRange(map.FoundCells());
                foreach (Cell cell in cells)
                {
                    cell.SetSilence(true);
                    if (map.Value(cell.position) <= distance) emitters.Add(Object.Instantiate(sparkle, cell.ObjectBase));
                }
                item.StartCoroutine((IEnumerator)squeeTimer.Invoke(item, new object[] { ec, Mathf.Max(0.01f, value) }));
            }
            return item;
        }

        // ---------------- Local use (host and client) ----------------

        public void ItemUsed(Item item, PlayerManager pm, bool used)
        {
            if (!used || item == null || pm == null || pm.playerNumber != 0 || !Active || !Running || !worldReady || Applying || LocalEliminated) return;
            byte kind = ItemKind(item); if (kind == 0) return;
            Vector3 origin = pm.transform.position;
            Vector3 dir = kind == ItemSpray || kind == ItemPeel ? item.transform.forward : pm.transform.forward;
            string prefab = ItemPrefabName(item); float now = Time.realtimeSinceStartup;
            if (Hosting)
            {
                if (kind == ItemWhistle) return;
                hostItemEffects.Add(new HostItemEffect { Owner = self, Token = ++hostItemToken, Kind = kind, Prefab = prefab, Origin = origin, Dir = dir, Obj = item, EndsAt = now + ItemDuration(kind, item) });
                return;
            }
            if (!readyAcknowledged) return;
            int token = ++clientItemToken;
            if (kind != ItemWhistle) ownItemEffects[token] = new ClientItemEffect { Owner = self, Token = token, Kind = kind, Obj = item, At = now };
            Network.Send(0, Wire.Packet(Op.ItemsToHost, w => { w.Write(epoch); w.Write((byte)1); w.Write(token); w.Write(kind); Wire.Text(w, prefab); WriteVector(w, origin); WriteVector(w, dir); }));
        }

        // Client replicas are driven by the host, and teammates are not hit by another player's item.
        public bool BlockItemContact(Object source, Entity entity, Collider other)
        {
            if (!Active || source == null) return false;
            if (inertItemObjects.Contains(source)) return true;
            bool npc = (entity != null && entity.GetComponentInParent<NPC>() != null) || (other != null && other.GetComponentInParent<NPC>() != null);
            if (npc) return ModHooks.ClientWorld;
            bool player = (other != null && other.CompareTag("Player")) || (entity != null && entity.GetComponentInParent<PlayerManager>() != null);
            return player && Hosting && relayedItemObjects.Contains(source);
        }

        public bool RelayNpcItem(byte action, NPC npc)
        {
            if (!ModHooks.ClientWorld || Applying) return false;
            int index = Array.IndexOf(npcs, npc);
            if (worldReady && readyAcknowledged && !LocalEliminated && index >= 0)
            {
                int token = ++clientItemToken, character = (int)npc.Character;
                Network.Send(0, Wire.Packet(Op.ItemsToHost, w => { w.Write(epoch); w.Write((byte)4); w.Write(token); w.Write(action); w.Write(index); w.Write(character); }));
                itemNpcActions++;
                if (action == NpcItemApple) PlayFieldSound(npc, baldiAudio, baldiThanks);
            }
            return true;
        }

        public bool SkipReplicaWhistle(Principal principal)
        {
            if (!ModHooks.ClientWorld || Applying) return false;
            // The host's real Principal answers; the replica only gives the user the native "coming" cue.
            var audio = principalAudio.GetValue(principal) as AudioManager;
            if (audio != null) audio.FlushQueue(true);
            PlayFieldSound(principal, principalAudio, principalComing);
            return true;
        }

        static void PlayFieldSound(object owner, FieldInfo audioField, FieldInfo soundField)
        {
            if (owner == null || audioField == null || soundField == null) return;
            var audio = audioField.GetValue(owner) as AudioManager; var sound = soundField.GetValue(owner) as SoundObject;
            if (audio != null && sound != null) audio.PlaySingle(sound);
        }

        public bool RelayAcceptor(byte kind, Component target)
        {
            if (!ModHooks.ClientWorld || Applying) return false;
            string key = AcceptorKey(kind, target);
            if (key != null && worldReady && readyAcknowledged && !LocalEliminated)
            {
                int token = ++clientItemToken;
                Network.Send(0, Wire.Packet(Op.ItemsToHost, w => { w.Write(epoch); w.Write((byte)5); w.Write(token); w.Write(kind); Wire.Text(w, key); }));
                itemAcceptors++;
            }
            return true;
        }

        public void HostAcceptorUsed(byte kind, Component target)
        {
            if (!Active || !Hosting || !Running || !worldReady || Applying) return;
            string key = AcceptorKey(kind, target);
            if (key != null) SendAcceptorMirror(kind, key, self);
        }

        void SendAcceptorMirror(byte kind, string key, int user)
        { SendAll(Wire.Packet(Op.ItemsToClient, w => { w.Write(epoch); w.Write((byte)2); w.Write(kind); Wire.Text(w, key); w.Write((byte)user); })); }

        string AcceptorKey(byte kind, Component target)
        {
            if (target == null) return null;
            if (kind == AcceptDetentionKey) { string key = Key(target); return doors.ContainsKey(key) ? key : null; }
            if (kind == AcceptTape) return Key(target);
            Object door = kind == AcceptCoinDoor ? coinSwing.GetValue(target) as SwingDoor : target as SwingDoor;
            if (door == null) return null;
            foreach (var pair in extraDoors) if ((Object)pair.Value == door) return pair.Key;
            return null;
        }

        Component FindAcceptor(byte kind, string key)
        {
            if (kind == AcceptDetentionKey) { StandardDoor standard; return doors.TryGetValue(key, out standard) ? standard : null; }
            if (kind == AcceptTape) return Object.FindObjectsOfType<TapePlayer>().FirstOrDefault(t => Key(t) == key);
            Door door;
            if (!extraDoors.TryGetValue(key, out door) || !(door is SwingDoor)) return null;
            if (kind == AcceptSwingLock) return door;
            return Object.FindObjectsOfType<CoinDoor>().FirstOrDefault(c => (Object)(coinSwing.GetValue(c) as SwingDoor) == (Object)door);
        }

        void ApplyAcceptor(Component target)
        {
            var core = Singleton<CoreGameManager>.Instance; var manager = Singleton<BaseGameManager>.Instance;
            var acceptor = target as IItemAcceptor;
            if (core == null || manager == null || acceptor == null) return;
            Applying = true;
            try { acceptor.InsertItem(core.GetPlayer(0), manager.Ec); }
            finally { Applying = false; }
        }

        public void ClockClicked(ITM_AlarmClock clock)
        {
            if (!ModHooks.ClientWorld || Applying || !worldReady || !readyAcknowledged || LocalEliminated) return;
            ClientItemEffect effect = ownItemEffects.Values.FirstOrDefault(e => (Object)e.Obj == (Object)clock) ?? copiedItemEffects.Values.FirstOrDefault(e => (Object)e.Obj == (Object)clock);
            if (effect == null) return;
            effect.LastWind = Time.realtimeSinceStartup;
            int token = ++clientItemToken, owner = effect.Owner, entry = effect.Token; float time = (float)clockTime.GetValue(clock);
            Network.Send(0, Wire.Packet(Op.ItemsToHost, w => { w.Write(epoch); w.Write((byte)2); w.Write(token); w.Write((byte)owner); w.Write(entry); w.Write(time); }));
        }

        // An alarm clock is a lure. HidingSync steers a chasing Baldi to the nearest teammate and suppresses Baldi.Hear
        // while he has one; a ringing item clock suspends that steering so the native hearing runs, until he arrives.
        public void ItemNoise(EnvironmentController ec, Vector3 position)
        {
            if (!Active || !Hosting || !worldReady || ec == null) return;
            bool clock = false;
            foreach (var effect in hostItemEffects)
                if (effect.Kind == ItemClock && effect.Obj != null && (effect.Obj.transform.position - position).sqrMagnitude < 1) { clock = true; break; }
            if (!clock) return;
            // Same early-outs as the native MakeNoise (clocks have no silent chance).
            if ((bool)ecSilent.GetValue(ec) || ec.CellFromPosition(IntVector2.GetGridPosition(position)).Silent) return;
            float now = Time.realtimeSinceStartup;
            foreach (var baldi in ec.Npcs.OfType<Baldi>())
            {
                if (baldi == null || !(baldi.behaviorStateMachine.CurrentState is Baldi_Chase)) continue;
                ChaseTarget chase;
                if (!chaseTargets.TryGetValue(baldi, out chase)) { chase = new ChaseTarget(); chaseTargets.Add(baldi, chase); }
                chase.HasTarget = false; chase.Next = now + 20;
                baldiLures[baldi] = new BaldiLure { Position = position, Until = now + 20 };
                itemLures++;
            }
        }

        // ---------------- Host: client reports ----------------

        bool ItemPeerValid(Peer sender)
        { return sender != null && sender.Accepted && sender.Ready && sender.HasPose && !sender.Eliminated && !sender.InLocker && Time.realtimeSinceStartup - sender.PoseAt < 2; }

        bool ItemCommandValid(int id, Peer sender, int generation, int token)
        {
            if (generation != epoch || !Running || !worldReady || !ItemPeerValid(sender)) { itemRejects++; itemRejectReason = "generation-or-sender"; return false; }
            int last; itemTokens.TryGetValue(id, out last);
            if (token <= last) { itemRejects++; itemRejectReason = "replayed-token"; return false; }
            itemTokens[id] = token;
            return true;
        }

        bool ItemRateAllowed(int id)
        {
            float now = Time.realtimeSinceStartup, at;
            if (itemActionAt.TryGetValue(id, out at) && now - at < 0.1f) { itemRejects++; itemRejectReason = "rate"; return false; }
            itemActionAt[id] = now; return true;
        }

        static float Flat(Vector3 a, Vector3 b) { Vector3 d = a - b; d.y = 0; return d.magnitude; }

        bool HandleItemsClient(int id, Peer sender, Op op, BinaryReader r)
        {
            if (op != Op.ItemsToHost) return false;
            int generation = r.ReadInt32(); byte sub = r.ReadByte(); int token = r.ReadInt32();
            if (token < 1) throw new IOException("Invalid item token.");
            if (sub == 1)
            {
                byte kind = r.ReadByte(); string prefab = Wire.Text(r, 64); Vector3 origin = ReadVector(r), dir = ReadVector(r);
                if (kind < ItemSpray || kind > ItemWhistle) throw new IOException("Invalid item kind.");
                if (ItemCommandValid(id, sender, generation, token) && ItemRateAllowed(id)) HostRelayUse(id, sender, token, kind, prefab, origin, dir);
            }
            else if (sub == 2)
            {
                int owner = r.ReadByte(), entry = r.ReadInt32(); float time = Wire.Number(r);
                if (owner > 3 || entry < 1 || time < 0 || time > 600) throw new IOException("Invalid clock wind.");
                if (ItemCommandValid(id, sender, generation, token)) HostWindClock(sender, owner, entry, time);
            }
            else if (sub == 3)
            {
                int entry = r.ReadInt32();
                if (entry < 1) throw new IOException("Invalid peel report.");
                if (generation == epoch && worldReady && sender != null && sender.Ready)
                {
                    int last; itemTokens.TryGetValue(id, out last);
                    if (token > last) { itemTokens[id] = token; HostPeelTaken(id, entry); }
                }
            }
            else if (sub == 4)
            {
                byte action = r.ReadByte(); int index = r.ReadInt32(), character = r.ReadInt32();
                if (action < NpcItemCut || action > NpcItemAppleContact) throw new IOException("Invalid NPC item action.");
                if (ItemCommandValid(id, sender, generation, token) && ItemRateAllowed(id)) HostNpcItem(id, sender, token, action, index, character);
            }
            else if (sub == 5)
            {
                byte kind = r.ReadByte(); string key = Wire.Text(r, 256);
                if (kind < AcceptSwingLock || kind > AcceptTape) throw new IOException("Invalid item acceptor.");
                if (ItemCommandValid(id, sender, generation, token) && ItemRateAllowed(id)) HostAcceptor(id, sender, kind, key);
            }
            else throw new IOException("Unknown item command.");
            return true;
        }

        void HostRelayUse(int id, Peer sender, int token, byte kind, string prefabName, Vector3 origin, Vector3 dir)
        {
            var manager = Singleton<BaseGameManager>.Instance;
            if (manager == null || manager.Ec == null) { itemRejects++; return; }
            var ec = manager.Ec;
            if (Flat(origin, sender.Position) > 10 || Mathf.Abs(origin.y - sender.Position.y) > 10 || dir.magnitude < 0.5f || dir.magnitude > 1.5f || !ec.ContainsCoordinates(origin))
            { itemRejects++; return; }
            dir.Normalize();
            if (kind == ItemWhistle)
            {
                Applying = true;
                try { foreach (var principal in ec.Npcs.OfType<Principal>().ToArray()) if (principal != null) principal.WhistleReact(origin); }
                finally { Applying = false; }
                itemRelays++; return;
            }
            Item prefab = ResolveItemPrefab(kind, prefabName);
            PruneItemEffects();
            if (prefab == null || hostItemEffects.Count(e => e.Relayed) >= MaxItemEffects) { itemRejects++; return; }
            float value = ItemDuration(kind, prefab);
            Item spawned;
            Applying = true;
            try { spawned = SpawnItemEffect(kind, prefab, origin, dir, value, false, ec); }
            finally { Applying = false; }
            if (spawned == null) { itemRejects++; return; }
            hostItemEffects.Add(new HostItemEffect { Owner = id, Token = token, Kind = kind, Prefab = prefabName, Origin = origin, Dir = dir, Obj = spawned, EndsAt = Time.realtimeSinceStartup + value, Relayed = true });
            itemRelays++;
        }

        void HostWindClock(Peer sender, int owner, int entry, float time)
        {
            var effect = hostItemEffects.FirstOrDefault(e => e.Owner == owner && e.Token == entry && e.Kind == ItemClock && e.Obj != null);
            if (effect == null || (bool)clockFinished.GetValue(effect.Obj) || Flat(effect.Obj.transform.position, sender.Position) > 25) { itemRejects++; return; }
            clockTime.SetValue(effect.Obj, Mathf.Clamp(time, 0, 60));
        }

        void HostPeelTaken(int id, int entry)
        {
            var effect = hostItemEffects.FirstOrDefault(e => e.Owner == id && e.Token == entry && e.Kind == ItemPeel && e.Obj != null);
            if (effect == null || (bool)peelSlipping.GetValue(effect.Obj) || (bool)peelDying.GetValue(effect.Obj)) return;
            CleanupItemEffect(effect.Obj, effect.Kind); hostItemEffects.Remove(effect);
        }

        void HostNpcItem(int id, Peer sender, int token, byte action, int index, int character)
        {
            if (index < 0 || index >= npcs.Length || npcs[index] == null || (int)npcs[index].Character != character) { itemRejects++; itemRejectReason = "npc-index"; return; }
            NPC npc = npcs[index]; float distance = Flat(npc.transform.position, sender.Position), now = Time.realtimeSinceStartup;
            // One accepted item action per NPC per second, so a spamming client cannot keep an NPC locked in a state.
            float cooldown;
            if (npcItemCooldown.TryGetValue(npc, out cooldown) && now < cooldown) { itemRejects++; itemRejectReason = "cooldown"; return; }
            // Native reach: a 4-unit sphere or PlayerClick reach (with stickers). A reach extender is honoured along a clear line up to 40.
            bool reach = distance <= 20 || (distance <= 40 && ClearWorldLine(sender.Position, npc.transform.position));
            var prize = npc as FirstPrize; var baldi = npc as Baldi;
            if (action == NpcItemCut && prize != null && reach && !(prize.behaviorStateMachine.CurrentState is FirstPrize_Stunned))
            {
                npcItemCooldown[npc] = now + 1;
                Applying = true; try { prize.CutWires(); } finally { Applying = false; }
                itemNpcActions++; return;
            }
            if (action == NpcItemApple && baldi != null && reach && !(baldi.behaviorStateMachine.CurrentState is Baldi_Apple))
            {
                npcItemCooldown[npc] = now + 1;
                Applying = true; try { baldi.TakeApple(); } finally { Applying = false; }
                itemNpcActions++; return;
            }
            // Native Baldi_Chase contact takes a held apple instead of catching the player.
            if (action == NpcItemAppleContact && baldi != null && distance <= 7 && baldi.behaviorStateMachine.CurrentState is Baldi_Chase)
            {
                npcItemCooldown[npc] = now + 1;
                Applying = true; try { baldi.TakeApple(); } finally { Applying = false; }
                Network.Send(id, Wire.Packet(Op.ItemsToClient, w => { w.Write(epoch); w.Write((byte)1); w.Write(token); }));
                itemNpcActions++; return;
            }
            itemRejects++; itemRejectReason = reach ? "npc-state" : "reach";
        }

        void HostAcceptor(int id, Peer sender, byte kind, string key)
        {
            Component target = FindAcceptor(kind, key);
            if (target == null) { itemRejects++; return; }
            Vector3 at = kind == AcceptDetentionKey ? ((StandardDoor)target).CenteredPosition : target.transform.position;
            bool fits = kind == AcceptSwingLock ? ((SwingDoor)target).ItemFits(Items.DoorLock)
                : kind == AcceptDetentionKey ? ((StandardDoor)target).ItemFits(Items.DetentionKey)
                : kind == AcceptTape ? ((TapePlayer)target).ItemFits(Items.Tape) : true;
            if (Flat(at, sender.Position) > 25 || !fits) { itemRejects++; return; }
            ApplyAcceptor(target);
            SendAcceptorMirror(kind, key, id);
            itemAcceptors++;
        }

        // ---------------- Client: host messages ----------------

        bool HandleItemsHost(Op op, BinaryReader r)
        {
            if (op != Op.ItemsToClient) return false;
            int generation = r.ReadInt32(); byte sub = r.ReadByte();
            bool live = generation == epoch && worldReady && (readyAcknowledged || Hosting);
            if (sub == 1)
            {
                int token = r.ReadInt32();
                if (token < 1) throw new IOException("Invalid apple token.");
                if (live) ConsumeHeldApple(token);
            }
            else if (sub == 2)
            {
                byte kind = r.ReadByte(); string key = Wire.Text(r, 256); int user = r.ReadByte();
                if (kind < AcceptSwingLock || kind > AcceptTape || user > 3) throw new IOException("Invalid acceptor mirror.");
                Component target = live ? FindAcceptor(kind, key) : null;
                if (target != null && (kind != AcceptTape || ((TapePlayer)target).ItemFits(Items.Tape))) { ApplyAcceptor(target); itemMirrors++; }
            }
            else throw new IOException("Unknown item message.");
            return true;
        }

        void ConsumeHeldApple(int token)
        {
            if (token != pendingAppleToken) return;
            pendingAppleToken = 0;
            var core = Singleton<CoreGameManager>.Instance; var player = core == null ? null : core.GetPlayer(0);
            if (player == null || !player.itm.Has(Items.Apple)) return;
            Applying = true;
            try { player.itm.Remove(Items.Apple); }
            finally { Applying = false; }
            itemApplesConsumed++;
            PlayFieldSound(npcs.OfType<Baldi>().FirstOrDefault(b => b != null), baldiAudio, baldiThanks);
        }

        // ---------------- World section ----------------

        float HostItemValue(HostItemEffect effect, float now)
        {
            float value = 0;
            if (effect.Kind == ItemClock) value = (bool)clockFinished.GetValue(effect.Obj) ? 0 : (float)clockTime.GetValue(effect.Obj);
            else if (effect.Kind == ItemCloud || effect.Kind == ItemNoSquee) value = effect.EndsAt - now;
            return Mathf.Clamp(value, 0, 600);
        }

        void WriteItemsWorld(BinaryWriter w)
        {
            float now = Time.realtimeSinceStartup;
            var live = new List<HostItemEffect>();
            for (int i = hostItemEffects.Count - 1; i >= 0 && live.Count < MaxItemEffects; i--)
            {
                var effect = hostItemEffects[i];
                if (effect.Obj == null) continue;
                // A peel that is slipping or flying away is no longer on the floor for anyone.
                if (effect.Kind == ItemPeel && ((bool)peelSlipping.GetValue(effect.Obj) || (bool)peelDying.GetValue(effect.Obj))) continue;
                live.Add(effect);
            }
            w.Write((byte)live.Count);
            foreach (var effect in live)
            {
                w.Write((byte)effect.Owner); w.Write(effect.Token); w.Write(effect.Kind); Wire.Text(w, effect.Prefab);
                WriteVector(w, effect.Origin); WriteVector(w, effect.Dir); w.Write(HostItemValue(effect, now));
            }
        }

        static List<ItemWorldEntry> ParseItemsWorld(BinaryReader r)
        {
            int count = r.ReadByte();
            if (count > MaxItemEffects) throw new IOException("Item effect limit exceeded.");
            var entries = new List<ItemWorldEntry>(count);
            for (int i = 0; i < count; i++)
            {
                int owner = r.ReadByte(), token = r.ReadInt32(); byte kind = r.ReadByte(); string prefab = Wire.Text(r, 64);
                Vector3 origin = ReadVector(r), dir = ReadVector(r); float value = Wire.Number(r);
                if (owner > 3 || token < 1 || kind < ItemSpray || kind > ItemNoSquee || value < 0 || value > 600) throw new IOException("Invalid item effect.");
                entries.Add(new ItemWorldEntry { Owner = owner, Token = token, Kind = kind, Prefab = prefab, Origin = origin, Dir = dir, Value = value });
            }
            return entries;
        }

        void ReadItemsWorld(BinaryReader r, bool apply)
        {
            var entries = ParseItemsWorld(r);
            if (apply && worldReady && (!Hosting || itemsLabMirror)) ApplyItemsWorld(entries);
        }

        void SyncItemClock(ClientItemEffect effect, float value, float now)
        {
            if (effect.Kind != ItemClock || effect.Obj == null || now - effect.LastWind < 0.5f || (bool)clockFinished.GetValue(effect.Obj)) return;
            float time = (float)clockTime.GetValue(effect.Obj);
            if (Mathf.Abs(time - value) > 1.5f) clockTime.SetValue(effect.Obj, value);
        }

        void ApplyItemsWorld(List<ItemWorldEntry> entries)
        {
            var manager = Singleton<BaseGameManager>.Instance;
            if (manager == null || manager.Ec == null) return;
            float now = Time.realtimeSinceStartup;
            var seenOwn = new HashSet<int>(); var seenCopies = new HashSet<long>();
            foreach (var entry in entries)
            {
                ClientItemEffect effect;
                if (entry.Owner == self)
                {
                    seenOwn.Add(entry.Token);
                    if (ownItemEffects.TryGetValue(entry.Token, out effect)) { effect.Acked = true; SyncItemClock(effect, entry.Value, now); }
                    continue;
                }
                long key = ItemCopyKey(entry.Owner, entry.Token); seenCopies.Add(key);
                if (copiedItemEffects.TryGetValue(key, out effect)) { SyncItemClock(effect, entry.Value, now); continue; }
                if (spawnedItemCopies.Count >= 4096) spawnedItemCopies.Clear();
                if (!spawnedItemCopies.Add(key)) continue;
                Item prefab = ResolveItemPrefab(entry.Kind, entry.Prefab);
                if (prefab == null || !manager.Ec.ContainsCoordinates(entry.Origin)) continue;
                Vector3 dir = entry.Dir.sqrMagnitude > 0.25f ? entry.Dir.normalized : Vector3.forward;
                Item copy = SpawnItemEffect(entry.Kind, prefab, entry.Origin, dir, entry.Value, true, manager.Ec);
                if (copy == null) continue;
                copiedItemEffects[key] = new ClientItemEffect { Owner = entry.Owner, Token = entry.Token, Kind = entry.Kind, Obj = copy, At = now, Acked = true };
                itemCopySpawns++;
            }
            // The host's peel is gone (an NPC slipped on it, or it was rejected): remove the local one too.
            foreach (var effect in ownItemEffects.Values.ToArray())
                if (effect.Kind == ItemPeel && effect.Obj != null && !seenOwn.Contains(effect.Token) && (effect.Acked || now - effect.At > 3)
                    && !(bool)peelSlipping.GetValue(effect.Obj) && !(bool)peelDying.GetValue(effect.Obj))
                { CleanupItemEffect(effect.Obj, effect.Kind); ownItemEffects.Remove(effect.Token); }
            foreach (var pair in copiedItemEffects.ToArray())
                if (!seenCopies.Contains(pair.Key) && (pair.Value.Kind == ItemPeel || pair.Value.Kind == ItemCloud))
                { CleanupItemEffect(pair.Value.Obj, pair.Value.Kind); copiedItemEffects.Remove(pair.Key); }
        }

        // ---------------- Per frame ----------------

        void PruneItemEffects()
        {
            for (int i = hostItemEffects.Count - 1; i >= 0; i--)
                if (hostItemEffects[i].Obj == null) hostItemEffects.RemoveAt(i);
            foreach (var key in ownItemEffects.Where(p => p.Value.Obj == null).Select(p => p.Key).ToArray()) ownItemEffects.Remove(key);
            foreach (var key in copiedItemEffects.Where(p => p.Value.Obj == null).Select(p => p.Key).ToArray()) copiedItemEffects.Remove(key);
        }

        void UpdateItems()
        {
            if (!Active || !worldReady) return;
            float now = Time.realtimeSinceStartup;
            if (now >= nextItemPrune)
            {
                nextItemPrune = now + 1;
                PruneItemEffects();
                inertItemObjects.RemoveWhere(o => o == null); relayedItemObjects.RemoveWhere(o => o == null);
                foreach (var npc in npcItemCooldown.Keys.Where(n => n == null || now >= npcItemCooldown[n]).ToArray()) npcItemCooldown.Remove(npc);
            }
            if (Hosting)
            {
                // A departed player's token stream ends with the connection.
                foreach (int id in itemTokens.Keys.Where(id => !peers.ContainsKey(id)).ToArray()) itemTokens.Remove(id);
                foreach (int id in itemActionAt.Keys.Where(id => !peers.ContainsKey(id)).ToArray()) itemActionAt.Remove(id);
                foreach (var pair in baldiLures.ToArray())
                {
                    Baldi baldi = pair.Key; ChaseTarget chase;
                    if (baldi == null || !chaseTargets.TryGetValue(baldi, out chase)) { baldiLures.Remove(baldi); continue; }
                    if (now > pair.Value.Until || Flat(baldi.transform.position, pair.Value.Position) < 5 || !(baldi.behaviorStateMachine.CurrentState is Baldi_Chase))
                    { chase.Next = 0; baldiLures.Remove(baldi); }
                }
                return;
            }
            if (!readyAcknowledged || !Running) return;
            var core = Singleton<CoreGameManager>.Instance; var player = core == null ? null : core.GetPlayer(0);
            if (player == null) return;
            foreach (var effect in ownItemEffects.Values)
                if (effect.Kind == ItemPeel && effect.Obj != null && !effect.PeelReported && (bool)peelSlipping.GetValue(effect.Obj))
                {
                    effect.PeelReported = true;
                    int token = ++clientItemToken, entry = effect.Token;
                    Network.Send(0, Wire.Packet(Op.ItemsToHost, w => { w.Write(epoch); w.Write((byte)3); w.Write(token); w.Write(entry); }));
                }
            // Native Baldi_Chase takes a held apple on contact instead of catching. The host decides remote catches,
            // so the owner reports the contact slightly early, and removes the apple when the host accepts.
            if (AppleContactAllowed(now) && !LocalEliminated && now >= nextAppleContact && !(localLocker != null && localLocker.playerInside) && player.itm.Has(Items.Apple))
                for (int i = 0; i < npcs.Length; i++)
                {
                    var baldi = npcs[i] as Baldi;
                    if (baldi == null || Flat(baldi.transform.position, player.transform.position) > 4.5f) continue;
                    int token = ++clientItemToken, index = i, character = (int)baldi.Character;
                    pendingAppleToken = token; pendingAppleAt = now; nextAppleContact = now + 1;
                    Network.Send(0, Wire.Packet(Op.ItemsToHost, w => { w.Write(epoch); w.Write((byte)4); w.Write(token); w.Write(NpcItemAppleContact); w.Write(index); w.Write(character); }));
                    break;
                }
        }

        // One contact at a time: a newer contact would orphan the pending acceptance and keep an apple Baldi ate.
        // An unanswered contact expires after 3 s (the host rejected it or the floor changed).
        bool AppleContactAllowed(float now)
        {
            if (pendingAppleToken != 0 && now - pendingAppleAt > 3) pendingAppleToken = 0;
            return pendingAppleToken == 0 && now >= nextAppleContact;
        }

        string ItemsDiagnostic()
        {
            return ",\"itemRelays\":" + itemRelays + ",\"itemRejects\":" + itemRejects + ",\"itemNpcActions\":" + itemNpcActions + ",\"itemAcceptors\":" + itemAcceptors
                + ",\"itemMirrors\":" + itemMirrors + ",\"itemLures\":" + itemLures + ",\"itemApplesConsumed\":" + itemApplesConsumed + ",\"itemCopySpawns\":" + itemCopySpawns
                + ",\"itemHostEffects\":" + hostItemEffects.Count + ",\"itemOwnEffects\":" + ownItemEffects.Count + ",\"itemCopies\":" + copiedItemEffects.Count + ",\"baldiLures\":" + baldiLures.Count;
        }

        // ---------------- Lab tour ----------------

        void UpdateItemsLab()
        {
            if (!Lab.Enabled || itemsLabStarted || !TitleVisible || !Lab.HasArg("-bt-items-tour")) return;
            itemsLabStarted = true; StartCoroutine(TourItems());
        }

        byte[] ItemsWorldBytes()
        { using (var memory = new MemoryStream()) { using (var writer = new BinaryWriter(memory)) WriteItemsWorld(writer); return memory.ToArray(); } }

        bool ReadItemsWorldBytes(byte[] data, bool apply)
        { using (var memory = new MemoryStream(data, false)) using (var reader = new BinaryReader(memory)) { ReadItemsWorld(reader, apply); return memory.Position == memory.Length; } }

        bool FeedItemsClient(int id, Peer sender, byte[] packet)
        {
            using (var memory = new MemoryStream(packet, false)) using (var reader = new BinaryReader(memory))
            { Op op = (Op)reader.ReadByte(); return HandleItemsClient(id, sender, op, reader) && memory.Position == memory.Length; }
        }

        bool FeedItemsHost(byte[] packet)
        {
            using (var memory = new MemoryStream(packet, false)) using (var reader = new BinaryReader(memory))
            { Op op = (Op)reader.ReadByte(); return HandleItemsHost(op, reader) && memory.Position == memory.Length; }
        }

        static byte[] ItemUsePacket(int generation, int token, byte kind, string prefab, Vector3 origin, Vector3 dir)
        { return Wire.Packet(Op.ItemsToHost, w => { w.Write(generation); w.Write((byte)1); w.Write(token); w.Write(kind); Wire.Text(w, prefab ?? ""); WriteVector(w, origin); WriteVector(w, dir); }); }

        int RelayedItemCount(int owner) { return hostItemEffects.Count(e => e.Owner == owner && e.Relayed && e.Obj != null); }

        static string LabItemPrefab(byte kind)
        { var prefab = Resources.FindObjectsOfTypeAll<Item>().FirstOrDefault(i => i != null && !i.gameObject.scene.IsValid() && ItemKind(i) == kind); return prefab == null ? "" : prefab.name; }

        void LogItemsApple(string step, Baldi baldi, Peer peer, int actionsBefore)
        {
            float distance = Flat(baldi.transform.position, peer.Position), cooldown;
            npcItemCooldown.TryGetValue(baldi, out cooldown);
            bool reach = distance <= 20 || (distance <= 40 && ClearWorldLine(peer.Position, baldi.transform.position));
            Log("LAB items apple " + step + ": distance=" + distance.ToString("0.0") + " reach=" + reach + " state=" + baldi.behaviorStateMachine.CurrentState.GetType().Name
                + " cooldownLeft=" + Mathf.Max(0, cooldown - Time.realtimeSinceStartup).ToString("0.00") + " accepted=" + (itemNpcActions > actionsBefore) + " actions=" + (itemNpcActions - actionsBefore)
                + " lastReject=" + (itemRejectReason == "" ? "none" : itemRejectReason));
        }

        IEnumerator TourItems()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            portText = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(); listener.Stop();
            Connect(true); if (!Active) yield break;
            AutoStartRun();
            while (!worldReady) yield return null;
            float until = Time.realtimeSinceStartup + 15;
            while (Time.realtimeSinceStartup < until && !Singleton<BaseGameManager>.Instance.Ec.Active) yield return null;
            yield return new WaitForSecondsRealtime(1);
            var manager = Singleton<BaseGameManager>.Instance; var ec = manager.Ec; var core = Singleton<CoreGameManager>.Instance; var player = core.GetPlayer(0);
            if (ec.Npcs.Count == 0) ec.SpawnNPCs();
            foreach (var type in new[] { typeof(Baldi), typeof(Principal), typeof(FirstPrize) })
                if (!ec.Npcs.Any(n => n != null && n.GetType() == type))
                {
                    var template = Resources.FindObjectsOfTypeAll<NPC>().FirstOrDefault(n => n.GetType() == type && !n.gameObject.scene.IsValid());
                    if (template != null) ec.SpawnNPC(template, ec.RandomCell(false, false, true).position);
                }
            npcs = ec.Npcs.ToArray();
            var baldi = npcs.OfType<Baldi>().FirstOrDefault(); var principal = npcs.OfType<Principal>().FirstOrDefault(); var prize = npcs.OfType<FirstPrize>().FirstOrDefault();
            // Keep Baldi in place: the tour drives his states directly and must not catch anyone.
            if (baldi != null) baldi.Navigator.enabled = false;
            var peer = new Peer { Id = 3, Name = "Item tester", Accepted = true, Ready = true, HasPose = true, Position = ec.spawnPoint, PoseAt = Time.realtimeSinceStartup };
            peers[3] = peer;
            string sprayName = LabItemPrefab(ItemSpray), peelName = LabItemPrefab(ItemPeel), clockName = LabItemPrefab(ItemClock), whistleName = LabItemPrefab(ItemWhistle);

            // 1-3. A remote spray becomes a native host BSODA that pushes host NPCs and spares the host player.
            Vector3 origin = peer.Position;
            FeedItemsClient(3, peer, ItemUsePacket(epoch, 1, ItemSpray, sprayName, origin, Vector3.forward));
            var spray = hostItemEffects.LastOrDefault(e => e.Owner == 3 && e.Token == 1);
            CheckNativeUi("items-spray-relayed", spray != null && spray.Obj is ITM_BSODA && Vector3.Distance(spray.Obj.transform.position, origin) < 0.5f && relayedItemObjects.Contains(spray.Obj));
            MovementModifier sodaMod = spray == null || spray.Obj == null ? null : (MovementModifier)sodaMove.GetValue(spray.Obj);
            bool pushedNpc = false, sparedPlayer = false;
            if (sodaMod != null && principal != null)
            {
                var npcCollider = principal.GetComponentsInChildren<Collider>(true).FirstOrDefault();
                ((ITM_BSODA)spray.Obj).EntityTriggerEnter(principal.Entity, npcCollider, true);
                pushedNpc = principal.Entity.ExternalActivity.moveMods.Contains(sodaMod);
                var playerCollider = player.GetComponentsInChildren<Collider>(true).FirstOrDefault(c => c.CompareTag("Player"));
                if (playerCollider != null)
                {
                    ((ITM_BSODA)spray.Obj).EntityTriggerEnter(player.plm.Entity, playerCollider, true);
                    sparedPlayer = !player.plm.Entity.ExternalActivity.moveMods.Contains(sodaMod);
                }
            }
            CheckNativeUi("items-spray-hits-host-npc", pushedNpc);
            CheckNativeUi("items-spray-spares-host-player", sparedPlayer);

            // 4-6. Replays, other generations, lockers, eliminated senders and implausible poses are refused.
            int relayed = RelayedItemCount(3);
            FeedItemsClient(3, peer, ItemUsePacket(epoch, 1, ItemSpray, sprayName, origin, Vector3.forward));
            CheckNativeUi("items-duplicate-replay-ignored", RelayedItemCount(3) == relayed);
            yield return new WaitForSecondsRealtime(0.15f); peer.PoseAt = Time.realtimeSinceStartup;
            FeedItemsClient(3, peer, ItemUsePacket(epoch + 1, 2, ItemSpray, sprayName, origin, Vector3.forward));
            CheckNativeUi("items-stale-epoch-ignored", RelayedItemCount(3) == relayed);
            peer.InLocker = true; FeedItemsClient(3, peer, ItemUsePacket(epoch, 3, ItemSpray, sprayName, origin, Vector3.forward)); peer.InLocker = false;
            peer.Eliminated = true; FeedItemsClient(3, peer, ItemUsePacket(epoch, 4, ItemSpray, sprayName, origin, Vector3.forward)); peer.Eliminated = false;
            FeedItemsClient(3, peer, ItemUsePacket(epoch, 5, ItemSpray, sprayName, origin + new Vector3(60, 0, 0), Vector3.forward));
            CheckNativeUi("items-invalid-sender-or-pose-rejected", RelayedItemCount(3) == relayed);

            // 7. The whistle calls the host's real Principal to the remote player's position.
            yield return new WaitForSecondsRealtime(0.15f); peer.PoseAt = Time.realtimeSinceStartup;
            FeedItemsClient(3, peer, ItemUsePacket(epoch, 6, ItemWhistle, whistleName, peer.Position, Vector3.forward));
            CheckNativeUi("items-whistle-principal", principal != null && principal.behaviorStateMachine.CurrentState is Principal_WhistleApproach);

            // 8. Scissors from a remote player stun the host's 1st Prize.
            yield return new WaitForSecondsRealtime(0.15f);
            bool stunned = false, stunGuard = false;
            if (prize != null)
            {
                peer.Position = prize.transform.position + Vector3.right * 2; peer.PoseAt = Time.realtimeSinceStartup;
                int index = Array.IndexOf(npcs, (NPC)prize), character = (int)prize.Character;
                FeedItemsClient(3, peer, Wire.Packet(Op.ItemsToHost, w => { w.Write(epoch); w.Write((byte)4); w.Write(7); w.Write(NpcItemCut); w.Write(index); w.Write(character); }));
                stunned = prize.behaviorStateMachine.CurrentState is FirstPrize_Stunned;
                // Past the cooldown and rate limit, a second cut while stunned must not restart the stun.
                var stunState = prize.behaviorStateMachine.CurrentState;
                yield return new WaitForSecondsRealtime(1.1f); peer.PoseAt = Time.realtimeSinceStartup;
                int rejects = itemRejects;
                FeedItemsClient(3, peer, Wire.Packet(Op.ItemsToHost, w => { w.Write(epoch); w.Write((byte)4); w.Write(8); w.Write(NpcItemCut); w.Write(index); w.Write(character); }));
                stunGuard = stunState is FirstPrize_Stunned && (object)prize.behaviorStateMachine.CurrentState == (object)stunState && itemRejects == rejects + 1;
            }
            CheckNativeUi("items-scissors-first-prize", stunned);
            CheckNativeUi("items-stunned-prize-not-restunned", stunGuard);

            // 9. An apple given by a remote player feeds the host's real Baldi.
            yield return new WaitForSecondsRealtime(0.15f);
            bool apple = false, appleGuard = false;
            if (baldi != null)
            {
                peer.Position = baldi.transform.position + Vector3.right * 10; peer.PoseAt = Time.realtimeSinceStartup;
                int index = Array.IndexOf(npcs, (NPC)baldi), character = (int)baldi.Character;
                int actions = itemNpcActions; itemRejectReason = "";
                FeedItemsClient(3, peer, Wire.Packet(Op.ItemsToHost, w => { w.Write(epoch); w.Write((byte)4); w.Write(9); w.Write(NpcItemApple); w.Write(index); w.Write(character); }));
                apple = baldi.behaviorStateMachine.CurrentState is Baldi_Apple && itemNpcActions == actions + 1;
                LogItemsApple("give", baldi, peer, actions);
                // While eating, another apple is refused (no nested Baldi_Apple states).
                var appleState = baldi.behaviorStateMachine.CurrentState;
                yield return new WaitForSecondsRealtime(1.1f); peer.PoseAt = Time.realtimeSinceStartup;
                FeedItemsClient(3, peer, Wire.Packet(Op.ItemsToHost, w => { w.Write(epoch); w.Write((byte)4); w.Write(10); w.Write(NpcItemApple); w.Write(index); w.Write(character); }));
                bool notNested = (object)baldi.behaviorStateMachine.CurrentState == (object)appleState && itemRejectReason == "npc-state";
                LogItemsApple("while-eating", baldi, peer, actions);
                // Out of the apple state but inside the per-NPC cooldown, the next apple is refused too.
                baldi.behaviorStateMachine.ChangeState(new Baldi_Chase(baldi, baldi));
                npcItemCooldown[baldi] = Time.realtimeSinceStartup + 1;
                yield return new WaitForSecondsRealtime(0.15f); peer.PoseAt = Time.realtimeSinceStartup;
                FeedItemsClient(3, peer, Wire.Packet(Op.ItemsToHost, w => { w.Write(epoch); w.Write((byte)4); w.Write(11); w.Write(NpcItemApple); w.Write(index); w.Write(character); }));
                LogItemsApple("in-cooldown", baldi, peer, actions);
                appleGuard = notNested && baldi.behaviorStateMachine.CurrentState is Baldi_Chase && itemRejectReason == "cooldown";
                npcItemCooldown.Remove(baldi);
            }
            CheckNativeUi("items-apple-baldi", apple);
            CheckNativeUi("items-apple-state-and-cooldown-guard", appleGuard);

            // 10. A remote alarm clock rings on the host and Baldi hears it despite the nearest-player steering.
            yield return new WaitForSecondsRealtime(0.15f);
            bool lured = false, released = false;
            if (baldi != null)
            {
                // Hide the host player from Baldi's native Looker so only the clock decides where he goes.
                player.SetHidden(true);
                baldi.behaviorStateMachine.ChangeState(new Baldi_Chase(baldi, baldi));
                peer.PoseAt = Time.realtimeSinceStartup;
                ChaseTarget chase; if (chaseTargets.TryGetValue(baldi, out chase)) chase.Next = 0;
                UpdateNearestBaldi(baldi);
                bool suppressedBefore = UseNearestBaldi(baldi);
                FeedItemsClient(3, peer, ItemUsePacket(epoch, 16, ItemClock, clockName, peer.Position, Vector3.forward));
                FeedItemsClient(3, peer, Wire.Packet(Op.ItemsToHost, w => { w.Write(epoch); w.Write((byte)2); w.Write(17); w.Write((byte)3); w.Write(16); w.Write(0.05f); }));
                float ring = Time.realtimeSinceStartup + 3;
                while (Time.realtimeSinceStartup < ring && !baldiLures.ContainsKey(baldi)) { peer.PoseAt = Time.realtimeSinceStartup; yield return null; }
                lured = suppressedBefore && baldiLures.ContainsKey(baldi) && !UseNearestBaldi(baldi) && baldi.behaviorStateMachine.CurrentNavigationState is NavigationState_TargetPosition;
                BaldiLure lure; if (baldiLures.TryGetValue(baldi, out lure)) lure.Until = Time.realtimeSinceStartup - 1;
                UpdateItems();
                released = !baldiLures.ContainsKey(baldi) && chaseTargets.TryGetValue(baldi, out chase) && chase.Next == 0;
                player.SetHidden(false);
            }
            CheckNativeUi("items-clock-lure-baldi-hears", lured);
            CheckNativeUi("items-lure-released", released);

            // 11. A remote swing-door lock locks the host door, and the mirror locks it on clients exactly once.
            bool hostLocked = false, mirrored = false;
            var swing = extraDoors.FirstOrDefault(p => p.Value is SwingDoor && ((SwingDoor)p.Value).ItemFits(Items.DoorLock)
                && !Object.FindObjectsOfType<CoinDoor>().Any(c => (Object)(coinSwing.GetValue(c) as SwingDoor) == (Object)p.Value));
            if (swing.Value != null)
            {
                yield return new WaitForSecondsRealtime(0.15f);
                peer.Position = swing.Value.transform.position + Vector3.right * 2; peer.PoseAt = Time.realtimeSinceStartup;
                string key = swing.Key;
                FeedItemsClient(3, peer, Wire.Packet(Op.ItemsToHost, w => { w.Write(epoch); w.Write((byte)5); w.Write(18); w.Write(AcceptSwingLock); Wire.Text(w, key); }));
                hostLocked = swing.Value.locked;
                swing.Value.Unlock();
                int mirrorsBefore = itemMirrors;
                FeedItemsHost(Wire.Packet(Op.ItemsToClient, w => { w.Write(epoch); w.Write((byte)2); w.Write(AcceptSwingLock); Wire.Text(w, key); w.Write((byte)3); }));
                mirrored = swing.Value.locked && itemMirrors == mirrorsBefore + 1;
                swing.Value.Unlock();
            }
            CheckNativeUi("items-swing-lock-host-and-mirror", hostLocked && mirrored);

            // 12. The owner removes a held apple exactly once when the host accepts Baldi's contact.
            // While a contact is pending no new contact is sent; an unanswered one expires after 3 s.
            float clock = Time.realtimeSinceStartup;
            pendingAppleToken = 40; pendingAppleAt = clock; nextAppleContact = 0;
            bool blocked = !AppleContactAllowed(clock);
            pendingAppleAt = clock - 4;
            CheckNativeUi("items-one-pending-apple-contact", blocked && AppleContactAllowed(clock) && pendingAppleToken == 0);
            bool consumedOnce = false;
            var appleItem = Resources.FindObjectsOfTypeAll<ItemObject>().FirstOrDefault(o => o.itemType == Items.Apple);
            if (appleItem != null)
            {
                player.itm.AddItem(appleItem);
                int before = player.itm.items.Count(i => i != null && i.itemType == Items.Apple);
                pendingAppleToken = 77; pendingAppleAt = Time.realtimeSinceStartup;
                byte[] consume = Wire.Packet(Op.ItemsToClient, w => { w.Write(epoch); w.Write((byte)1); w.Write(77); });
                FeedItemsHost(consume); FeedItemsHost(consume);
                consumedOnce = before > 0 && player.itm.items.Count(i => i != null && i.itemType == Items.Apple) == before - 1;
            }
            CheckNativeUi("items-apple-consumed-once", consumedOnce);

            // 13. World section round trip: parsed exactly, and other players' effects become inert native copies once.
            yield return new WaitForSecondsRealtime(0.15f);
            peer.Position = ec.spawnPoint; peer.PoseAt = Time.realtimeSinceStartup;
            FeedItemsClient(3, peer, ItemUsePacket(epoch, 19, ItemPeel, peelName, peer.Position, Vector3.forward));
            byte[] section = ItemsWorldBytes();
            int expected; using (var memory = new MemoryStream(section, false)) using (var reader = new BinaryReader(memory)) expected = ParseItemsWorld(reader).Count(e => e.Owner != self);
            bool parsed = ReadItemsWorldBytes(section, false);
            itemsLabMirror = true; int spawnsBefore = itemCopySpawns;
            bool applied = ReadItemsWorldBytes(section, true) && ReadItemsWorldBytes(section, true);
            itemsLabMirror = false;
            CheckNativeUi("items-world-roundtrip-inert-copies", parsed && applied && expected > 0 && itemCopySpawns - spawnsBefore == expected
                && copiedItemEffects.Count == expected && copiedItemEffects.Values.All(c => c.Obj != null && inertItemObjects.Contains(c.Obj)));

            // 14. Disconnect forgets the peer's token stream; Reset destroys relayed objects and copies and undoes their native effects.
            peers.Remove(3); UpdateItems();
            bool forgot = !itemTokens.ContainsKey(3) && !itemActionAt.ContainsKey(3);
            var created = hostItemEffects.Where(e => e.Relayed && e.Obj != null).Select(e => (Object)e.Obj).Concat(copiedItemEffects.Values.Where(c => c.Obj != null).Select(c => (Object)c.Obj)).ToArray();
            ResetItems();
            yield return null;
            CheckNativeUi("items-disconnect-reset-cleanup", forgot && created.Length > 0 && created.All(o => o == null) && hostItemEffects.Count == 0 && copiedItemEffects.Count == 0
                && baldiLures.Count == 0 && inertItemObjects.Count == 0 && relayedItemObjects.Count == 0
                && (sodaMod == null || principal == null || !principal.Entity.ExternalActivity.moveMods.Contains(sodaMod)));

            if (baldi != null) baldi.Navigator.enabled = true;
            WriteLabResults("items-results.json", "Items");
        }
    }

    [HarmonyPatch]
    static class ItemUseRelayPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in new[] { typeof(ITM_BSODA), typeof(ITM_NanaPeel), typeof(ITM_AlarmClock), typeof(ChalkEraser), typeof(ITM_NoSquee), typeof(ITM_PrincipalWhistle) })
                yield return AccessTools.Method(type, "Use", new[] { typeof(PlayerManager) });
        }
        static void Postfix(object __instance, PlayerManager pm, bool __result)
        { var r = ModHooks.Current; if (r != null) r.ItemUsed(__instance as Item, pm, __result); }
    }

    [HarmonyPatch(typeof(ITM_BSODA), "EntityTriggerEnter")]
    static class ItemSprayContactPatch
    { static bool Prefix(ITM_BSODA __instance, Entity otherEntity, Collider other) { var r = ModHooks.Current; return r == null || !r.BlockItemContact(__instance, otherEntity, other); } }

    [HarmonyPatch(typeof(ITM_NanaPeel), "EntityTriggerStay")]
    static class ItemPeelContactPatch
    { static bool Prefix(ITM_NanaPeel __instance, Entity otherEntity, Collider other) { var r = ModHooks.Current; return r == null || !r.BlockItemContact(__instance, otherEntity, other); } }

    [HarmonyPatch(typeof(CoverCloud), "OnTriggerEnter")]
    static class ItemCloudContactPatch
    { static bool Prefix(CoverCloud __instance, Collider other) { var r = ModHooks.Current; return r == null || other == null || !r.BlockItemContact(__instance, other.GetComponent<Entity>(), other); } }

    [HarmonyPatch(typeof(ITM_AlarmClock), "Clicked")]
    static class ItemClockWindPatch
    { static void Postfix(ITM_AlarmClock __instance) { var r = ModHooks.Current; if (r != null) r.ClockClicked(__instance); } }

    [HarmonyPatch(typeof(FirstPrize), "CutWires")]
    static class ItemScissorsPrizePatch
    { static bool Prefix(FirstPrize __instance) { var r = ModHooks.Current; return r == null || !r.RelayNpcItem(1, __instance); } }

    [HarmonyPatch(typeof(Baldi), "TakeApple")]
    static class ItemAppleBaldiPatch
    { static bool Prefix(Baldi __instance) { var r = ModHooks.Current; return r == null || !r.RelayNpcItem(2, __instance); } }

    // Tape players distract Baldi natively; on a client that would only rewrite the replica's navigation.
    [HarmonyPatch(typeof(Baldi), "Distract")]
    static class ItemReplicaDistractPatch
    { static bool Prefix() { return !ModHooks.ClientWorld; } }

    [HarmonyPatch(typeof(Principal), "WhistleReact")]
    static class ItemWhistleReplicaPatch
    { static bool Prefix(Principal __instance) { var r = ModHooks.Current; return r == null || !r.SkipReplicaWhistle(__instance); } }

    [HarmonyPatch(typeof(SwingDoor), "InsertItem")]
    static class ItemSwingLockPatch
    {
        static bool Prefix(SwingDoor __instance) { var r = ModHooks.Current; return r == null || !r.RelayAcceptor(1, __instance); }
        static void Postfix(SwingDoor __instance) { var r = ModHooks.Current; if (r != null) r.HostAcceptorUsed(1, __instance); }
    }

    [HarmonyPatch(typeof(StandardDoor), "InsertItem")]
    static class ItemDetentionKeyPatch
    {
        static bool Prefix(StandardDoor __instance) { var r = ModHooks.Current; return r == null || !r.RelayAcceptor(2, __instance); }
        static void Postfix(StandardDoor __instance) { var r = ModHooks.Current; if (r != null) r.HostAcceptorUsed(2, __instance); }
    }

    [HarmonyPatch(typeof(CoinDoor), "InsertItem")]
    static class ItemCoinDoorPatch
    {
        static bool Prefix(CoinDoor __instance) { var r = ModHooks.Current; return r == null || !r.RelayAcceptor(3, __instance); }
        static void Postfix(CoinDoor __instance) { var r = ModHooks.Current; if (r != null) r.HostAcceptorUsed(3, __instance); }
    }

    [HarmonyPatch(typeof(TapePlayer), "InsertItem")]
    static class ItemTapePlayerPatch
    {
        static bool Prefix(TapePlayer __instance) { var r = ModHooks.Current; return r == null || !r.RelayAcceptor(4, __instance); }
        static void Postfix(TapePlayer __instance) { var r = ModHooks.Current; if (r != null) r.HostAcceptorUsed(4, __instance); }
    }

    [HarmonyPatch(typeof(EnvironmentController), "MakeNoise", new[] { typeof(GameObject), typeof(Vector3), typeof(int), typeof(bool) })]
    static class ItemNoiseLurePatch
    { static void Prefix(EnvironmentController __instance, Vector3 position) { var r = ModHooks.Current; if (r != null) r.ItemNoise(__instance, position); } }
}
