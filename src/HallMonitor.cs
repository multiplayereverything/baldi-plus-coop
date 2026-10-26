using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    // The Hall Monitor, a co-op NPC added by Baldi Together (multiplayer floors only).
    // He patrols the halls. A player he sees running gets written up (a mark that lasts MarkSeconds).
    // When two marked players stand together he blows his whistle: for FocusSeconds Baldi hunts that pair
    // (the nearer of the two) instead of whoever is closest, and the whistle is a native noise at the pair.
    // The decisions are host-authoritative and live in HallMonitorSync.cs; this class is the native NPC body:
    // it is built from the game's own Principal prefab (Navigator, Looker, Entity, propagated AudioManager)
    // with the Principal component swapped for this one, and spawned through the level's own npcsToSpawn list.
    public class HallMonitor : NPC
    {
        public const Character Id = (Character)200; // below PlayerFileManager.foundChars' 256 slots
        public const string PrefabName = "BaldiTogether_HallMonitor";
        public const float MarkSeconds = 25f, WhistleRange = 15f, WhistleCooldown = 10f, FocusSeconds = 10f, WriteSeconds = 1.4f, WhistleSeconds = 2.4f, SightSeconds = 0.3f;
        public const int WhistleNoise = 112; // louder than detention (95), quieter than an alarm clock (127)

        [SerializeField] public AudioManager audMan;
        [SerializeField] public Sprite patrolSprite, writingSprite, whistleSprite;
        [SerializeField] public SoundObject audNoRunning, audScribble, audWhistle, audTogether;

        public enum Look { Patrol, Writing, Whistle }
        public Look Pose { get; private set; }
        readonly List<Cell> hallCells = new List<Cell>();

        public override void Initialize()
        {
            base.Initialize();
            navigator.maxSpeed = Mathf.Max(10f, navigator.maxSpeed * 0.8f); // a brisk walk, slower than the Principal
            foreach (var room in ec.rooms)
                if (room != null && room.category == RoomCategory.Hall) hallCells.AddRange(room.AllTilesNoGarbage(false, false));
            behaviorStateMachine.ChangeState(new HallMonitor_Patrol(this));
        }

        public Vector3 NextPatrolTarget()
        {
            var cells = hallCells.Count > 0 ? hallCells : ec.AllTilesNoGarbage(false, false);
            if (cells.Count == 0) return transform.position;
            // Prefer a far hall tile so he walks real rounds instead of shuffling in place.
            for (int i = 0; i < 6; i++)
            {
                var cell = cells[UnityEngine.Random.Range(0, cells.Count)];
                if (i == 5 || (cell.FloorWorldPosition - transform.position).sqrMagnitude > 40f * 40f) return cell.FloorWorldPosition;
            }
            return transform.position;
        }

        public void SetPose(Look pose)
        {
            Pose = pose;
            if (spriteRenderer.Length == 0 || spriteRenderer[0] == null) return;
            spriteRenderer[0].sprite = pose == Look.Writing ? writingSprite : pose == Look.Whistle ? whistleSprite : patrolSprite;
        }

        // "No running in the halls!" and a scribble; he stops to write for a moment.
        public void WriteUp()
        {
            PlayWriteUp();
            behaviorStateMachine.ChangeState(new HallMonitor_Busy(this, Look.Writing, WriteSeconds));
        }

        public void PlayWriteUp()
        {
            SetPose(Look.Writing);
            if (audMan == null) return;
            audMan.FlushQueue(true); audMan.QueueAudio(audNoRunning); audMan.QueueAudio(audScribble);
        }

        // "Hey! You two! I'm telling!" and a whistle blast. The noise for Baldi is made by the runtime at the pair.
        public void BlowWhistle()
        {
            PlayWhistle();
            behaviorStateMachine.ChangeState(new HallMonitor_Busy(this, Look.Whistle, WhistleSeconds));
        }

        public void PlayWhistle()
        {
            SetPose(Look.Whistle);
            if (audMan == null) return;
            audMan.FlushQueue(true); audMan.QueueAudio(audTogether); audMan.QueueAudio(audWhistle);
        }
    }

    // Walks between hall tiles. Doors open through the base NpcState.DoorHit like any student NPC.
    public class HallMonitor_Patrol : NpcState
    {
        readonly HallMonitor monitor;
        public HallMonitor_Patrol(HallMonitor monitor) : base(monitor) { this.monitor = monitor; }
        public override void Enter() { base.Enter(); monitor.SetPose(HallMonitor.Look.Patrol); Next(); }
        public override void DestinationEmpty() { base.DestinationEmpty(); Next(); }
        void Next() { ChangeNavigationState(new NavigationState_TargetPosition(npc, 0, monitor.NextPatrolTarget())); }
    }

    // Stands still while writing or whistling, then resumes the patrol. Timed in NPC time like native NPC timers.
    public class HallMonitor_Busy : NpcState
    {
        readonly HallMonitor monitor; readonly HallMonitor.Look pose; float time;
        public HallMonitor_Busy(HallMonitor monitor, HallMonitor.Look pose, float time) : base(monitor) { this.monitor = monitor; this.pose = pose; this.time = time; }
        public HallMonitor.Look Pose { get { return pose; } }
        public override void Enter() { base.Enter(); monitor.SetPose(pose); ChangeNavigationState(new NavigationState_DoNothing(npc, 0)); }
        public override void Update()
        {
            base.Update();
            time -= Time.deltaTime * npc.TimeScale;
            if (time <= 0) npc.behaviorStateMachine.ChangeState(new HallMonitor_Patrol(monitor));
        }
    }

    // Embedded artwork and sounds. Drawn by scripts/draw-hall-monitor.py; sounds are in assets/README.md.
    static class HallMonitorAssets
    {
        public static Texture2D Patrol, Writing, Whistle, PosterTexture;
        public static SoundObject NoRunning, Scribble, WhistleSound, Together;
        public static PosterObject Poster;
        static bool attempted, loaded;

        public static bool Load()
        {
            if (attempted) return loaded;
            attempted = true;
            try
            {
                Patrol = Texture("patrol"); Writing = Texture("writing"); Whistle = Texture("whistle"); PosterTexture = Texture("poster");
                NoRunning = Sound("hm-norunning", "No running in the halls!", SoundType.Voice);
                Scribble = Sound("hm-scribble", "*Scribble scribble*", SoundType.Effect);
                WhistleSound = Sound("hm-whistle", "*WHISTLE!*", SoundType.Effect);
                Together = Sound("hm-together", "Hey! You two! I'm telling!", SoundType.Voice);
                Poster = ScriptableObject.CreateInstance<PosterObject>();
                Poster.name = "BaldiTogether_HallMonitorPoster"; Poster.baseTexture = PosterTexture; // text boxes are added in UsePosterText
                loaded = true;
            }
            catch (Exception ex) { TogetherRuntime.Log("Hall Monitor assets unavailable: " + ex.Message); }
            return loaded;
        }

        public const string PosterName = "Hall Monitor";
        public const string PosterDescription = "Writes up anyone he sees running in the halls! If two marked students meet, he blows his whistle for Baldi.";

        // Same text boxes, font and size as a native character poster (name plate and description card), with his text.
        // Missing localization keys show as themselves, so the English text is the key.
        public static void UsePosterText(PosterObject native)
        {
            if (Poster == null || native == null || native.textData.Length < 2) return;
            Poster.textData = native.textData.Take(2).Select((t, i) => new PosterTextData
            {
                textKey = i == 0 ? PosterName : PosterDescription, position = t.position, size = t.size, font = t.font,
                fontSize = t.fontSize, color = t.color, style = t.style, alignment = t.alignment
            }).ToArray();
        }

        static Texture2D Texture(string name)
        {
            using (var stream = typeof(HallMonitorAssets).Assembly.GetManifestResourceStream("BaldiTogether.Npc.hall-monitor-" + name + ".png"))
            {
                if (stream == null) throw new IOException("Missing Hall Monitor artwork: " + name);
                var bytes = new MemoryStream(); stream.CopyTo(bytes);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(bytes.ToArray())) throw new IOException("Hall Monitor artwork could not be decoded: " + name);
                texture.name = "BaldiTogether_HallMonitor_" + name;
                texture.filterMode = FilterMode.Point; texture.wrapMode = TextureWrapMode.Clamp;
                return texture;
            }
        }

        static SoundObject Sound(string file, string subtitle, SoundType type)
        {
            var clip = TogetherRuntime.LoadEmbeddedWave("BaldiTogether.Sfx." + file + ".wav", "BaldiTogether_" + file);
            var sound = ScriptableObject.CreateInstance<SoundObject>();
            sound.name = "BaldiTogether_" + file; sound.soundClip = clip; sound.soundType = type;
            // Missing localization keys display as themselves, so the English line is the key.
            sound.soundKey = subtitle; sound.subtitle = true; sound.subDuration = clip.length; sound.color = new Color(0.55f, 0.75f, 1f);
            return sound;
        }

        // Same on-screen height scale as the Principal (a student is a little shorter), same pivot.
        public static Sprite MakeSprite(Texture2D texture, Sprite reference)
        {
            float ppu = 100f; Vector2 pivot = new Vector2(0.5f, 0.5f);
            if (reference != null)
            {
                float worldHeight = reference.rect.height / reference.pixelsPerUnit;
                ppu = texture.height / (worldHeight * 0.9f);
                pivot = new Vector2(reference.pivot.x / reference.rect.width, reference.pivot.y / reference.rect.height);
            }
            var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), pivot, ppu, 0, SpriteMeshType.FullRect);
            sprite.name = texture.name;
            return sprite;
        }
    }

    // Builds the Hall Monitor prefab once from the loaded Principal prefab. It lives under an inactive,
    // persistent holder so it never runs on its own; EnvironmentController.SpawnNPC instantiates it like any prefab.
    static class HallMonitorPrefab
    {
        static HallMonitor prefab;
        static bool failed;
        static readonly string[] NpcFields = { "navigator", "looker", "baseTrigger", "spriteBase", "spriteRenderer" };

        public static HallMonitor Get()
        {
            if (prefab != null || failed) return prefab;
            // The standard Principal prefab, not a challenge variant such as Principal_AllKnowing.
            var candidates = Resources.FindObjectsOfTypeAll<Principal>().Where(p => p != null && !p.gameObject.scene.IsValid()).ToArray();
            var principal = candidates.FirstOrDefault(p => p.name == "Principal") ?? candidates.FirstOrDefault(p => !(bool)AccessTools.Field(typeof(Principal), "allKnowing").GetValue(p)) ?? candidates.FirstOrDefault();
            if (principal == null || !HallMonitorAssets.Load()) return null; // game assets not loaded yet
            try { prefab = Build(principal); }
            catch (Exception ex) { failed = true; TogetherRuntime.Log("Hall Monitor prefab failed: " + ex); }
            return prefab;
        }

        public static bool IsPrefab(NPC npc) { return npc != null && prefab != null && npc == prefab; }

        static HallMonitor Build(Principal principal)
        {
            var holder = new GameObject("BaldiTogether NPC prefabs");
            holder.SetActive(false); Object.DontDestroyOnLoad(holder);
            var copy = Object.Instantiate(principal.gameObject, holder.transform);
            copy.name = HallMonitor.PrefabName;
            var old = copy.GetComponent<Principal>();
            var monitor = copy.AddComponent<HallMonitor>();
            foreach (string name in NpcFields)
            {
                var field = AccessTools.Field(typeof(NPC), name);
                field.SetValue(monitor, field.GetValue(old));
            }
            AccessTools.Field(typeof(NPC), "character").SetValue(monitor, HallMonitor.Id);
            HallMonitorAssets.UsePosterText(old.Poster);
            AccessTools.Field(typeof(NPC), "poster").SetValue(monitor, HallMonitorAssets.Poster);
            AccessTools.Field(typeof(NPC), "ignorePlayerOnSpawn").SetValue(monitor, false);
            monitor.spawnableRooms = new List<RoomCategory> { RoomCategory.Hall };
            monitor.potentialRoomAssets = new WeightedRoomAsset[0];
            monitor.audMan = (AudioManager)AccessTools.Field(typeof(Principal), "audMan").GetValue(old);
            // Every component that pointed at the Principal (Looker.npc, Navigator.npc, ...) now points here.
            int repointed = 0;
            foreach (var component in copy.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component == old || component == monitor) continue;
                for (var type = component.GetType(); type != null && type != typeof(MonoBehaviour) && type != typeof(Component); type = type.BaseType)
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (field.FieldType.IsAssignableFrom(typeof(HallMonitor)) && ReferenceEquals(field.GetValue(component), old)) { field.SetValue(component, monitor); repointed++; }
            }
            var reference = (Sprite)AccessTools.Field(typeof(Principal), "normalSprite").GetValue(old);
            monitor.patrolSprite = HallMonitorAssets.MakeSprite(HallMonitorAssets.Patrol, reference);
            monitor.writingSprite = HallMonitorAssets.MakeSprite(HallMonitorAssets.Writing, reference);
            monitor.whistleSprite = HallMonitorAssets.MakeSprite(HallMonitorAssets.Whistle, reference);
            monitor.audNoRunning = HallMonitorAssets.NoRunning; monitor.audScribble = HallMonitorAssets.Scribble;
            monitor.audWhistle = HallMonitorAssets.WhistleSound; monitor.audTogether = HallMonitorAssets.Together;
            Object.DestroyImmediate(old);
            if (monitor.spriteRenderer.Length > 0 && monitor.spriteRenderer[0] != null) monitor.spriteRenderer[0].sprite = monitor.patrolSprite;
            TogetherRuntime.Log("Hall Monitor prefab built from " + principal.name + ": repointed " + repointed + " references, sprite ppu " + monitor.patrolSprite.pixelsPerUnit.ToString("0.0"));
            return monitor;
        }
    }

    // Adds the Hall Monitor to multiplayer main floors through the level's own NPC list, so the generator picks
    // his hall spawn tile, places his poster in the character-poster rooms and spawns him after the countdown
    // exactly like a native NPC. Like the game's own random NPCs he appears on some floors and not others: a
    // SpawnChance roll from the run seed and the floor, so host and clients (same seed, same mod) always agree.
    [HarmonyPatch(typeof(LevelBuilder), "AddNpcsFromPreviousLevels")]
    static class HallMonitorSpawnPatch
    {
        public const double SpawnChance = 0.5;
        public static bool ShouldAdd(bool multiplayer, SceneObject scene) { return multiplayer && scene != null && scene.baldiPrefab != null; }

        // Deterministic per run and floor; string.GetHashCode is not used because it is not guaranteed stable.
        public static bool Rolls(int seed, SceneObject scene)
        {
            int hash = 17;
            unchecked { foreach (char c in scene.name) hash = hash * 31 + c; hash = hash * 31 + scene.levelNo; hash ^= seed * 486187739; }
            return new System.Random(hash).NextDouble() < SpawnChance;
        }

        // Lab switches keep fixtures deterministic: -bt-hall-monitor (or his tour) forces him in, -bt-no-hall-monitor out.
        public static bool Decide(bool multiplayer, SceneObject scene, int seed)
        {
            if (!ShouldAdd(multiplayer, scene)) return false;
            if (Lab.Enabled && Lab.HasArg("-bt-no-hall-monitor")) return false;
            if (Lab.Enabled && (Lab.HasArg("-bt-hall-monitor") || Lab.HasArg("-bt-hall-monitor-tour"))) return true;
            return Rolls(seed, scene);
        }

        static void Postfix(LevelBuilder __instance)
        {
            var runtime = ModHooks.Current; var core = Singleton<CoreGameManager>.Instance;
            if (core == null || !Decide(runtime != null && runtime.Active, __instance.scene, core.Seed())) return;
            var ec = __instance.Ec;
            if (ec == null || ec.npcsToSpawn == null || ec.npcsToSpawn.Any(n => n is HallMonitor)) return;
            var monitor = HallMonitorPrefab.Get();
            if (monitor != null) { ec.npcsToSpawn.Add(monitor); TogetherRuntime.Log("Hall Monitor joins " + __instance.scene.name + " this run."); }
        }
    }
}
