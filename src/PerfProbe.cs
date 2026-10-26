using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

namespace BaldiTogether
{
    // Lab-only performance probe (-bt-lab -bt-profile). Times the mod's per-frame methods with Harmony
    // prefix/postfix pairs and writes 10-second windows of frame time, method cost, GC and heap use.
    // -bt-soak drives a long two-player session: both players roam real floors with every NPC active,
    // ping, use BSODA, then advance floor 1 -> Pitstop -> floor 2 so memory can be compared across loads.
    static class PerfProbe
    {
        public static readonly bool Enabled = Lab.Enabled && Lab.HasArg("-bt-profile");
        public static readonly bool Soak = Lab.Enabled && Lab.HasArg("-bt-soak");

        sealed class Stat { public long Calls, Ticks, MaxTicks; }
        static readonly Dictionary<string, Stat> stats = new Dictionary<string, Stat>();
        static readonly List<float> frameTimes = new List<float>(4096);
        static readonly List<string> windows = new List<string>();
        static float windowStart = -1;
        static int gcStart, frames;
        static long allocatedStart;
        static readonly double tickToUs = 1000000.0 / Stopwatch.Frequency;

        public static void Record(string name, long ticks)
        {
            Stat s;
            if (!stats.TryGetValue(name, out s)) { s = new Stat(); stats.Add(name, s); }
            s.Calls++; s.Ticks += ticks; if (ticks > s.MaxTicks) s.MaxTicks = ticks;
        }

        // Called once per runtime Update (after it has been timed).
        public static void Frame(TogetherRuntime runtime)
        {
            float now = Time.realtimeSinceStartup;
            if (windowStart < 0) { BeginWindow(now); return; }
            frames++; frameTimes.Add(Time.unscaledDeltaTime * 1000f);
            if (now - windowStart < 10) return;
            float seconds = now - windowStart;
            frameTimes.Sort();
            Func<float, float> pct = p => frameTimes.Count == 0 ? 0 : frameTimes[Math.Min(frameTimes.Count - 1, (int)(frameTimes.Count * p))];
            var json = new StringBuilder();
            json.Append("{\"t\":").Append(now.ToString("0.0"))
                .Append(",\"scene\":\"").Append(runtime.LabSceneName).Append("\",\"epoch\":").Append(runtime.LabEpoch)
                .Append(",\"hosting\":").Append(runtime.Hosting ? "true" : "false")
                .Append(",\"worldReady\":").Append(runtime.WorldReady ? "true" : "false")
                .Append(",\"npcs\":").Append(runtime.LabNpcCount).Append(",\"peers\":").Append(runtime.LabPeerCount)
                .Append(",\"fps\":").Append((frames / seconds).ToString("0.0"))
                .Append(",\"frameMsP50\":").Append(pct(.5f).ToString("0.00"))
                .Append(",\"frameMsP95\":").Append(pct(.95f).ToString("0.00"))
                .Append(",\"frameMsMax\":").Append(pct(1f).ToString("0.00"))
                .Append(",\"gc0PerMin\":").Append(((GC.CollectionCount(0) - gcStart) * 60f / seconds).ToString("0.0"))
                .Append(",\"monoHeapMB\":").Append((GC.GetTotalMemory(false) / 1048576.0).ToString("0.0"))
                .Append(",\"monoUsedMB\":").Append((Profiler.GetMonoUsedSizeLong() / 1048576.0).ToString("0.0"))
                .Append(",\"unityAllocatedMB\":").Append((Profiler.GetTotalAllocatedMemoryLong() / 1048576.0).ToString("0.0"))
                .Append(",\"modMsPerFrame\":");
            // Mod cost per frame = the runtime Update plus top-level work outside it (OnGUI, NPC callbacks, sprite LateUpdate).
            double modUs = 0;
            foreach (var pair in stats) if (pair.Key == "TogetherRuntime.Update" || pair.Key.StartsWith("Top:", StringComparison.Ordinal)) modUs += pair.Value.Ticks * tickToUs;
            json.Append(frames == 0 ? "0" : (modUs / 1000.0 / frames).ToString("0.000")).Append(",\"methods\":{");
            bool first = true;
            foreach (var pair in stats.OrderByDescending(p => p.Value.Ticks).Take(40))
            {
                if (!first) json.Append(','); first = false;
                json.Append('"').Append(pair.Key).Append("\":{\"callsPerSec\":").Append((pair.Value.Calls / seconds).ToString("0.0"))
                    .Append(",\"avgUs\":").Append((pair.Value.Ticks * tickToUs / Math.Max(1, pair.Value.Calls)).ToString("0.0"))
                    .Append(",\"maxUs\":").Append((pair.Value.MaxTicks * tickToUs).ToString("0"))
                    .Append(",\"msPerSec\":").Append((pair.Value.Ticks * tickToUs / 1000.0 / seconds).ToString("0.000")).Append('}');
            }
            json.Append("}}");
            windows.Add(json.ToString());
            Write(runtime);
            BeginWindow(now);
        }

        static void BeginWindow(float now)
        {
            windowStart = now; frames = 0; frameTimes.Clear(); stats.Clear();
            gcStart = GC.CollectionCount(0); allocatedStart = Profiler.GetTotalAllocatedMemoryLong();
        }

        static void Write(TogetherRuntime runtime)
        {
            try
            {
                string folder = Path.Combine(TogetherRuntime.Root, "MultiplayerMod/artifacts/tests");
                Directory.CreateDirectory(folder);
                string role = runtime.Hosting ? "host" : "client";
                File.WriteAllText(Path.Combine(folder, "perf-" + role + ".json"),
                    "{\"pid\":" + Process.GetCurrentProcess().Id + ",\"soak\":" + (Soak ? "true" : "false") + ",\"windows\":[\n" + string.Join(",\n", windows.ToArray()) + "\n]}");
            }
            catch (Exception ex) { TogetherRuntime.Log("Perf probe write failed: " + ex.Message); }
        }
    }

    // Times every per-frame TogetherRuntime method plus the remote-player sprite update.
    [HarmonyPatch]
    static class PerfProbeMethodPatch
    {
        static readonly string[] Extra = { "CheckRemoteCatches", "CheckTeamWipe", "TrySendWorld", "SendPose", "HandleEvent", "ReadWorld", "OnGUI", "UpdateNearestBaldi", "UpdateRemoteNpcTarget" };
        static bool Prepare() { return PerfProbe.Enabled; }
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var method in typeof(TogetherRuntime).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.IsGenericMethodDefinition || method.GetMethodBody() == null) continue;
                if ((method.Name.StartsWith("Update", StringComparison.Ordinal) && method.GetParameters().Length <= 1) || Extra.Contains(method.Name)) yield return method;
            }
            var late = AccessTools.Method(typeof(RemotePlayerSprite), "LateUpdate");
            if (late != null) yield return late;
        }
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) { __state = Stopwatch.GetTimestamp(); }
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state, MethodBase __originalMethod, object __instance)
        {
            long ticks = Stopwatch.GetTimestamp() - __state;
            string name = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
            PerfProbe.Record(name, ticks);
            // Top-level per-frame work outside TogetherRuntime.Update: OnGUI and the NPC/sprite callbacks.
            if (__originalMethod.Name == "OnGUI" || __originalMethod.Name == "UpdateNearestBaldi" || __originalMethod.Name == "UpdateRemoteNpcTarget" || __originalMethod.Name == "LateUpdate")
                PerfProbe.Record("Top:" + name, ticks);
            if (__originalMethod.Name == "Update" && __originalMethod.DeclaringType == typeof(TogetherRuntime)) PerfProbe.Frame((TogetherRuntime)__instance);
        }
    }

    public sealed partial class TogetherRuntime
    {
        public string LabSceneName { get { return sceneName ?? ""; } }
        public int LabEpoch { get { return epoch; } }
        public int LabNpcCount { get { return npcs == null ? 0 : npcs.Length; } }
        public int LabPeerCount { get { return peers.Count; } }

        float soakNextMove, soakNextPing, soakNextItem, soakStartedAt = -1, soakStepAt = -1;
        int soakFloorStep;
        readonly System.Random soakRandom = new System.Random(1234);

        // -bt-soak: both players wander real floors with all NPCs active; the host advances floors on a timer.
        void UpdateSoakLab()
        {
            if (!PerfProbe.Soak || !Active) return;
            float clock = Time.realtimeSinceStartup;
            // Leave the Pitstop on a timer even if its environment never reports active.
            if (Hosting && soakFloorStep == 1 && soakStepAt > 0 && clock - soakStepAt > 30)
            {
                var pit = Singleton<BaseGameManager>.Instance;
                if (pit != null) { soakFloorStep = 2; Log("SOAK leaving the pitstop"); pit.LoadNextLevel(); }
            }
            if (!worldReady || (!Hosting && !readyAcknowledged)) return;
            var core = Singleton<CoreGameManager>.Instance; var manager = Singleton<BaseGameManager>.Instance;
            if (core == null || manager == null || manager.Ec == null || !manager.Ec.Active) return;
            float now = Time.realtimeSinceStartup;
            if (soakStartedAt < 0) { soakStartedAt = now; Log("SOAK started on " + sceneName); }
            var player = core.GetPlayer(0);
            if (player != null && now >= soakNextMove)
            {
                soakNextMove = now + 3;
                var cells = manager.Ec.AllTilesNoGarbage(false, false);
                if (cells.Count > 0)
                {
                    var cell = cells[soakRandom.Next(cells.Count)];
                    player.Teleport(cell.CenterWorldPosition);
                    player.transform.rotation = Quaternion.Euler(0, soakRandom.Next(4) * 90, 0);
                }
            }
            if (now >= soakNextPing) { soakNextPing = now + 15; PingTeam(); }
            if (player != null && player.itm != null && now >= soakNextItem)
            {
                soakNextItem = now + 20;
                var soda = Resources.FindObjectsOfTypeAll<ItemObject>().FirstOrDefault(i => i != null && i.itemType == Items.Bsoda);
                if (soda != null && !player.itm.InventoryFull()) { player.itm.AddItem(soda); player.itm.UseItem(); }
            }
            // Host: floor 1 for 150 s, then the Pitstop for 30 s, then the next floor.
            if (Hosting && soakFloorStep == 0 && now - soakStartedAt > 150) { soakFloorStep = 1; soakStepAt = now; Log("SOAK advancing to the next level"); manager.LoadNextLevel(); soakStartedAt = -1; }
        }
    }
}
