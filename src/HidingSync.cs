using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        int hidingStep = 10, hidingSequence, hidingReceived;
        float hidingRemaining, nextHidingState, nextHidingRequest;
        Coroutine hidingRoutine;
        int hidingApplied = 10, hidingBeats;
        bool hidingFinished;
        HideableLocker localLocker;
        public void EnteredLocker(HideableLocker locker,int player)
        {if(Active&&player==0)localLocker=locker;}
        sealed class ChaseTarget { public NavigationState_TargetPlayer State; public float Next; public int Peer = -1; public bool HasTarget; }
        readonly Dictionary<Baldi, ChaseTarget> chaseTargets = new Dictionary<Baldi, ChaseTarget>();
        int nearestBaldiPeer = -1, baldiTargetSwitches;
        GUIStyle playerNameStyle, playerNameShadow;

        void ResetHiding()
        {
            if (hidingRoutine != null) StopCoroutine(hidingRoutine);
            hidingRoutine = null; hidingStep = hidingApplied = 10; hidingSequence = hidingReceived = hidingBeats = 0;
            hidingRemaining = 0; hidingFinished = false; nextHidingState = nextHidingRequest = 0;
            chaseTargets.Clear(); nearestBaldiPeer = -1;
            localLocker=null;
            countdownReleased.Clear();
        }

        public bool StartHiding(HappyBaldi happy)
        {
            if (!Active || Applying || !Running) return true;
            if (!Hosting)
            {
                if (worldReady && readyAcknowledged && Time.realtimeSinceStartup > nextHidingRequest)
                {
                    nextHidingRequest = Time.realtimeSinceStartup + 0.5f;
                    Network.Send(0,Wire.Packet(Op.HideRequest,w => w.Write(epoch)));
                }
                return false;
            }
            if (hidingRoutine == null && hidingStep == 10)
            {
                AccessTools.Field(typeof(HappyBaldi),"activated").SetValue(happy,true);
                hidingRoutine = StartCoroutine(HostHiding(happy,epoch));
            }
            return false;
        }

        IEnumerator HostHiding(HappyBaldi happy, int generation)
        {
            yield return null;
            var audio = (AudioManager)AccessTools.Field(typeof(HappyBaldi),"audMan").GetValue(happy);
            while (Active && generation == epoch && happy != null && (!worldReady || !CanBeginPlay() || audio.QueuedAudioIsPlaying)) yield return null;
            for (int count = 9; count >= -1 && Active && generation == epoch && happy != null; count--)
            {
                hidingStep = count; hidingSequence++;
                hidingRemaining = count >= 0 ? Mathf.Max(0.1f,2 + Singleton<StickerManager>.Instance.StickerValue(Sticker.BaldiCountdown)) : 0;
                PlayHidingBeat(happy,count); BroadcastHiding();
                while (hidingRemaining > 0 && Active && generation == epoch && happy != null)
                {
                    hidingRemaining -= Time.deltaTime * happy.Ec.NpcTimeScale;
                    yield return null;
                }
            }
            yield return null;
            while (Active && generation == epoch && happy != null && audio.QueuedAudioIsPlaying) yield return null;
            if (Active && generation == epoch && happy != null)
            {
                while(Active&&generation==epoch&&happy!=null&&!FinishHiding(happy))yield return null;
                if(Active&&generation==epoch){hidingStep = -2; hidingSequence++; hidingFinished = true;BroadcastHiding();}
            }
            hidingRoutine = null;
        }

        void PlayHidingBeat(HappyBaldi happy, int count)
        {
            if (happy == null || hidingApplied == count) return;
            hidingApplied = count; hidingBeats++;
            AccessTools.Field(typeof(HappyBaldi),"activated").SetValue(happy,true);
            var audio = (AudioManager)AccessTools.Field(typeof(HappyBaldi),"audMan").GetValue(happy);
            if (!Hosting) audio.FlushQueue(true);
            if (count >= 0)
            {
                var sounds = (SoundObject[])AccessTools.Field(typeof(HappyBaldi),"audCountdown").GetValue(happy);
                audio.QueueAudio(sounds[count]);
                ((Animator)AccessTools.Field(typeof(HappyBaldi),"animator").GetValue(happy)).Play("BAL_CountNormal",-1,0);
            }
            else if (count == -1) audio.QueueAudio((SoundObject)AccessTools.Field(typeof(HappyBaldi),"audHere").GetValue(happy));
        }

        bool FinishHiding(HappyBaldi happy)
        {
            var manager = Singleton<BaseGameManager>.Instance;
            var core = Singleton<CoreGameManager>.Instance;
            if (happy == null || happy.Ec==null || manager == null || core == null) return false;
            bool spawn = (bool)AccessTools.Field(typeof(HappyBaldi),"spawnNpcsOnFinishCounting").GetValue(happy);
            if (spawn && countdownReleased.Add(happy.Ec))
            {
                Singleton<MusicManager>.Instance.StopMidi(); manager.BeginSpoopMode();
                if (Hosting) { happy.Ec.SpawnNPCs(); happy.Ec.StartEventTimers(); }
                Log("Countdown release generation="+epoch+" NPCs="+happy.Ec.Npcs.Count+" pending="+happy.Ec.npcsLeftToSpawn.Count);
            }
            Baldi baldi = happy.Ec.GetBaldi();
            if (baldi != null)
            {
                if (core.currentMode == Mode.Main) baldi.transform.position = happy.transform.position;
                else if (core.currentMode == Mode.Free) baldi.Despawn();
            }
            Object.Destroy(happy.gameObject);
            return true;
        }

        void BroadcastHiding()
        {
            if (!Hosting || hidingStep == 10) return;
            SendAll(Wire.Packet(Op.HideState,w => { w.Write(epoch); w.Write(hidingSequence); w.Write(hidingStep); w.Write(Mathf.Max(0,hidingRemaining)); }));
            nextHidingState = Time.realtimeSinceStartup + 0.5f;
        }

        HappyBaldi cachedHappy;
        int cachedHappyEpoch = -1;
        void UpdateHiding()
        {
            if (Hosting) { if (hidingStep != 10 && Time.realtimeSinceStartup > nextHidingState) BroadcastHiding(); return; }
            if (!worldReady || hidingStep == 10 || (hidingStep == -2 && hidingFinished)) return;
            // Search once per floor; Unity's == null also catches a destroyed HappyBaldi.
            if (cachedHappy == null || cachedHappyEpoch != epoch) { cachedHappy = Object.FindObjectOfType<HappyBaldi>(); cachedHappyEpoch = epoch; }
            var happy = cachedHappy;
            if (hidingStep == -2)
            {
                // A null HappyBaldi here means world replication already released the floor.
                hidingFinished = happy == null || FinishHiding(happy);
            }
            else PlayHidingBeat(happy,hidingStep);
        }

        bool HandleHidingClient(int id, Peer sender, Op op, BinaryReader r)
        {
            if (op != Op.HideRequest) return false;
            int generation = r.ReadInt32(); var happy = Object.FindObjectOfType<HappyBaldi>();
            if (generation == epoch && worldReady && sender.Ready && sender.HasPose && !sender.Eliminated && happy != null && Vector3.Distance(sender.Position,happy.transform.position) < 40) StartHiding(happy);
            return true;
        }

        bool HandleHidingHost(Op op, BinaryReader r)
        {
            if (op != Op.HideState) return false;
            int generation = r.ReadInt32(), serial = r.ReadInt32(), step = r.ReadInt32(); float remaining = Wire.Number(r);
            if (step < -2 || step > 9 || serial < 1 || remaining < 0 || remaining > 60) throw new IOException("Invalid hiding countdown state.");
            if (generation == epoch && serial >= hidingReceived)
            { hidingReceived = serial; hidingStep = step; hidingRemaining = remaining; if (worldReady) UpdateHiding(); }
            return true;
        }

        public void UpdateNearestBaldi(Baldi baldi)
        {
            if (!Hosting || !worldReady || baldi == null || !(baldi.behaviorStateMachine.CurrentState is Baldi_Chase)) return;
            ChaseTarget chase;
            if (!chaseTargets.TryGetValue(baldi,out chase)) { chase = new ChaseTarget(); chaseTargets.Add(baldi,chase); }
            float now = Time.realtimeSinceStartup;
            if (now < chase.Next) return;
            chase.Next = now + 0.2f;
            Peer closest = NearestBaldiPeer(baldi, now);
            chase.HasTarget = closest != null;
            if (closest == null) return;
            if (chase.Peer != closest.Id) { chase.Peer = closest.Id; baldiTargetSwitches++; }
            nearestBaldiPeer = closest.Id;
            if (chase.State == null || baldi.behaviorStateMachine.CurrentNavigationState != chase.State)
            {
                chase.State = new NavigationState_TargetPlayer(baldi,63,closest.Position,true);
                baldi.behaviorStateMachine.ChangeNavigationState(chase.State);
            }
            else chase.State.UpdatePosition(closest.Position);
        }

        // The nearest living, visible, unhidden player. A Hall Monitor whistle narrows it to the whistled pair for a while.
        Peer NearestBaldiPeer(Baldi baldi, float now)
        {
            var candidates = peers.Values.Where(p => p.Accepted && p.HasPose && !p.Eliminated && !p.InLocker && !(p.Id != self && p.Invisible) && (p.Id == self || p.Ready) && now-p.PoseAt < 2).OrderBy(p => p.Id).ToList();
            if (hallFocusLeft > 0 && candidates.Any(p => HallWhistleFocused(p.Id))) candidates.RemoveAll(p => !HallWhistleFocused(p.Id));
            Peer closest = null; float distance = float.MaxValue;
            foreach (Peer peer in candidates)
            {
                Vector3 delta = peer.Position-baldi.transform.position; delta.y = 0;
                if (delta.sqrMagnitude < distance) { distance = delta.sqrMagnitude; closest = peer; }
            }
            return closest;
        }

        public bool UseNearestBaldi(Baldi baldi)
        {
            // With everyone hidden or caught there is no target, so noises and items must still lure him.
            ChaseTarget chase;
            return Hosting && worldReady && baldi != null && baldi.behaviorStateMachine.CurrentState is Baldi_Chase
                && chaseTargets.TryGetValue(baldi, out chase) && chase.HasTarget;
        }

        static bool ClearWorldLine(Vector3 from, Vector3 to)
        {
            Vector3 direction = to-from;
            foreach (var hit in Physics.RaycastAll(from,direction.normalized,direction.magnitude,~0,QueryTriggerInteraction.Ignore))
                if (hit.collider != null && hit.collider.GetComponentInParent<NPC>() == null && hit.collider.GetComponentInParent<PlayerManager>() == null) return false;
            return true;
        }

        string HidingDiagnostic()
        {
            return ",\"hidingStep\":" + hidingStep + ",\"hidingBeats\":" + hidingBeats + ",\"hidingFinished\":" + hidingFinished.ToString().ToLowerInvariant()
                + ",\"baldiTarget\":" + nearestBaldiPeer + ",\"baldiTargetSwitches\":" + baldiTargetSwitches;
        }
    }

    [HarmonyPatch(typeof(HappyBaldi),"Activate")]
    static class SharedHidingPatch
    { static bool Prefix(HappyBaldi __instance) { var r = ModHooks.Current; return r == null || r.StartHiding(__instance); } }

    [HarmonyPatch(typeof(HideableLocker),"Clicked")]
    static class LockerEntryPatch
    {static void Postfix(HideableLocker __instance,int player){var r=ModHooks.Current;if(r!=null)r.EnteredLocker(__instance,player);}}

    [HarmonyPatch(typeof(NPC),"Update")]
    static class NearestBaldiPatch
    { static void Postfix(NPC __instance) { var r = ModHooks.Current; if (r == null) return; if (__instance is Baldi) r.UpdateNearestBaldi((Baldi)__instance); else r.UpdateRemoteNpcTarget(__instance); } }

    [HarmonyPatch(typeof(Baldi),"Hear")]
    static class NearestBaldiHearingPatch
    { static bool Prefix(Baldi __instance) { var r = ModHooks.Current; return r == null || !r.UseNearestBaldi(__instance); } }
}
