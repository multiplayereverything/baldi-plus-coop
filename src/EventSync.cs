using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BaldiTogether
{
    // Random events are the host's. Each copy builds the same seeded event list, but the start order, gaps and
    // durations come from UnityEngine.Random at run time, so a client starting its own timers would run its own
    // schedule. Hide & Seek only starts them on the host already (countdown release); BaseGameManager.Initialize
    // and PitstopGameManager start them directly when a scene spawns NPCs on init (challenges), so block it here.
    // The host sends each event's announcement, begin and end by its index in the shared list; clients mirror the
    // events whose effects are local (fog, mystery room, the ruler, the time-out), with their own end timer disabled so only the
    // host ends them. Other event types are host-only for now; their NPC and door effects already replicate.
    public partial class TogetherRuntime
    {
        const byte EventEnded = 0, EventBegun = 1, EventAnnounced = 2;
        static readonly System.Reflection.FieldInfo EventsField = AccessTools.Field(typeof(EnvironmentController), "events"), CurrentEventsField = AccessTools.Field(typeof(EnvironmentController), "currentEvents"),
            CurrentTypesField = AccessTools.Field(typeof(EnvironmentController), "currentEventTypes"), EcAudioField = AccessTools.Field(typeof(EnvironmentController), "audMan"),
            EcJingleField = AccessTools.Field(typeof(EnvironmentController), "audEventNotification"), RemainingField = AccessTools.Field(typeof(RandomEvent), "remainingTime"),
            TimeOutAnger = AccessTools.Field(typeof(TimeOut), "baldiAngerAmount");
        static List<RandomEvent> EventList(EnvironmentController ec) { return (List<RandomEvent>)EventsField.GetValue(ec); }
        static List<RandomEvent> CurrentEventList(EnvironmentController ec) { return (List<RandomEvent>)CurrentEventsField.GetValue(ec); }
        static List<RandomEventType> CurrentEventTypeList(EnvironmentController ec) { return (List<RandomEventType>)CurrentTypesField.GetValue(ec); }

        int clientEventTimersBlocked, eventsSent, eventsMirrored, eventsMirrorEnded, eventsAnnounced, eventsHostOnly, eventMismatches;
        bool clientEventProbeDone, applyingEvent;
        string lastMirroredEvent = "";

        internal void NoteBlockedClientEvents() { clientEventTimersBlocked++; }
        int seededTripPicks;
        internal void NoteSeededTripPick() { seededTripPicks++; }

        static bool MirroredOnClients(RandomEventType type)
        { return type == RandomEventType.Fog || type == RandomEventType.MysteryRoom || type == RandomEventType.Snap || type == RandomEventType.TimeOut; }

        void ResetEvents() { lastMirroredEvent = ""; eventLabStep = 0; labButtonPressed = false; labButtonDoorsOpenBefore = -1; }

        // Host: one of its events was announced, began or ended.
        internal void SendEventState(RandomEvent randomEvent, byte state)
        {
            if (!Hosting || !Active || !Running || !worldReady || randomEvent == null) return;
            if (Lab.Enabled && Lab.HasArg("-bt-lab-no-event-sync")) return; // mutation twin
            var ec = Singleton<BaseGameManager>.Instance == null ? null : Singleton<BaseGameManager>.Instance.Ec;
            if (ec == null || randomEvent.Ec() != ec) return;
            int index = EventList(ec).IndexOf(randomEvent);
            if (index < 0) return;
            eventsSent++;
            SendAll(Wire.Packet(Op.EventState, w => { w.Write(epoch); w.Write(index); w.Write((int)randomEvent.Type); w.Write(state); }));
        }

        internal void SendEventAnnouncement(SoundObject intro)
        {
            var ec = Singleton<BaseGameManager>.Instance == null ? null : Singleton<BaseGameManager>.Instance.Ec;
            if (!Hosting || ec == null || intro == null) return;
            var next = EventList(ec).FirstOrDefault(e => e != null && !e.Active && e.EventIntro == intro);
            if (next != null) SendEventState(next, EventAnnounced);
        }

        bool HandleEventHost(Op op, BinaryReader r)
        {
            if (op != Op.EventState) return false;
            int generation = r.ReadInt32(), index = r.ReadInt32(), type = r.ReadInt32(); byte state = r.ReadByte();
            if (state > EventAnnounced) throw new IOException("Invalid event state.");
            var manager = Singleton<BaseGameManager>.Instance; var core = Singleton<CoreGameManager>.Instance;
            if (generation != epoch || Hosting || !worldReady || manager == null || manager.Ec == null || core == null) return true;
            var list = EventList(manager.Ec);
            if (index < 0 || index >= list.Count || list[index] == null || (int)list[index].Type != type) { eventMismatches++; return true; }
            var randomEvent = list[index];
            if (state == EventAnnounced)
            {
                eventsAnnounced++;
                ((AudioManager)EcAudioField.GetValue(manager.Ec)).PlaySingle(randomEvent.EventJingleOverride != null ? randomEvent.EventJingleOverride : (SoundObject)EcJingleField.GetValue(manager.Ec));
                core.GetHud(0).BaldiTv.AnnounceEvent(randomEvent.EventIntro);
                return true;
            }
            if (!MirroredOnClients(randomEvent.Type)) { if (state == EventBegun) eventsHostOnly++; return true; }
            applyingEvent = true;
            try
            {
                if (state == EventBegun && !randomEvent.Active)
                {
                    // TimeOut: each player gets the after-hours rule break, dimming lights and music; Baldi's anger is the host's.
                    if (randomEvent is TimeOut) TimeOutAnger.SetValue(randomEvent, 0f);
                    randomEvent.Begin();
                    RemainingField.SetValue(randomEvent, float.MaxValue); // the host's End ends it
                    CurrentEventList(manager.Ec).Add(randomEvent); CurrentEventTypeList(manager.Ec).Add(randomEvent.Type);
                    eventsMirrored++; lastMirroredEvent = randomEvent.Type.ToString();
                }
                else if (state == EventEnded && randomEvent.Active) { randomEvent.End(); eventsMirrorEnded++; }
            }
            finally { applyingEvent = false; }
            return true;
        }

        // Lab (-bt-test-actions client): ask the client world to start its event timers, as a challenge scene would.
        void ProbeClientEventTimers(EnvironmentController ec)
        {
            if (clientEventProbeDone || ec == null) return;
            clientEventProbeDone = true;
            ec.StartEventTimers();
            Log("LAB: client asked its world to start event timers; started=" + ec.EventsStarted + " blocked=" + clientEventTimersBlocked);
        }

        // Lab (-bt-event-sync, both sides): add the same fog event to each copy's list at world ready, then the host
        // runs it for 6 s. The client must mirror its begin and its end.
        int eventLabStep, labButtonDoorsOpenBefore = -1; float eventLabNext; bool labButtonPressed;
        RandomEvent labFog;
        void UpdateEventLab()
        {
            if (!Lab.Enabled || !Lab.HasArg("-bt-event-sync") || !Running || !worldReady) return;
            var manager = Singleton<BaseGameManager>.Instance; if (manager == null || manager.Ec == null) return;
            var ec = manager.Ec; var list = EventList(ec); float now = Time.realtimeSinceStartup;
            if (eventLabStep == 0)
            {
                labFog = list.FirstOrDefault(e => e is FogEvent);
                if (labFog == null)
                {
                    var prefab = Resources.FindObjectsOfTypeAll<FogEvent>().FirstOrDefault(f => !f.gameObject.scene.IsValid());
                    if (prefab == null) { Log("LAB events: no fog event prefab loaded."); eventLabStep = 9; return; }
                    labFog = UnityEngine.Object.Instantiate(prefab, ec.transform);
                    labFog.Initialize(ec, new System.Random(Seed));
                    list.Add(labFog);
                }
                Log("LAB events: fog is event " + list.IndexOf(labFog) + " of " + list.Count);
                eventLabStep = 1; eventLabNext = now + 8;
            }
            else if (Hosting && eventLabStep == 1 && now > eventLabNext && peers.Values.Any(p => p.Id != self && p.Ready))
            {
                labFog.Begin(); CurrentEventList(ec).Add(labFog); CurrentEventTypeList(ec).Add(labFog.Type);
                Log("LAB events: host began fog."); eventLabStep = 2; eventLabNext = now + 6;
            }
            else if (Hosting && eventLabStep == 2 && now > eventLabNext)
            {
                labFog.End(); Log("LAB events: host ended fog."); eventLabStep = 3;
            }
            else if (!Hosting && eventLabStep == 1 && readyAcknowledged && now > eventLabNext + 12)
            {
                // After the fog: press a lever the host owns (a lockdown door's, when this floor has one).
                var lever = buttons.Values.FirstOrDefault(b => b != null && GameButtonPatch.HostOwned(b));
                labButtonDoorsOpenBefore = extraDoors.Values.Count(d => d is LockdownDoor && d.open);
                if (lever != null)
                {
                    Singleton<CoreGameManager>.Instance.GetPlayer(0).Teleport(lever.transform.position - lever.transform.forward * 3);
                    lever.Clicked(0); labButtonPressed = true;
                    Log("LAB events: client pressed " + Key(lever) + "; lockdown doors open before: " + labButtonDoorsOpenBefore);
                }
                else Log("LAB events: no host-owned button on this floor (" + buttons.Count + " buttons).");
                eventLabStep = 4;
            }
        }

        string EventDiagnostic()
        {
            var manager = Singleton<BaseGameManager>.Instance;
            var ec = manager == null ? null : manager.Ec;
            var list = ec == null ? null : EventList(ec);
            return ",\"eventsStarted\":" + (ec != null && ec.EventsStarted ? "true" : "false")
                + ",\"eventCount\":" + (list == null ? 0 : list.Count) + ",\"eventTypes\":\"" + (list == null ? "" : string.Join("|", list.Where(e => e != null).Select(e => e.Type.ToString()).ToArray())) + "\""
                + ",\"activeEvents\":\"" + (ec == null ? "" : string.Join("|", ec.CurrentEventTypes.Select(t => t.ToString()).ToArray())) + "\""
                + ",\"clientEventTimersBlocked\":" + clientEventTimersBlocked + ",\"clientEventProbe\":" + (clientEventProbeDone ? "true" : "false")
                + ",\"eventsSent\":" + eventsSent + ",\"eventsMirrored\":" + eventsMirrored + ",\"eventsMirrorEnded\":" + eventsMirrorEnded
                + ",\"eventsAnnounced\":" + eventsAnnounced + ",\"eventsHostOnly\":" + eventsHostOnly + ",\"eventMismatches\":" + eventMismatches
                + ",\"buttons\":" + buttons.Count + ",\"acceptedButtons\":" + acceptedButtons + ",\"labButtonPressed\":" + (labButtonPressed ? "true" : "false") + ",\"labButtonDoorsOpenBefore\":" + labButtonDoorsOpenBefore + ",\"lockdownDoors\":" + extraDoors.Values.Count(d => d is LockdownDoor) + ",\"lockdownDoorsOpen\":" + extraDoors.Values.Count(d => d is LockdownDoor && d.open)
                + ",\"seededTripPicks\":" + seededTripPicks + ",\"tripHub\":\"" + (Singleton<CoreGameManager>.Instance != null && Singleton<CoreGameManager>.Instance.currentFieldTrip != null ? Singleton<CoreGameManager>.Instance.currentFieldTrip.name : "") + "\""
                + ",\"lastMirroredEvent\":\"" + lastMirroredEvent + "\",\"eventLabStep\":" + eventLabStep;
        }
    }

    static class EventExtensions
    {
        static readonly System.Reflection.FieldInfo EventEc = AccessTools.Field(typeof(RandomEvent), "ec");
        public static EnvironmentController Ec(this RandomEvent randomEvent) { return (EnvironmentController)EventEc.GetValue(randomEvent); }
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

    [HarmonyPatch(typeof(RandomEvent), "Begin")]
    static class EventBeginPatch { static void Postfix(RandomEvent __instance) { var r = ModHooks.Current; if (r != null) r.SendEventState(__instance, 1); } }

    [HarmonyPatch(typeof(RandomEvent), "End")]
    static class EventEndPatch { static void Postfix(RandomEvent __instance) { var r = ModHooks.Current; if (r != null) r.SendEventState(__instance, 0); } }

    [HarmonyPatch(typeof(BaldiTV), "AnnounceEvent")]
    static class EventAnnouncePatch { static void Postfix(SoundObject sound) { var r = ModHooks.Current; if (r != null) r.SendEventAnnouncement(sound); } }
    // The Pitstop picks its field-trip hub with UnityEngine.Random (PrepareLevelData). With more than one weighted trip,
    // host and clients could build different Pitstops and fail the world match. In co-op the pick is seeded from the
    // run seed and the floor, the same on every copy. Each player's own trip and its rewards stay local, as in the game.
    [HarmonyPatch(typeof(PitstopGameManager), "PrepareLevelData")]
    static class FieldTripPickPatch
    {
        static void Prefix(out UnityEngine.Random.State __state)
        {
            __state = UnityEngine.Random.state;
            var r = ModHooks.Current;
            if (r == null || !r.Active) return;
            var next = Singleton<CoreGameManager>.Instance == null ? null : Singleton<CoreGameManager>.Instance.nextLevel;
            UnityEngine.Random.InitState(r.Seed * 31 + (next == null ? 0 : next.levelNo) * 7919 + 17);
            r.NoteSeededTripPick();
        }
        static void Postfix(UnityEngine.Random.State __state) { var r = ModHooks.Current; if (r != null && r.Active) UnityEngine.Random.state = __state; }
    }
}
