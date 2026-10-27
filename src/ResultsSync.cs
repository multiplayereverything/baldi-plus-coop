using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        sealed class FloorResults
        {
            public int Generation, Points, Wallet, Multiplier, Sticker;
            public float Time;
        }
        FloorResults lastFloorResults;
        int resultsAcceptedGeneration = -1;
        bool resultsLabStarted,resultsLabBooted;
        ElevatorScreen resultsReplica;
        int resultsReplicaGeneration;
        public bool ResultsTransitionActive { get { return !Hosting && resultsReplica != null && resultsReplicaGeneration == epoch; } }

        static readonly System.Reflection.FieldInfo resultLevelPoints = AccessTools.Field(typeof(CoreGameManager), "thoughtPointsThisLevel");
        static readonly System.Reflection.FieldInfo resultWallet = AccessTools.Field(typeof(CoreGameManager), "thoughtPoints");
        static readonly System.Reflection.FieldInfo resultMultiplier = AccessTools.Field(typeof(ElevatorScreen), "ytpMultiplier");

        public void PublishNativeResults(ElevatorScreen screen, float time, int stickerBonus)
        {
            if (!Active || !Hosting || Applying || !Running) return;
            var core = Singleton<CoreGameManager>.Instance;
            if (core == null) return;
            var result = new FloorResults { Generation = epoch, Points = core.GetPointsThisLevel(0), Wallet = core.GetPoints(0),
                Multiplier = (int)resultMultiplier.GetValue(screen), Time = time, Sticker = stickerBonus };
            lastFloorResults = result;
            SendAll(ResultsPacket(result));
            Log("Results published generation=" + epoch + " earned=" + result.Points + " multiplier=" + result.Multiplier + " sticker=" + result.Sticker + " time=" + result.Time);
        }

        byte[] ResultsPacket(FloorResults result)
        {
            return Wire.Packet(Op.ResultsState, w => { w.Write(result.Generation); w.Write(result.Points); w.Write(result.Wallet);
                w.Write(result.Multiplier); w.Write(result.Sticker); w.Write(result.Time); });
        }

        FloorResults ReadResults(BinaryReader reader)
        {
            var result = new FloorResults { Generation = reader.ReadInt32(), Points = reader.ReadInt32(), Wallet = reader.ReadInt32(),
                Multiplier = reader.ReadInt32(), Sticker = reader.ReadInt32(), Time = Wire.Number(reader) };
            if (result.Points < 0 || result.Wallet < 0 || result.Sticker < 0 || result.Multiplier < 0 || result.Multiplier > 3 || result.Time < 0)
                throw new IOException("Invalid floor results.");
            return result;
        }

        bool AcceptResults(FloorResults result)
        {
            if (result.Generation != epoch || result.Generation <= resultsAcceptedGeneration) return false;
            resultsAcceptedGeneration = result.Generation;
            lastFloorResults = result;
            return true;
        }

        void ApplyResultsFields(FloorResults result, CoreGameManager core, ElevatorScreen screen)
        {
            // Native rewards have already been granted on the host. Assign totals, never grant again.
            ((int[])resultLevelPoints.GetValue(core))[0] = result.Points;
            ((int[])resultWallet.GetValue(core))[0] = result.Wallet;
            resultMultiplier.SetValue(screen, result.Multiplier);
        }

        bool HandleResultsHost(Op op, BinaryReader reader)
        {
            if (op != Op.ResultsState) return false;
            var result = ReadResults(reader);
            if (!AcceptResults(result)) return true;
            var core = Singleton<CoreGameManager>.Instance;
            var manager = Singleton<BaseGameManager>.Instance;
            if (core == null || manager == null) throw new IOException("Results arrived without a loaded floor.");
            ReleaseSpectator();
            manager.StopAllCoroutines();
            if (manager.Ec != null) manager.Ec.ResetEvents();
            core.readyToStart = false; core.disablePause = true;
            Time.timeScale = 0; PropagatedAudioManager.paused = true;
            EnsureClientElevator();
            resultsReplica = clientElevatorOwned;
            resultsReplicaGeneration = result.Generation;
            ApplyResultsFields(result, core, resultsReplica);
            resultsReplica.ShowResults(result.Time, result.Sticker);
            Log("Results received generation=" + result.Generation + " earned=" + result.Points + " wallet=" + result.Wallet);
            return true;
        }

        public bool ResultsButtonAllowed(ElevatorScreen screen)
        { return !Active || Hosting || screen != resultsReplica; }

        bool ReuseResultsElevator()
        {
            if (resultsReplica == null || resultsReplicaGeneration >= epoch) return false;
            // Native host keeps the screen alive while the next map loads behind its results animation.
            clientElevatorStarted = null; clientElevatorOwned = resultsReplica;
            return true;
        }

        public IEnumerator WrapNativeResults(ElevatorScreen screen, IEnumerator original)
        {
            if (!Active || Hosting || screen != resultsReplica) return original;
            return NativeResultsValues(original, lastFloorResults);
        }

        IEnumerator NativeResultsValues(IEnumerator original, FloorResults result)
        {
            while (true)
            {
                var core = Singleton<CoreGameManager>.Instance;
                var points = core == null ? null : (int[])resultLevelPoints.GetValue(core);
                int previous = points == null ? 0 : points[0];
                bool more; object current = null;
                try
                {
                    // Scope completed-floor points to the original UI iterator, even if the next map resets them.
                    if (points != null) points[0] = result.Points;
                    more = original.MoveNext();
                    if (more) current = original.Current;
                }
                finally { if (points != null) points[0] = previous; }
                if (!more) yield break;
                yield return current;
            }
        }

        public void ResetResultsSession()
        { resultsAcceptedGeneration = -1; lastFloorResults = null; resultsReplica = null; }

        void UpdateResultsLab()
        {
            UpdateResultsLiveLab();
            if(Lab.Enabled&&!resultsLabBooted&&TitleVisible&&Lab.HasArg("-bt-results-tour"))
            {
                resultsLabBooted=true;var reserve=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);reserve.Start();portText=((System.Net.IPEndPoint)reserve.LocalEndpoint).Port.ToString();reserve.Stop();Connect(true);AutoStartRun();
            }
            if (!Lab.Enabled || resultsLabStarted || !worldReady || Singleton<CoreGameManager>.Instance == null ||
                !Lab.HasArg("-bt-results-tour")) return;
            resultsLabStarted = true; StartCoroutine(ResultsTour());
        }

        bool resultsLiveStarted, resultsLiveWritten, resultsLiveClearedPoints;
        float resultsLiveAt;
        int resultsLiveWalletBefore;
        void UpdateResultsLiveLab()
        {
            if (!Lab.Enabled || !Active || !Lab.HasArg("-bt-results-live") || resultsLiveWritten) return;
            var core = Singleton<CoreGameManager>.Instance;
            if (Hosting && !resultsLiveStarted)
            {
                var manager = Singleton<BaseGameManager>.Instance as MainGameManager;
                if (epoch != 1 || !worldReady || manager == null || !manager.GameReady || !manager.Ec.Active ||
                    peers.Values.Count(p => p.Accepted) < 2 || peers.Values.Any(p => p.Id != 0 && p.Accepted && !p.Ready)) return;
                resultsLiveStarted = true; resultsLiveAt = Time.realtimeSinceStartup;
                ((int[])resultLevelPoints.GetValue(core))[0] = 100;
                core.SetAttempts(1); resultsLiveWalletBefore = core.GetPoints(0);
                manager.LoadNextLevel();
            }
            if (lastFloorResults == null) return;
            if (!Hosting && !resultsLiveStarted) { resultsLiveStarted = true; resultsLiveAt = Time.realtimeSinceStartup; }
            var result = lastFloorResults;
            if (Hosting)
            {
                if (epoch <= result.Generation && Time.realtimeSinceStartup - resultsLiveAt < 25) return;
                WriteResultsLive(new Dictionary<string, bool> {
                    {"native-host-earned", result.Points == 100}, {"native-host-multiplier", result.Multiplier == 2},
                    {"native-host-reward", result.Wallet == resultsLiveWalletBefore + 100 + result.Sticker},
                    {"host-advanced-floor", epoch > result.Generation}
                }, result);
                return;
            }
            var screen = resultsReplica;
            var big = screen == null ? null : (BigScreen)AccessTools.Field(typeof(ElevatorScreen), "bigScreen").GetValue(screen);
            if (epoch > result.Generation && !resultsLiveClearedPoints)
            {
                // Isolated lab fault injection: emulate a next-floor earned-point reset while results are animating.
                ((int[])resultLevelPoints.GetValue(core))[0] = 0;
                resultsLiveClearedPoints = true;
            }
            string clock = Mathf.Floor(result.Time / 60) + ":" + Mathf.Floor(result.Time % 60).ToString("00");
            bool values = big != null && big.points.text == result.Points.ToString() && big.multiplier.text == result.Multiplier + "X" &&
                big.grade.text == result.Sticker.ToString() && big.total.text == (result.Points * result.Multiplier + result.Sticker).ToString() && big.time.text == clock;
            if (!values && Time.realtimeSinceStartup - resultsLiveAt < 25) return;
            WriteResultsLive(new Dictionary<string, bool> {
                {"received-host-snapshot", result.Points == 100 && result.Multiplier == 2},
                {"native-client-display", values}, {"native-client-wallet", core.GetPoints(0) == result.Wallet},
                {"screen-survived-floor-load", epoch > result.Generation && screen != null && clientElevatorOwned == screen},
                {"next-floor-earned-points-preserved", resultsLiveClearedPoints && core.GetPointsThisLevel(0) == 0}
            }, result);
        }

        void WriteResultsLive(Dictionary<string, bool> checks, FloorResults result)
        {
            resultsLiveWritten = true;
            string path = Path.Combine(Lab.Workspace, "artifacts/tests/results-live-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "{\"role\":\"" + (Hosting ? "host" : "client") + "\",\"epoch\":" + epoch +
                ",\"resultEpoch\":" + result.Generation + ",\"points\":" + result.Points + ",\"wallet\":" + result.Wallet +
                ",\"multiplier\":" + result.Multiplier + ",\"sticker\":" + result.Sticker + ",\"time\":" + result.Time.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"passed\":" + checks.Count(p => p.Value) + ",\"failed\":" + checks.Count(p => !p.Value) + ",\"checks\":{" +
                string.Join(",", checks.Select(p => "\"" + p.Key + "\":" + (p.Value ? "true" : "false")).ToArray()) + "}}");
            Log("Live results " + (Hosting ? "host" : "client") + ": " + checks.Count(p => p.Value) + "/" + checks.Count);
        }

        IEnumerator ResultsTour()
        {
            var checks = new Dictionary<string, bool>();
            var core = Singleton<CoreGameManager>.Instance;
            var previous = lastFloorResults; int accepted = resultsAcceptedGeneration;
            int wallet = core.GetPoints(0), points = core.GetPointsThisLevel(0);
            bool ready = core.readyToStart; float scale = Time.timeScale;
            core.readyToStart = false; Time.timeScale = 0;
            var field = AccessTools.Field(typeof(BaseGameManager), "elevatorScreenPre");
            var manager = Singleton<BaseGameManager>.Instance;
            var prefab = field.GetValue(manager) as ElevatorScreen;
            if (prefab == null) yield break;
            foreach (var old in UnityEngine.Object.FindObjectsOfType<ElevatorScreen>()) UnityEngine.Object.DestroyImmediate(old.gameObject);
            var screen = UnityEngine.Object.Instantiate(prefab); screen.Initialize();
            var sample = new FloorResults { Generation = epoch, Points = 123, Wallet = 999, Multiplier = 2, Sticker = 17, Time = 83 };
            resultsAcceptedGeneration = -1;
            checks["accept-current"] = AcceptResults(sample);
            ApplyResultsFields(sample, core, screen);
            checks["native-points"] = core.GetPointsThisLevel(0) == 123;
            checks["native-wallet"] = core.GetPoints(0) == 999;
            checks["native-multiplier"] = (int)resultMultiplier.GetValue(screen) == 2;
            checks["reject-duplicate"] = !AcceptResults(new FloorResults { Generation = epoch, Wallet = 99999 });
            checks["reject-stale"] = !AcceptResults(new FloorResults { Generation = epoch - 1 });
            checks["reject-future"] = !AcceptResults(new FloorResults { Generation = epoch + 1 });
            checks["no-duplicate-reward"] = core.GetPoints(0) == 999 && lastFloorResults == sample;
            var bytes = ResultsPacket(sample);
            using (var stream = new MemoryStream(bytes)) using (var reader = new BinaryReader(stream))
            {
                reader.ReadByte(); var decoded = ReadResults(reader);
                checks["wire-roundtrip"] = decoded.Time == 83 && decoded.Sticker == 17 && decoded.Points == 123 && decoded.Wallet == 999;
            }
            var big = (BigScreen)AccessTools.Field(typeof(ElevatorScreen), "bigScreen").GetValue(screen);
            // Execute the original Results iterator, retaining the game's text/audio/animation code.
            var routine = (IEnumerator)AccessTools.Method(typeof(ElevatorScreen), "Results").Invoke(screen, new object[] { sample.Time, sample.Sticker });
            ((int[])resultLevelPoints.GetValue(core))[0] = 0;
            screen.StartCoroutine(NativeResultsValues(routine, sample));
            yield return new WaitForSecondsRealtime(7);
            checks["native-results-values"] = big.points.text == "123" && big.multiplier.text == "2X" && big.grade.text == "17" && big.total.text == "263" && big.time.text == "1:23";
            checks["next-floor-points-preserved"] = core.GetPointsThisLevel(0) == 0;
            ((int[])resultLevelPoints.GetValue(core))[0] = points; ((int[])resultWallet.GetValue(core))[0] = wallet;
            core.readyToStart = ready; Time.timeScale = scale;
            lastFloorResults = previous; resultsAcceptedGeneration = accepted;
            UnityEngine.Object.Destroy(screen.gameObject);
            string path = Path.Combine(Lab.Workspace, "artifacts/tests/results-sync-results.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "{\"passed\":" + checks.Count(p => p.Value) + ",\"failed\":" + checks.Count(p => !p.Value) + ",\"checks\":{" +
                string.Join(",", checks.Select(p => "\"" + p.Key + "\":" + (p.Value ? "true" : "false")).ToArray()) + "}}");
            Log("Results tour: " + checks.Count(p => p.Value) + "/" + checks.Count);
        }
    }

    [HarmonyPatch(typeof(ElevatorScreen), "ShowResults")]
    static class MultiplayerResultsCapturePatch
    {
        static void Prefix(ElevatorScreen __instance, float time, int stickerBonus)
        { var runtime = ModHooks.Current; if (runtime != null) runtime.PublishNativeResults(__instance, time, stickerBonus); }
    }

    [HarmonyPatch(typeof(ElevatorScreen), "ButtonPressed")]
    static class MultiplayerResultsButtonPatch
    {
        static bool Prefix(ElevatorScreen __instance)
        { var runtime = ModHooks.Current; return runtime == null || runtime.ResultsButtonAllowed(__instance); }
    }

    [HarmonyPatch(typeof(ElevatorScreen), "Results")]
    static class MultiplayerResultsValuesPatch
    {
        static void Postfix(ElevatorScreen __instance, ref IEnumerator __result)
        { var runtime = ModHooks.Current; if (runtime != null) __result = runtime.WrapNativeResults(__instance, __result); }
    }
}
