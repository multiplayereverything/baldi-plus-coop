using HarmonyLib;
using UnityEngine;

namespace BaldiTogether
{
    // Random events are the host's. Each copy builds the same seeded event list, but the start order, gaps and
    // durations come from UnityEngine.Random at run time, so a client starting its own timers would run its own
    // schedule. Hide & Seek only starts them on the host already (countdown release); BaseGameManager.Initialize
    // and PitstopGameManager start them directly when a scene spawns NPCs on init (challenges), so block it here.
    public partial class TogetherRuntime
    {
        int clientEventTimersBlocked;
        bool clientEventProbeDone;

        internal void NoteBlockedClientEvents() { clientEventTimersBlocked++; }

        // Lab (-bt-test-actions client): ask the client world to start its event timers, as a challenge scene would.
        void ProbeClientEventTimers(EnvironmentController ec)
        {
            if (clientEventProbeDone || ec == null) return;
            clientEventProbeDone = true;
            ec.StartEventTimers();
            Log("LAB: client asked its world to start event timers; started=" + ec.EventsStarted + " blocked=" + clientEventTimersBlocked);
        }

        string EventDiagnostic()
        {
            var manager = Singleton<BaseGameManager>.Instance;
            var ec = manager == null ? null : manager.Ec;
            return ",\"eventsStarted\":" + (ec != null && ec.EventsStarted ? "true" : "false")
                + ",\"clientEventTimersBlocked\":" + clientEventTimersBlocked + ",\"clientEventProbe\":" + (clientEventProbeDone ? "true" : "false");
        }
    }

    [HarmonyPatch(typeof(EnvironmentController), "StartEventTimers")]
    static class ClientEventTimersPatch
    {
        static bool Prefix()
        {
            if (!ModHooks.ClientWorld || (Lab.Enabled && Lab.HasArg("-bt-lab-client-events"))) return true; // mutation twin
            ModHooks.Current.NoteBlockedClientEvents();
            return false;
        }
    }
}
