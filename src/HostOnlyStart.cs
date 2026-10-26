using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    // Only the host starts a run. The title menu's run buttons (Hide & Seek new/continue, challenges, field trip,
    // endless, tutorial) are wired in the inspector to hide the menu, call GameLoader.Initialize/AssignElevatorScreen/
    // LoadLevel (or LoadSavedGame) and switch the ElevatorScreen on. A client's GameLoader calls are refused, so the
    // rest of the chain used to leave an elevator stuck over the title menu. Now the whole press is refused on a
    // connected client and it is told to wait for the host, whose run it joins automatically.
    public sealed partial class TogetherRuntime
    {
        static readonly HashSet<string> RunStartCalls = new HashSet<string> { "Initialize", "LoadLevel", "LoadSavedGame", "AssignElevatorScreen" };
        static readonly Dictionary<int, bool> runStartButtons = new Dictionary<int, bool>();
        int clientStartBlocks;

        public static bool StartsRun(MenuButton button)
        {
            if (button == null || button.OnPress == null) return false;
            bool starts; int id = button.GetInstanceID();
            if (runStartButtons.TryGetValue(id, out starts)) return starts;
            for (int i = 0; i < button.OnPress.GetPersistentEventCount() && !starts; i++)
                starts = button.OnPress.GetPersistentTarget(i) is GameLoader && RunStartCalls.Contains(button.OnPress.GetPersistentMethodName(i));
            runStartButtons[id] = starts;
            return starts;
        }

        public bool BlockClientRunStart(MenuButton button)
        {
            if (!Active || Hosting || Applying || !StartsRun(button)) return false;
            clientStartBlocks++;
            Peer host; string name = peers.TryGetValue(0, out host) && !string.IsNullOrEmpty(host.Name) ? host.Name : "the host";
            Notify("Only the host can start a run. Wait for " + name + " to start; you will join automatically.");
            return true;
        }

        // Lab (-bt-compat-fail-tour): the host's level check fails while loading; it must land cleanly on the title
        // menu with no elevator screen left behind (it used to throw in ElevatorScreen.Update every frame).
        bool compatLabStarted;
        void UpdateCompatFailLab()
        {
            if (compatLabStarted || !Lab.Enabled || !TitleVisible || !Lab.HasArg("-bt-compat-fail-tour")) return;
            compatLabStarted = true; StartCoroutine(TourCompatFail());
        }
        System.Collections.IEnumerator TourCompatFail()
        {
            int errors = 0;
            Application.LogCallback count = (text, trace, type) => { if (type == LogType.Exception && trace != null && trace.Contains("ElevatorScreen")) errors++; };
            Application.logMessageReceived += count;
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            portText = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(); listener.Stop();
            Connect(true);
            // Start the way a player does: the real Hide & Seek New button, which opens the native elevator screen.
            var newRun = Resources.FindObjectsOfTypeAll<StandardMenuButton>().FirstOrDefault(btn => btn != null && btn.gameObject.scene.IsValid() && btn.name == "MainNew" && btn.transform.parent != null && btn.transform.parent.name == "HideSeekMenu");
            if (newRun != null) newRun.Press(); else AutoStartRun();
            float until = Time.realtimeSinceStartup + 60;
            while ((!labCompatFailed || Active) && Time.realtimeSinceStartup < until) yield return null;
            CheckNativeUi("compat-failure-disconnects", labCompatFailed && !Active);
            until = Time.realtimeSinceStartup + 20;
            while (!TitleVisible && Time.realtimeSinceStartup < until) yield return null;
            yield return new WaitForSecondsRealtime(3);
            Application.logMessageReceived -= count;
            CheckNativeUi("compat-failure-returns-to-title", TitleVisible && Singleton<CoreGameManager>.Instance == null);
            CheckNativeUi("compat-failure-leaves-no-elevator", Object.FindObjectsOfType<ElevatorScreen>().Length == 0 && errors == 0);
            WriteLabResults("compat-fail-results.json", "CompatFail");
        }

        string HostOnlyStartDiagnostic() { return ",\"clientStartBlocks\":" + clientStartBlocks; }

        // Lab: write every title-menu button and its inspector-wired OnPress calls (-bt-menu-dump).
        bool buttonWiringDumped;
        void UpdateMenuDumpLab()
        {
            if (buttonWiringDumped || !Lab.Enabled || !TitleVisible || !Lab.HasArg("-bt-menu-dump")) return;
            buttonWiringDumped = true;
            var lines = new List<string>();
            foreach (var button in Resources.FindObjectsOfTypeAll<MenuButton>().Where(b => b != null && b.gameObject.scene.IsValid()))
            {
                lines.Add(PathOf(button.transform) + " [" + button.GetType().Name + "] active=" + button.gameObject.activeInHierarchy);
                foreach (var e in new[] { "OnPress", "OnRelease" })
                {
                    var field = AccessTools.Field(button.GetType(), e); var ev = field == null ? null : field.GetValue(button) as UnityEventBase;
                    if (ev == null) continue;
                    for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                    {
                        var target = ev.GetPersistentTarget(i);
                        lines.Add("   " + e + " -> " + (target == null ? "null" : target.GetType().Name + "(" + (target is Component ? PathOf(((Component)target).transform) : target.name) + ")") + "." + ev.GetPersistentMethodName(i));
                    }
                }
            }
            // Character posters: text boxes and base images (layout reference; written to private/, never shipped).
            foreach (var npc in Resources.FindObjectsOfTypeAll<NPC>().Where(n => n != null && !n.gameObject.scene.IsValid() && n.Poster != null))
            {
                var poster = npc.Poster;
                lines.Add("POSTER " + npc.name + " poster=" + poster.name + " base=" + (poster.baseTexture == null ? "none" : poster.baseTexture.name + " " + poster.baseTexture.width + "x" + poster.baseTexture.height + " readable=" + poster.baseTexture.isReadable) + " multi=" + poster.multiPosterArray.Length);
                foreach (var t in poster.textData)
                    lines.Add("   text key=" + t.textKey + " => " + Singleton<LocalizationManager>.Instance.GetLocalizedText(t.textKey).Replace('\n', ' ') + " pos=" + t.position.x + "," + t.position.z + " size=" + t.size.x + "," + t.size.z
                        + " font=" + (t.font == null ? "null" : t.font.name) + " fontSize=" + t.fontSize + " colour=" + t.color + " style=" + t.style + " align=" + t.alignment);
                if (poster.baseTexture != null && poster.baseTexture.isReadable && npc.name == "FirstPrize")
                    File.WriteAllBytes(Path.Combine(Root, "MultiplayerMod/private/poster-reference-firstprize.png"), poster.baseTexture.EncodeToPNG());
            }
            File.WriteAllLines(Path.Combine(Root, "MultiplayerMod/artifacts/tests/menu-dump.txt"), lines.ToArray());
            Log("LAB menu dump: " + lines.Count + " lines");
        }

        static string PathOf(Transform t) { string path = t.name; while (t.parent != null) { t = t.parent; path = t.name + "/" + path; } return path; }
    }

    [HarmonyPatch(typeof(StandardMenuButton), "Press")]
    static class HostOnlyStandardStartPatch
    { static bool Prefix(StandardMenuButton __instance) { var r = ModHooks.Current; return r == null || !r.BlockClientRunStart(__instance); } }

    [HarmonyPatch(typeof(MenuButton), "Press")]
    static class HostOnlyStartPatch
    { static bool Prefix(MenuButton __instance) { var r = ModHooks.Current; return r == null || !r.BlockClientRunStart(__instance); } }
}
