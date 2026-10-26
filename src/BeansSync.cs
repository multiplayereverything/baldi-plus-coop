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
    // Remote counterparts of Beans_ChewPrep and Beans_Chewing. The native states hold a PlayerManager,
    // which a remote player does not have on the host, so these track a peer id and use host-side sight.
    sealed class BeansRemotePrep : Beans_StateBase
    {
        public readonly int Peer;
        public BeansRemotePrep(Beans beans, int peer) : base(beans) { Peer = peer; }
        public override void Enter() { base.Enter(); ChangeNavigationState(new NavigationState_TargetPosition(npc, 63, npc.Navigator.NextPoint)); }
        public override void DestinationEmpty()
        {
            var runtime = ModHooks.Current;
            if (runtime != null && runtime.BeansRemoteInSight(beans, Peer))
            {
                base.DestinationEmpty();
                beans.Chew();
                npc.behaviorStateMachine.ChangeState(new BeansRemoteChewing(beans, Peer, beans.ChewTime));
            }
            else npc.behaviorStateMachine.ChangeState(new Beans_NewWandering(beans));
        }
    }

    sealed class BeansRemoteChewing : Beans_StateBase
    {
        public readonly int Peer;
        float chewTime; bool blown;
        public BeansRemoteChewing(Beans beans, int peer, float chewTime) : base(beans) { Peer = peer; this.chewTime = chewTime; }
        public override void Enter() { base.Enter(); ChangeNavigationState(new NavigationState_DoNothing(npc, 0)); }
        public override void Update()
        {
            base.Update();
            chewTime -= Time.deltaTime * npc.TimeScale;
            if (chewTime <= 1.25f && !blown) { beans.Blow(); blown = true; }
            if (chewTime > 0) return;
            var runtime = ModHooks.Current;
            if (runtime != null && runtime.BeansRemoteSpit(beans, Peer)) npc.behaviorStateMachine.ChangeState(new Beans_Watch(beans));
            else npc.behaviorStateMachine.ChangeState(new Beans_NewWandering(beans));
        }
    }

    // Beans sync. Host: Beans sights, chews for and spits at remote players; the host resolves the gum
    // hit against the remote pose and commands the owner. Owner: a private Gum instance applies the
    // native player branch (slowdown, overlay, gauge, scissors, expiry). Everyone: gum flight replicated
    // in the World section and shown on the replica Beans' own (inert) Gum.
    // Ops: BeansToClient = 70 (1 hit, 2 end), BeansToHost = 71 (1 owner effect ended).
    public sealed partial class TogetherRuntime
    {
        sealed class RemoteGum { public Beans Beans; public Gum Gum; public int Owner, Token; public float Remaining; }
        sealed class GumEffect { public Beans Beans; public Gum Gum; public int Token; public bool HostEnded; }
        sealed class GumVisual { public bool Flying; public Vector3 Forward; public float At; }
        sealed class GumWorldEntry { public int Index; public byte Flags; public Vector3 Position, Forward; }

        const float GumHitRadius = 2.5f;
        static readonly FieldInfo GumFlyingField = AccessTools.Field(typeof(Gum), "flying");
        static readonly FieldInfo GumLeftBeansField = AccessTools.Field(typeof(Gum), "leftBeans");
        static readonly FieldInfo GumActModField = AccessTools.Field(typeof(Gum), "actMod");
        static readonly FieldInfo GumStuckField = AccessTools.Field(typeof(Gum), "stuckEntity");
        static readonly FieldInfo GumSetTimeField = AccessTools.Field(typeof(Gum), "setTime");
        static readonly FieldInfo GumGaugeField = AccessTools.Field(typeof(Gum), "gauge");

        readonly List<RemoteGum> remoteGums = new List<RemoteGum>();
        readonly List<GumEffect> gumEffects = new List<GumEffect>();
        readonly HashSet<int> gumTokens = new HashSet<int>();
        readonly Dictionary<Beans, GumVisual> gumVisuals = new Dictionary<Beans, GumVisual>();
        readonly List<GumWorldEntry> lastBeansWorld = new List<GumWorldEntry>();
        int nextGumToken, beansRemoteTargets, beansRemoteSpits, beansGumHits, beansGumApplied, beansGumEnds, beansOwnerEnds;
        float nextBeansSight;
        bool beansLabStarted;

        static bool GumFlying(Gum gum) { return (bool)GumFlyingField.GetValue(gum); }
        static Entity GumEntity(Gum gum) { return NpcField<Entity>(gum, "entity"); }

        void ResetBeans()
        {
            foreach (var record in remoteGums)
                if (record.Gum != null && !record.Gum.Hidden && !GumFlying(record.Gum)) record.Gum.Hide();
            remoteGums.Clear();
            foreach (var effect in gumEffects) DestroyGumEffect(effect);
            gumEffects.Clear(); gumTokens.Clear();
            foreach (var pair in gumVisuals)
                if (pair.Key != null && pair.Key.gum != null) ShowGumVisual(pair.Key.gum, pair.Value, 0, pair.Key.gum.transform.position, pair.Key.gum.transform.forward);
            gumVisuals.Clear(); lastBeansWorld.Clear();
            nextBeansSight = 0;
        }

        // Releases everything the native player branch acquired, without relying on its coroutine.
        void DestroyGumEffect(GumEffect effect)
        {
            var gum = effect.Gum;
            if (gum == null) return;
            var activity = GumActModField.GetValue(gum) as ActivityModifier;
            if (activity != null) { activity.moveMods.Remove(NpcField<MovementModifier>(gum, "playerMod")); activity.moveMods.Remove(NpcField<MovementModifier>(gum, "moveMod")); }
            var gauge = GumGaugeField.GetValue(gum) as HudGauge;
            if (gauge != null && !gum.Hidden) gauge.Deactivate();
            gum.StopAllCoroutines();
            Gum.playerGum.Remove(gum);
            Object.Destroy(gum.gameObject);
            effect.Gum = null;
        }

        bool ValidGumPeer(Peer p, float now)
        { return p != null && p.Id != self && p.Accepted && p.Ready && p.HasPose && !p.Eliminated && !p.InLocker && now - p.PoseAt < 2; }

        bool BeansSees(Beans beans, Vector3 position)
        {
            if (beans.Blinded || beans.looker == null) return false;
            var delta = position - beans.transform.position;
            if (delta.magnitude > Mathf.Min(beans.looker.distance, beans.ec.MaxRaycast)) return false;
            bool fov = (bool)AccessTools.Field(typeof(Looker), "hasFov").GetValue(beans.looker);
            float angle = (float)AccessTools.Field(typeof(Looker), "fieldOfView").GetValue(beans.looker);
            return (!fov || Vector3.Angle(beans.transform.forward, delta) <= angle) && ClearWorldLine(beans.transform.position, position);
        }

        Peer BeansTarget(Beans beans)
        {
            float now = Time.realtimeSinceStartup;
            return peers.Values.Where(p => ValidGumPeer(p, now) && !p.Tagged && !p.Invisible && BeansSees(beans, p.Position))
                .OrderBy(p => (p.Position - beans.transform.position).sqrMagnitude).ThenBy(p => p.Id).FirstOrDefault();
        }

        public bool BeansRemoteInSight(Beans beans, int peer)
        {
            Peer target;
            return Hosting && worldReady && peers.TryGetValue(peer, out target) && ValidGumPeer(target, Time.realtimeSinceStartup)
                && !target.Tagged && !target.Invisible && BeansSees(beans, target.Position);
        }

        // Beans_Wandering.PlayerSighted for a remote player: native Beans.ChewPrep without the PlayerManager.
        void ConsiderRemoteBeans(Beans beans)
        {
            if (beans == null || !(beans.behaviorStateMachine.CurrentState is Beans_Wandering)) return;
            Peer target = BeansTarget(beans);
            if (target == null) return;
            beans.Navigator.FindPath(beans.transform.position, target.Position);
            beans.behaviorStateMachine.ChangeState(new BeansRemotePrep(beans, target.Id));
            var audio = NpcField<AudioManager>(beans, "audMan"); var sounds = NpcField<SoundObject[]>(beans, "audTargetSounds");
            audio.FlushQueue(true);
            if (sounds.Length > 0) audio.QueueAudio(sounds[UnityEngine.Random.Range(0, sounds.Length)]);
            beansRemoteTargets++;
        }

        // Native Beans.Spit with the remote pose as the aim point.
        public bool BeansRemoteSpit(Beans beans, int peer)
        {
            Peer target;
            if (!Hosting || beans == null || beans.gum == null || !peers.TryGetValue(peer, out target) || !ValidGumPeer(target, Time.realtimeSinceStartup)) return false;
            foreach (var old in remoteGums.Where(g => g.Gum == beans.gum).ToArray()) EndRemoteGum(old, false);
            var gum = beans.gum;
            gum.transform.rotation = Directions.DirFromVector3(target.Position - beans.transform.position, 45f).ToRotation();
            gum.transform.position = beans.transform.position + gum.transform.forward * 4f;
            gum.Reset(beans.Entity);
            NpcField<Animator>(beans, "animator").Play("Beans_Spit", -1, 0f);
            var audio = NpcField<AudioManager>(beans, "audMan"); var sounds = NpcField<SoundObject[]>(beans, "audSpitSounds");
            audio.PlaySingle(NpcField<SoundObject>(beans, "audSpit"));
            if (sounds.Length > 0) audio.QueueAudio(sounds[UnityEngine.Random.Range(0, sounds.Length)]);
            beansRemoteSpits++;
            return true;
        }

        // Host-side stand-in for the Gum trigger against a remote player's collider.
        void CheckRemoteGumHits()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < npcs.Length; i++)
            {
                var beans = npcs[i] as Beans;
                if (beans == null || beans.gum == null || beans.gum.Hidden || !GumFlying(beans.gum)) continue;
                Vector3 at = beans.gum.transform.position;
                Peer hit = peers.Values.Where(p => ValidGumPeer(p, now))
                    .Select(p => new { Peer = p, Distance = new Vector2(p.Position.x - at.x, p.Position.z - at.z).magnitude })
                    .Where(p => p.Distance <= GumHitRadius).OrderBy(p => p.Distance).ThenBy(p => p.Peer.Id).Select(p => p.Peer).FirstOrDefault();
                if (hit != null) HitRemoteWithGum(beans, i, hit);
            }
        }

        void HitRemoteWithGum(Beans beans, int index, Peer peer)
        {
            var gum = beans.gum;
            // Native player branch without the local-player parts: the owner applies those.
            GumFlyingField.SetValue(gum, false); GumActModField.SetValue(gum, null); GumStuckField.SetValue(gum, null);
            NpcField<GameObject>(gum, "flyingSprite").SetActive(false); NpcField<GameObject>(gum, "groundedSprite").SetActive(false);
            var audio = NpcField<AudioManager>(gum, "audMan"); audio.FlushQueue(true);
            var record = new RemoteGum { Beans = beans, Gum = gum, Owner = peer.Id, Token = ++nextGumToken, Remaining = (float)GumSetTimeField.GetValue(gum) };
            remoteGums.Add(record);
            GumEntity(gum).Teleport(peer.Position);
            beans.HitPlayer(); beans.GumHit(gum, false);
            audio.PlaySingle(NpcField<SoundObject>(gum, "audSplat"));
            Network.Send(peer.Id, BeansHitPacket(record.Token, index, (int)beans.Character, record.Remaining, epoch));
            beansGumHits++;
        }

        void EndRemoteGum(RemoteGum record, bool hide)
        {
            remoteGums.Remove(record);
            if (hide && record.Gum != null && !record.Gum.Hidden && !GumFlying(record.Gum)) record.Gum.Hide();
            if (peers.ContainsKey(record.Owner)) Network.Send(record.Owner, BeansEndPacket(record.Token, epoch));
            beansGumEnds++;
        }

        static byte[] BeansHitPacket(int token, int index, int character, float duration, int generation)
        { return Wire.Packet(Op.BeansToClient, w => { w.Write(generation); w.Write((byte)1); w.Write(token); w.Write(index); w.Write(character); w.Write(duration); }); }
        static byte[] BeansEndPacket(int token, int generation)
        { return Wire.Packet(Op.BeansToClient, w => { w.Write(generation); w.Write((byte)2); w.Write(token); }); }
        static byte[] BeansEndedPacket(int token, int generation)
        { return Wire.Packet(Op.BeansToHost, w => { w.Write(generation); w.Write((byte)1); w.Write(token); }); }

        void UpdateBeans()
        {
            if (!Active || !Running || !worldReady) return;
            float now = Time.realtimeSinceStartup;
            if (!Hosting)
            {
                foreach (var effect in gumEffects.ToArray())
                {
                    if (effect.Gum == null) { gumEffects.Remove(effect); continue; }
                    if (LocalEliminated && !effect.Gum.Hidden) effect.Gum.Cut();
                    if (!effect.Gum.Hidden) continue;
                    if (!effect.HostEnded && readyAcknowledged) Network.Send(0, BeansEndedPacket(effect.Token, epoch));
                    DestroyGumEffect(effect); gumEffects.Remove(effect);
                }
                foreach (var pair in gumVisuals)
                {
                    var gum = pair.Key == null ? null : pair.Key.gum;
                    if (gum == null || !pair.Value.Flying || now - pair.Value.At > .35f) continue;
                    gum.transform.position += pair.Value.Forward * NpcField<float>(gum, "speed") * gum.ec.EnvironmentTimeScale * Time.deltaTime;
                }
                return;
            }
            foreach (var record in remoteGums.ToArray())
            {
                Peer owner;
                bool ownerGone = !peers.TryGetValue(record.Owner, out owner) || owner.Eliminated || !owner.Ready;
                if (record.Gum == null || record.Beans == null || record.Gum.Hidden || GumFlying(record.Gum)) { EndRemoteGum(record, false); continue; }
                record.Remaining -= Time.deltaTime * record.Gum.ec.EnvironmentTimeScale;
                // The owner reports native expiry or scissors; the host deadline is only a fallback.
                if (ownerGone || record.Remaining <= -3) { EndRemoteGum(record, true); continue; }
                GumEntity(record.Gum).Teleport(owner.Position);
            }
            CheckRemoteGumHits();
            if (now < nextBeansSight) return;
            nextBeansSight = now + .1f;
            foreach (NPC npc in npcs) { var beans = npc as Beans; if (beans != null) ConsiderRemoteBeans(beans); }
        }

        bool HandleBeansClient(int id, Peer sender, Op op, BinaryReader r)
        {
            if (op != Op.BeansToHost) return false;
            int generation = r.ReadInt32(); byte sub = r.ReadByte();
            if (sub != 1) throw new IOException("Invalid Beans report.");
            int token = r.ReadInt32();
            if (token < 1) throw new IOException("Invalid Beans token.");
            if (generation != epoch || !worldReady || sender == null || !sender.Ready) return true;
            var record = remoteGums.FirstOrDefault(g => g.Token == token && g.Owner == id);
            if (record == null) return true;
            remoteGums.Remove(record);
            if (record.Gum != null && !record.Gum.Hidden && !GumFlying(record.Gum)) record.Gum.Hide();
            beansOwnerEnds++;
            return true;
        }

        bool HandleBeansHost(Op op, BinaryReader r)
        {
            if (op != Op.BeansToClient) return false;
            int generation = r.ReadInt32(); byte sub = r.ReadByte();
            if (sub == 1)
            {
                int token = r.ReadInt32(), index = r.ReadInt32(), character = r.ReadInt32(); float duration = Wire.Number(r);
                if (token < 1 || duration < .1f || duration > 60) throw new IOException("Invalid Beans hit.");
                // Hosting is only reachable from the lab tour, which drives this client handler directly.
                if (generation != epoch || !worldReady || !(readyAcknowledged || Hosting) || index < 0 || index >= npcs.Length) return true;
                var beans = npcs[index] as Beans;
                if (beans == null || (int)beans.Character != character) return true;
                if (gumTokens.Count >= 4096) gumTokens.Clear();
                if (!gumTokens.Add(token)) return true;
                if (LocalEliminated) { if (!Hosting) Network.Send(0, BeansEndedPacket(token, epoch)); return true; }
                ApplyGumEffect(beans, token, duration);
                return true;
            }
            if (sub == 2)
            {
                int token = r.ReadInt32();
                if (token < 1) throw new IOException("Invalid Beans token.");
                if (generation != epoch) return true;
                foreach (var effect in gumEffects.Where(e => e.Token == token))
                { effect.HostEnded = true; if (effect.Gum != null && !effect.Gum.Hidden) effect.Gum.Cut(); }
                return true;
            }
            throw new IOException("Invalid Beans command.");
        }

        static Collider PlayerGumCollider(PlayerManager player)
        {
            return new[] { player.plm.Entity.Trigger }.Concat(player.GetComponents<Collider>())
                .FirstOrDefault(c => c != null && c.isTrigger && c.CompareTag("Player") && c.GetComponent<PlayerManager>() == player);
        }

        // A private Gum takes the native player branch of Gum.EntityTriggerEnter on the local player.
        void ApplyGumEffect(Beans beans, int token, float duration)
        {
            var player = Singleton<CoreGameManager>.Instance.GetPlayer(0);
            var prefab = NpcField<Gum>(beans, "gumPre");
            Collider trigger = player == null ? null : PlayerGumCollider(player);
            if (prefab == null || trigger == null) { Log("Beans gum: local player trigger unavailable."); return; }
            var gum = Object.Instantiate(prefab);
            gum.name = "Beans gum (remote hit)";
            gum.Initialize(beans.ec, beans);
            GumSetTimeField.SetValue(gum, duration);
            gum.transform.position = player.transform.position;
            var effect = new GumEffect { Beans = beans, Gum = gum, Token = token };
            Applying = true;
            try
            {
                gum.Reset(beans.Entity);
                GumLeftBeansField.SetValue(gum, true);
                gum.EntityTriggerEnter(player.plm.Entity, trigger, true);
            }
            finally { Applying = false; }
            if (!Gum.playerGum.Contains(gum)) { Log("Beans gum: native player branch rejected the hit."); DestroyGumEffect(effect); return; }
            gumEffects.Add(effect);
            beansGumApplied++;
        }

        void WriteBeansWorld(BinaryWriter w)
        {
            var entries = new List<int>();
            for (int i = 0; i < npcs.Length && entries.Count < 32; i++) { var beans = npcs[i] as Beans; if (beans != null && beans.gum != null) entries.Add(i); }
            w.Write((byte)entries.Count);
            foreach (int i in entries)
            {
                var gum = ((Beans)npcs[i]).gum;
                byte flags = 0;
                if (!gum.Hidden) flags |= 1;
                if (GumFlying(gum)) flags |= 2;
                if (NpcField<GameObject>(gum, "flyingSprite").activeSelf) flags |= 4;
                if (NpcField<GameObject>(gum, "groundedSprite").activeSelf) flags |= 8;
                w.Write(i); w.Write(flags); WriteVector(w, gum.transform.position); WriteVector(w, gum.transform.forward);
            }
        }

        void ReadBeansWorld(BinaryReader r, bool apply)
        {
            int count = r.ReadByte();
            if (count > 32) throw new IOException("Beans gum limit exceeded.");
            if (apply) lastBeansWorld.Clear();
            for (int i = 0; i < count; i++)
            {
                int index = r.ReadInt32(); byte flags = r.ReadByte(); Vector3 position = ReadVector(r), forward = ReadVector(r);
                if (flags > 15 || forward.sqrMagnitude > 4 || position.sqrMagnitude > 1e10f) throw new IOException("Invalid Beans gum state.");
                if (!apply || index < 0 || index >= npcs.Length) continue;
                var beans = npcs[index] as Beans;
                if (beans == null || beans.gum == null) continue;
                lastBeansWorld.Add(new GumWorldEntry { Index = index, Flags = flags, Position = position, Forward = forward });
                if (Hosting) continue;
                GumVisual visual;
                if (!gumVisuals.TryGetValue(beans, out visual)) gumVisuals[beans] = visual = new GumVisual();
                ShowGumVisual(beans.gum, visual, flags, position, forward);
            }
        }

        // Presentation only: the replica Gum stays Hidden with its entity inactive, so it never triggers.
        void ShowGumVisual(Gum gum, GumVisual visual, byte flags, Vector3 position, Vector3 forward)
        {
            bool shown = (flags & 1) != 0, flying = (flags & 2) != 0;
            gum.transform.position = position;
            if (forward.sqrMagnitude > .01f) gum.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            NpcField<GameObject>(gum, "flyingSprite").SetActive(shown && (flags & 4) != 0);
            NpcField<GameObject>(gum, "groundedSprite").SetActive(shown && (flags & 8) != 0);
            var audio = NpcField<AudioManager>(gum, "audMan");
            if (flying && !visual.Flying) { audio.QueueAudio(NpcField<SoundObject>(gum, "audWhoosh")); audio.SetLoop(true); }
            else if (!flying && visual.Flying) { audio.FlushQueue(true); if (shown) audio.PlaySingle(NpcField<SoundObject>(gum, "audSplat")); }
            visual.Flying = flying; visual.Forward = forward; visual.At = Time.realtimeSinceStartup;
        }

        string BeansDiagnostic()
        {
            return ",\"beansRemoteTargets\":" + beansRemoteTargets + ",\"beansRemoteSpits\":" + beansRemoteSpits + ",\"beansGumHits\":" + beansGumHits
                + ",\"beansGumApplied\":" + beansGumApplied + ",\"beansGumEnds\":" + beansGumEnds + ",\"beansOwnerEnds\":" + beansOwnerEnds
                + ",\"beansGumActive\":" + (remoteGums.Count + gumEffects.Count);
        }

        void UpdateBeansLab()
        {
            if (beansLabStarted || !Lab.Enabled || !TitleVisible || !Lab.HasArg("-bt-beans-tour")) return;
            beansLabStarted = true; StartCoroutine(TourBeans());
        }

        static BinaryReader LabPacketReader(byte[] packet) { var reader = new BinaryReader(new MemoryStream(packet)); reader.ReadByte(); return reader; }

        IEnumerator TourBeans()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            portText = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(); listener.Stop();
            Connect(true); AutoStartRun();
            while (!worldReady) yield return null;
            yield return new WaitForSecondsRealtime(1);
            var ec = Singleton<BaseGameManager>.Instance.Ec; var player = Singleton<CoreGameManager>.Instance.GetPlayer(0);
            var beans = ec.Npcs.OfType<Beans>().FirstOrDefault(b => b != null);
            if (beans == null)
            {
                var prefab = Resources.FindObjectsOfTypeAll<Beans>().First(n => !n.gameObject.scene.IsValid());
                beans = (Beans)ec.SpawnNPC(prefab, ec.CellFromPosition(player.transform.position).position);
            }
            npcs = ec.Npcs.ToArray();
            int index = Array.IndexOf(npcs, beans);
            Vector3 origin = beans.transform.position;
            var directions = new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left };
            Vector3 open = directions.FirstOrDefault(d => ClearWorldLine(origin, origin + d * 6));
            Vector3 blocked = directions.FirstOrDefault(d => !ClearWorldLine(origin, origin + d * 15));
            var remote = new Peer { Id = 3, Name = "Beans target", Accepted = true, Ready = true, HasPose = true, Position = origin + open * 5, PoseAt = Time.realtimeSinceStartup };
            peers[3] = remote;

            // Host decision: sight, exclusions and occlusion.
            beans.transform.rotation = Quaternion.LookRotation(open == Vector3.zero ? Vector3.forward : open);
            beans.behaviorStateMachine.ChangeState(new Beans_Wandering(beans));
            remote.Eliminated = true; ConsiderRemoteBeans(beans); bool eliminatedIgnored = beans.behaviorStateMachine.CurrentState is Beans_Wandering;
            remote.Eliminated = false; remote.InLocker = true; ConsiderRemoteBeans(beans); bool lockerIgnored = beans.behaviorStateMachine.CurrentState is Beans_Wandering;
            remote.InLocker = false;
            CheckNativeUi("beans-eliminated-and-locker-not-targeted", eliminatedIgnored && lockerIgnored);
            if (blocked != Vector3.zero)
            {
                remote.Position = origin + blocked * 15; beans.transform.rotation = Quaternion.LookRotation(blocked);
                ConsiderRemoteBeans(beans);
                CheckNativeUi("beans-occluded-remote-not-targeted", beans.behaviorStateMachine.CurrentState is Beans_Wandering);
                remote.Position = origin + open * 5; beans.transform.rotation = Quaternion.LookRotation(open);
            }
            ConsiderRemoteBeans(beans);
            var prep = beans.behaviorStateMachine.CurrentState as BeansRemotePrep;
            CheckNativeUi("beans-host-targets-visible-remote", open != Vector3.zero && prep != null && prep.Peer == 3);
            if (prep != null) prep.DestinationEmpty();
            var chewing = beans.behaviorStateMachine.CurrentState as BeansRemoteChewing;
            bool spat = chewing != null && BeansRemoteSpit(beans, 3);
            if (spat) beans.behaviorStateMachine.ChangeState(new Beans_Watch(beans));
            CheckNativeUi("beans-chews-and-spits-at-remote", spat && !beans.gum.Hidden && GumFlying(beans.gum) && Vector3.Dot(beans.gum.transform.forward, open) > .7f);

            // Host resolves the hit against the remote pose; the host's own player is untouched.
            var hostMod = NpcField<MovementModifier>(beans.gum, "playerMod");
            GumEntity(beans.gum).Teleport(remote.Position);
            CheckRemoteGumHits();
            var record = remoteGums.FirstOrDefault();
            CheckNativeUi("beans-host-gum-hits-remote", record != null && record.Owner == 3 && !GumFlying(beans.gum) && beans.behaviorStateMachine.CurrentState is Beans_Flee
                && !player.plm.am.moveMods.Contains(hostMod) && beansGumHits == 1);

            // Owner applies the native effect once; duplicates and stale epochs are ignored.
            int token = record == null ? 1 : record.Token;
            byte[] hit = BeansHitPacket(token, index, (int)beans.Character, 10, epoch);
            HandleBeansHost(Op.BeansToClient, LabPacketReader(hit));
            var effect = gumEffects.FirstOrDefault();
            MovementModifier effectMod = effect == null ? null : NpcField<MovementModifier>(effect.Gum, "playerMod");
            HudGauge gauge = effect == null ? null : GumGaugeField.GetValue(effect.Gum) as HudGauge;
            CheckNativeUi("beans-owner-native-gum-applied", effect != null && Gum.playerGum.Contains(effect.Gum) && player.plm.am.moveMods.Contains(effectMod)
                && NpcField<Canvas>(effect.Gum, "canvas").gameObject.activeSelf && gauge != null && gauge.Active);
            HandleBeansHost(Op.BeansToClient, LabPacketReader(hit));
            CheckNativeUi("beans-duplicate-hit-applied-once", gumEffects.Count == 1 && beansGumApplied == 1 && player.plm.am.moveMods.Count(m => m == effectMod) == 1);
            HandleBeansHost(Op.BeansToClient, LabPacketReader(BeansHitPacket(token + 500, index, (int)beans.Character, 10, epoch + 1)));
            CheckNativeUi("beans-stale-epoch-hit-ignored", gumEffects.Count == 1 && beansGumApplied == 1);

            // Host accepts the owner's end once.
            byte[] ended = BeansEndedPacket(token, epoch);
            HandleBeansClient(3, remote, Op.BeansToHost, LabPacketReader(ended));
            bool accepted = remoteGums.Count == 0 && beans.gum.Hidden && beansOwnerEnds == 1;
            HandleBeansClient(3, remote, Op.BeansToHost, LabPacketReader(ended));
            CheckNativeUi("beans-host-accepts-owner-end-once", accepted && beansOwnerEnds == 1);

            // Host end command: the native timer releases the slowdown, overlay, gauge and scissors list.
            var effectGum = effect == null ? null : effect.Gum;
            HandleBeansHost(Op.BeansToClient, LabPacketReader(BeansEndPacket(token, epoch)));
            float until = Time.realtimeSinceStartup + 2;
            while (effectGum != null && !effectGum.Hidden && Time.realtimeSinceStartup < until) yield return null;
            bool expired = effectGum != null && effectGum.Hidden;
            if (effect != null) { DestroyGumEffect(effect); gumEffects.Remove(effect); }
            CheckNativeUi("beans-owner-end-cleanup-balanced", expired && !player.plm.am.moveMods.Contains(effectMod) && !Gum.playerGum.Contains(effectGum) && gauge != null && !gauge.Active);

            // World section round trip and the client's inert visual copy.
            remote.PoseAt = Time.realtimeSinceStartup;
            BeansRemoteSpit(beans, 3);
            byte[] section;
            using (var memory = new MemoryStream()) { using (var writer = new BinaryWriter(memory)) WriteBeansWorld(writer); section = memory.ToArray(); }
            bool consumed;
            using (var memory = new MemoryStream(section)) using (var reader = new BinaryReader(memory)) { ReadBeansWorld(reader, true); consumed = memory.Position == memory.Length; }
            var entry = lastBeansWorld.FirstOrDefault(e => e.Index == index);
            var visualGum = Object.Instantiate(NpcField<Gum>(beans, "gumPre")); visualGum.Initialize(ec, beans);
            var visual = new GumVisual();
            ShowGumVisual(visualGum, visual, entry == null ? (byte)0 : entry.Flags, entry == null ? Vector3.zero : entry.Position, entry == null ? Vector3.forward : entry.Forward);
            bool shownFlying = NpcField<GameObject>(visualGum, "flyingSprite").activeSelf && visualGum.Hidden && Vector3.Distance(visualGum.transform.position, beans.gum.transform.position) < .01f;
            ShowGumVisual(visualGum, visual, 0, visualGum.transform.position, visualGum.transform.forward);
            bool hiddenAgain = !NpcField<GameObject>(visualGum, "flyingSprite").activeSelf && !NpcField<GameObject>(visualGum, "groundedSprite").activeSelf;
            Object.Destroy(visualGum.gameObject);
            CheckNativeUi("beans-world-section-round-trip", consumed && entry != null && entry.Flags == 7 && shownFlying && hiddenAgain);

            // Disconnect releases the host record.
            GumEntity(beans.gum).Teleport(remote.Position);
            CheckRemoteGumHits();
            bool held = remoteGums.Count == 1;
            peers.Remove(3);
            UpdateBeans();
            CheckNativeUi("beans-disconnect-releases-remote-gum", held && remoteGums.Count == 0 && beans.gum.Hidden);
            peers[3] = remote; remote.PoseAt = Time.realtimeSinceStartup;

            // Reset releases an active owner effect.
            HandleBeansHost(Op.BeansToClient, LabPacketReader(BeansHitPacket(token + 1000, index, (int)beans.Character, 10, epoch)));
            var resetEffect = gumEffects.FirstOrDefault(); var resetGum = resetEffect == null ? null : resetEffect.Gum;
            var resetMod = resetGum == null ? null : NpcField<MovementModifier>(resetGum, "playerMod");
            var resetGauge = resetGum == null ? null : GumGaugeField.GetValue(resetGum) as HudGauge;
            bool resetApplied = resetGum != null && player.plm.am.moveMods.Contains(resetMod);
            ResetBeans();
            CheckNativeUi("beans-reset-cleanup-balanced", resetApplied && gumEffects.Count == 0 && remoteGums.Count == 0 && !player.plm.am.moveMods.Contains(resetMod)
                && !Gum.playerGum.Contains(resetGum) && resetGauge != null && !resetGauge.Active);

            // The host's own player keeps the untouched native gum.
            int hitsBefore = beansGumHits;
            Collider trigger = PlayerGumCollider(player);
            beans.gum.Reset(beans.Entity); GumLeftBeansField.SetValue(beans.gum, true);
            if (trigger != null) beans.gum.EntityTriggerEnter(player.plm.Entity, trigger, true);
            bool nativeApplied = Gum.playerGum.Contains(beans.gum) && player.plm.am.moveMods.Contains(hostMod) && remoteGums.Count == 0 && beansGumHits == hitsBefore;
            beans.gum.Cut();
            until = Time.realtimeSinceStartup + 2;
            while (!beans.gum.Hidden && Time.realtimeSinceStartup < until) yield return null;
            CheckNativeUi("beans-host-local-player-native-gum", trigger != null && nativeApplied && !player.plm.am.moveMods.Contains(hostMod) && !Gum.playerGum.Contains(beans.gum));

            peers.Remove(3);
            WriteLabResults("beans-results.json", "Beans");
        }
    }

    // Replica Beans on clients never run AI; the owner's effect gum must not drive their state machine.
    [HarmonyPatch(typeof(Beans), "GumHit")]
    static class BeansReplicaGumHitPatch { static bool Prefix() { return !ModHooks.ClientWorld; } }
}
