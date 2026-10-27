using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    // Lab (-bt-lab -bt-focus-tour): the host starts a real run, then an outside script (scripts/focus-test.ps1)
    // takes focus away, minimizes and restores the window like Alt+Tab. Everything the game and mod do is logged.
    public sealed partial class TogetherRuntime
    {
        internal static readonly bool FocusTour = Lab.Enabled && Lab.HasArg("-bt-focus-tour");
        internal static readonly List<string> focusEvents = new List<string>();
        internal static void FocusEvent(string text) { if (FocusTour) focusEvents.Add(Time.realtimeSinceStartup.ToString("0.00") + " f" + Time.frameCount + " " + text); }
        bool focusLabStarted;

        void UpdateFocusLab()
        {
            if (focusLabStarted || !FocusTour || !TitleVisible) return;
            focusLabStarted = true; StartCoroutine(TourFocus());
        }

        string FocusState()
        {
            var core = Singleton<CoreGameManager>.Instance; var global = Singleton<GlobalCam>.Instance;
            var scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(i => SceneManager.GetSceneAt(i).name).ToArray();
            return "scenes=" + string.Join("|", scenes) + " active=" + Active + " running=" + Running + " worldReady=" + worldReady + " panel=" + show
                + " core=" + (core != null) + " paused=" + (core != null && core.Paused) + " manager=" + (Singleton<BaseGameManager>.Instance != null)
                + " title=" + TitleVisible + " transition=" + (global != null && global.TransitionActive) + " timeScale=" + Time.timeScale
                + " page=" + nativePage + " choices=" + string.Join(",", nativeChoices.Where(c => c != null).Select(c => c.name).ToArray()) + " screen=" + Screen.width + "x" + Screen.height + " mode=" + Screen.fullScreenMode + " focused=" + Application.isFocused;
        }

        IEnumerator TourFocus()
        {
            Application.focusChanged += f => FocusEvent("focusChanged " + f);
            SceneManager.sceneLoaded += (s, m) => FocusEvent("sceneLoaded " + s.name + " " + m);
            SceneManager.sceneUnloaded += s => FocusEvent("sceneUnloaded " + s.name);
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            portText = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(); listener.Stop();
            if (Lab.HasArg("-bt-focus-lobby"))
            {
                // The host waiting for friends in the multiplayer lobby, opened through the native menu flow.
                SetPanel(true); yield return new WaitForSecondsRealtime(1);
                PressNativeUi("host"); string port = portText; PressNativeUi("connect");
                float wait = Time.realtimeSinceStartup + 10;
                while (!Hosting && Time.realtimeSinceStartup < wait) yield return null;
                FocusEvent("lobby port=" + port + " hosting=" + Hosting + " page=" + nativePage);
            }
            else
            {
                Connect(true); AutoStartRun();
                while (!worldReady) yield return null;
            }
            yield return new WaitForSecondsRealtime(3);
            FocusEvent("READY " + FocusState());
            yield return FocusShot("focus-before.png");
            string ready = Path.Combine(Lab.Workspace, "private/focus-ready.txt"), done = Path.Combine(Lab.Workspace, "private/focus-done.txt");
            File.WriteAllText(ready, System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
            float until = Time.realtimeSinceStartup + 90, nextState = 0;
            while (!File.Exists(done) && Time.realtimeSinceStartup < until)
            {
                if (Time.realtimeSinceStartup > nextState) { nextState = Time.realtimeSinceStartup + 0.5f; FocusEvent("state " + FocusState()); }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(2);
            FocusEvent("AFTER " + FocusState());
            yield return FocusShot("focus-after.png");
            File.WriteAllLines(Path.Combine(Lab.Workspace, "artifacts/tests/focus-events.txt"), focusEvents.ToArray());
            File.WriteAllText(Path.Combine(Lab.Workspace, "artifacts/tests/focus-results.json"), "{\"done\":true,\"events\":" + focusEvents.Count + "}");
        }

        IEnumerator FocusShot(string name)
        {
            yield return new WaitForEndOfFrame();
            var shot = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(Lab.Workspace, "artifacts/tests/" + name), shot.EncodeToPNG());
            Object.Destroy(shot);
        }
    }

    // Lab-only probes on everything that could send the game back to a menu.
    [HarmonyPatch]
    static class FocusLabProbe
    {
        static bool Prepare() { return TogetherRuntime.FocusTour; }
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CoreGameManager), "Pause");
            yield return AccessTools.Method(typeof(CoreGameManager), "ReturnToMenu");
            yield return AccessTools.Method(typeof(CoreGameManager), "Quit");
            yield return AccessTools.Method(typeof(GlobalCam), "Transition");
            yield return AccessTools.Method(typeof(GlobalCam), "FadeIn");
            yield return AccessTools.Method(typeof(GlobalCam), "UpdateResolution");
            yield return AccessTools.Method(typeof(GlobalCam), "ChangeType");
            yield return AccessTools.Method(typeof(AdditiveSceneManager), "LoadScene", new[] { typeof(string) });
            yield return AccessTools.Method(typeof(TogetherRuntime), "SetPanel");
            yield return AccessTools.Method(typeof(TogetherRuntime), "Disconnect", new[] { typeof(string), typeof(bool) });
        }
        static void Prefix(System.Reflection.MethodBase __originalMethod, object[] __args)
        { TogetherRuntime.FocusEvent("call " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + "(" + string.Join(",", __args.Select(a => a == null ? "null" : a.ToString()).ToArray()) + ") from " + new System.Diagnostics.StackTrace(2, false).GetFrame(0)); }
    }
}
