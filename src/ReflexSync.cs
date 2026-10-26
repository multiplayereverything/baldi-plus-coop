using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BaldiTogether
{
    // Dr. Reflex with remote players (host-authoritative).
    // The host runs the real DrReflex: it notices a remote peer from the peer's pose, charges, and on contact runs
    // the native TestSequence (timing, correct side, hands, audio). Only the targeted owner gets a token, mirrors the
    // hotspots on its replica and reports its click; the host validates the click and calls native HotspotClicked.
    // A failed test (owner walked away) leads to the native-style office trip and hammer hunt; a hammer hit makes the
    // owner squish its own player natively. Ops: ReflexToClient = 78, ReflexToHost = 79.
    sealed class ReflexSession
    {
        public const byte Notice = 1, Chase = 2, Test = 3, Done = 4, Failed = 5, Angry = 6, Hunt = 7;
        public DrReflex Npc;
        public int Peer, Token, LastClick, Points;
        public byte Phase;
        public bool Open, Rapid, HoldAttached;
        public Vector3 LastSeen;
        public float NextPath, FailTimer, RapidTime, ChillTime;
        public readonly MovementModifier Hold = new MovementModifier(Vector3.zero, 0f);
    }

    class ReflexRemoteState : DrReflex_StateBase
    {
        public readonly ReflexSession Session;
        public ReflexRemoteState(DrReflex reflex, ReflexSession session) : base(reflex) { Session = session; }
        protected static T Field<T>(DrReflex reflex, string name) { return (T)AccessTools.Field(typeof(DrReflex), name).GetValue(reflex); }
        protected void Face(Vector3 position)
        {
            Vector3 delta = position - drReflex.transform.position; delta.y = 0;
            if (delta != Vector3.zero) drReflex.transform.rotation = Quaternion.LookRotation(delta.normalized, Vector3.up);
        }
    }

    // DrReflex_Noticing for a remote peer: the runtime decides chase/wander once the native PauseAndTurn finishes.
    sealed class ReflexRemoteNotice : ReflexRemoteState
    {
        public bool Noticed; public int Frames;
        public ReflexRemoteNotice(DrReflex reflex, ReflexSession session) : base(reflex, session) { }
        public override void Enter() { base.Enter(); ChangeNavigationState(new NavigationState_TargetPlayer(npc, 63, Session.LastSeen)); }
        public override void Update() { Frames++; }
    }

    // DrReflex_Chasing toward a remote pose; contact is decided by the runtime from the pose.
    sealed class ReflexRemoteChase : ReflexRemoteState
    {
        public NavigationState_TargetPlayer Target;
        public ReflexRemoteChase(DrReflex reflex, ReflexSession session) : base(reflex, session) { }
        public override void Enter()
        {
            base.Enter(); drReflex.StartCharge();
            Target = new NavigationState_TargetPlayer(npc, 63, Session.LastSeen, true);
            ChangeNavigationState(Target);
        }
        public override void Update() { base.Update(); drReflex.IncreasePitch(); }
        public override void DestinationEmpty()
        {
            base.DestinationEmpty(); drReflex.AudioManager.FlushQueue(true); Target.priority = 0;
            npc.behaviorStateMachine.ChangeState(new DrReflex_Wandering(drReflex, 0f));
        }
        public override void Exit() { base.Exit(); if (Target != null) Target.priority = 0; }
    }

    // DrReflex_Testing without a host PlayerManager. Native EndTest is redirected to the runtime while this runs.
    sealed class ReflexRemoteTest : ReflexRemoteState
    {
        public ReflexRemoteTest(DrReflex reflex, ReflexSession session) : base(reflex, session) { }
        public override void Enter()
        {
            base.Enter();
            var runtime = ModHooks.Current; if (runtime != null) runtime.StartRemoteReflexTest(drReflex, Session);
            ChangeNavigationState(new NavigationState_DoNothing(drReflex, 127));
        }
        public override void Update()
        {
            if (!drReflex.Turning)
            {
                Vector3 direction = Vector3.RotateTowards(drReflex.transform.forward, (Session.LastSeen - drReflex.transform.position).normalized,
                    Time.deltaTime * 2f * Mathf.PI * Field<float>(drReflex, "turnSpeed"), 0f);
                if (direction != Vector3.zero) drReflex.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
            }
            if (Session.Phase == ReflexSession.Failed)
            {
                // Native LoseDelay: two scaled seconds, then DrReflex_Angry.
                Session.FailTimer -= Time.deltaTime * npc.TimeScale;
                if (Session.FailTimer <= 0f) { Session.Phase = ReflexSession.Angry; npc.behaviorStateMachine.ChangeState(new ReflexRemoteAngry(drReflex, Session)); }
                return;
            }
            // Native PlayerLeft against the remote pose.
            if (Session.Phase == ReflexSession.Test && !Field<bool>(drReflex, "testComplete")
                && Vector3.Distance(Session.LastSeen, drReflex.transform.position) > Field<float>(drReflex, "maxTestDistance"))
                drReflex.EndTest(false, null);
        }
    }

    sealed class ReflexRemoteAngry : ReflexRemoteState
    {
        public ReflexRemoteAngry(DrReflex reflex, ReflexSession session) : base(reflex, session) { }
        public override void Enter() { base.Enter(); drReflex.HeadToOffice(); }
        public override void DestinationEmpty()
        {
            base.DestinationEmpty();
            if (drReflex.IsInOffice) { Session.Phase = ReflexSession.Hunt; npc.behaviorStateMachine.ChangeState(new ReflexRemoteHunt(drReflex, Session)); }
            else drReflex.HeadToOffice();
        }
    }

    // DrReflex_Hunting for a remote target. Nearby local entities get native non-final hits; the remote target is hit
    // by the runtime from its pose, and the rapid-hammer/chill sequence below mirrors native RapidHammer(final).
    sealed class ReflexRemoteHunt : ReflexRemoteState
    {
        public NavigationState_TargetPlayer Target;
        float nextHammer, calloutTime = 3f; bool talking;
        public ReflexRemoteHunt(DrReflex reflex, ReflexSession session) : base(reflex, session) { }
        public override void Enter()
        {
            base.Enter();
            Target = new NavigationState_TargetPlayer(npc, 63, Session.LastSeen);
            ChangeNavigationState(Target);
            drReflex.GetHammer(); nextHammer = UnityEngine.Random.Range(0.2f, 2f);
            npc.Navigator.SetRoomAvoidance(false); npc.Navigator.passableObstacles.Add(PassableObstacle.Bully);
        }
        public override void Update()
        {
            if (Session.Rapid)
            {
                if (Session.RapidTime > 0f)
                {
                    Session.RapidTime -= Time.deltaTime; drReflex.Animator.SetBool("RapidSwing", true); Face(Session.LastSeen);
                    if (Session.RapidTime <= 0f) { drReflex.Animator.SetBool("RapidSwing", false); drReflex.Animator.SetBool("Chill", true); Session.ChillTime = 1f; }
                    return;
                }
                Session.ChillTime -= Time.deltaTime; Face(Session.LastSeen);
                if (Session.ChillTime <= 0f) { var runtime = ModHooks.Current; if (runtime != null) runtime.FinishRemoteReflexHammer(drReflex, Session); }
                return;
            }
            base.Update();
            var hooks = ModHooks.Current;
            if (hooks != null) hooks.RemoteReflexHammerCheck(drReflex);
            nextHammer -= Time.deltaTime * npc.TimeScale;
            if (talking && !drReflex.AudioManager.QueuedAudioIsPlaying) { talking = false; drReflex.Animator.SetBool("Chill", false); }
            if (nextHammer <= 0f) { drReflex.Hammer(null); nextHammer = UnityEngine.Random.Range(0.2f, 2f); }
            calloutTime -= Time.deltaTime * npc.TimeScale;
            if (calloutTime <= 0f) { calloutTime = 3f; if (drReflex.CalloutChance(false)) talking = true; }
        }
        public override void Hear(GameObject source, Vector3 position, int value)
        {
            base.Hear(source, position, value);
            if (!npc.looker.PlayerInSight()) { ChangeNavigationState(Target); Target.UpdatePosition(position); }
        }
        public override void DestinationEmpty() { base.DestinationEmpty(); ChangeNavigationState(new NavigationState_WanderRounds(npc, 0)); }
        public override void Exit()
        {
            base.Exit(); npc.Navigator.SetRoomAvoidance(true); npc.Navigator.passableObstacles.Remove(PassableObstacle.Bully);
            drReflex.Animator.SetBool("RapidSwing", false);
        }
    }

    public sealed partial class TogetherRuntime
    {
        sealed class LocalReflexTest { public DrReflex Npc; public int Index, Token, Seq; public bool Open, WorldOpen; public float At; }

        readonly List<ReflexSession> reflexSessions = new List<ReflexSession>();
        readonly HashSet<int> reflexStartTokens = new HashSet<int>(), reflexAwardTokens = new HashSet<int>(), reflexSquishTokens = new HashSet<int>();
        readonly MovementModifier reflexHoldMod = new MovementModifier(Vector3.zero, 0f);
        LocalReflexTest localReflex;
        ActivityModifier reflexHoldTarget, reflexTurnTarget;
        MovementModifier reflexTurnMod;
        float reflexHoldLeft, reflexTurnUntil;
        Transform reflexSightTarget;
        int reflexBystanderHits;
        int nextReflexToken, reflexRemoteTargets, reflexTestsStarted, reflexClicks, reflexRejectedClicks, reflexPasses, reflexFails, reflexHits, reflexCancels;
        int reflexLocalStarts, reflexLocalClicks, reflexLocalPasses, reflexLocalSquishes, reflexLocalCancels;
        bool reflexLabStarted;

        static readonly System.Reflection.MethodInfo reflexTestSequence = AccessTools.Method(typeof(DrReflex), "TestSequence");
        static readonly System.Reflection.MethodInfo reflexTurnPlayer = AccessTools.Method(typeof(DrReflex), "TurnPlayer");
        static void SetReflexField(DrReflex reflex, string name, object value) { AccessTools.Field(typeof(DrReflex), name).SetValue(reflex, value); }

        ReflexSession FindReflexSession(DrReflex reflex)
        { foreach (var s in reflexSessions) if (ReferenceEquals(s.Npc, reflex)) return s; return null; }

        // ---------------- Reset ----------------

        void ResetReflex()
        {
            foreach (var s in reflexSessions.ToArray())
            {
                if (s.Npc == null) continue;
                if (s.Phase == ReflexSession.Test) StopNativeReflexTest(s.Npc);
                ReleaseReflexHold(s);
                var state = s.Npc.behaviorStateMachine.CurrentState as ReflexRemoteState;
                if (state != null && state.Session == s) s.Npc.behaviorStateMachine.ChangeState(new DrReflex_Wandering(s.Npc, 0f));
            }
            reflexSessions.Clear();
            EndLocalReflex(false);
            ReleaseLocalReflexHold();
            if (reflexTurnTarget != null) { reflexTurnTarget.moveMods.Remove(reflexTurnMod); reflexTurnTarget = null; }
            reflexStartTokens.Clear(); reflexAwardTokens.Clear(); reflexSquishTokens.Clear();
        }

        // ---------------- Update ----------------

        void UpdateReflex()
        {
            if (!Active || !worldReady) return;
            var manager = Singleton<BaseGameManager>.Instance; var core = Singleton<CoreGameManager>.Instance;
            if (manager == null || core == null || manager.Ec == null || core.GetPlayer(0) == null) return;
            UpdateLocalReflex(core.GetPlayer(0));
            if (Hosting && manager.Ec.Active) UpdateReflexHost();
        }

        void UpdateReflexHost()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var s in reflexSessions.ToArray()) TickReflexSession(s, now);
            foreach (NPC npc in npcs)
            {
                var reflex = npc as DrReflex;
                if (reflex == null || FindReflexSession(reflex) != null || reflex.Blinded) continue;
                var wandering = reflex.behaviorStateMachine.CurrentState as DrReflex_Wandering;
                if (wandering == null || NpcField<float>(wandering, "cooldown") > 0f) continue;
                Peer target = null; float best = float.MaxValue; bool seen = false;
                foreach (var p in peers.Values)
                {
                    if (!ReflexTargetable(p, now) || p.Tagged) continue;
                    float distance = Vector3.Distance(p.Position, reflex.transform.position);
                    bool sees = ReflexSees(reflex, p.Position);
                    bool touching = FlatDistance(p.Position, reflex.transform.position) < 5f && ClearWorldLine(reflex.transform.position, p.Position);
                    if ((sees || touching) && distance < best) { best = distance; target = p; seen = sees; }
                }
                if (target == null) continue;
                var session = new ReflexSession { Npc = reflex, Peer = target.Id, LastSeen = target.Position, Phase = ReflexSession.Notice };
                reflexSessions.Add(session); reflexRemoteTargets++;
                if (seen && Facing(reflex, target.Position)) { session.Phase = ReflexSession.Chase; reflex.behaviorStateMachine.ChangeState(new ReflexRemoteChase(reflex, session)); }
                else reflex.behaviorStateMachine.ChangeState(new ReflexRemoteNotice(reflex, session));
            }
        }

        void TickReflexSession(ReflexSession s, float now)
        {
            var reflex = s.Npc;
            if (reflex == null) { reflexSessions.Remove(s); return; }
            var state = reflex.behaviorStateMachine.CurrentState as ReflexRemoteState;
            // A native transition (ResetDelay, RapidHammer end, Wandering) ended the remote episode.
            if (state == null || state.Session != s) { EndReflexSession(s, false); return; }
            Peer p;
            bool valid = peers.TryGetValue(s.Peer, out p) && p.Accepted && p.Ready && p.HasPose && !p.Eliminated && !p.InLocker;
            if (!valid)
            {
                // A won test is already settled; its native ResetDelay finishes on its own.
                if (s.Phase != ReflexSession.Done) { EndReflexSession(s, true); reflexCancels++; }
                return;
            }
            bool sees = now - p.PoseAt < 2f && !p.Invisible && ReflexSees(reflex, p.Position);
            switch (s.Phase)
            {
                case ReflexSession.Notice:
                {
                    var notice = (ReflexRemoteNotice)state;
                    if (sees) s.LastSeen = p.Position;
                    if (!notice.Noticed)
                    {
                        // One frame lets the target navigation pick its next point before the native turn reads it.
                        if (notice.Frames < 1) return;
                        if (!Facing(reflex, s.LastSeen)) reflex.PauseAndTurn();
                        notice.Noticed = true;
                    }
                    else if (!reflex.Turning)
                    {
                        if (sees && !p.Tagged) { s.Phase = ReflexSession.Chase; reflex.behaviorStateMachine.ChangeState(new ReflexRemoteChase(reflex, s)); }
                        else reflex.behaviorStateMachine.ChangeState(new DrReflex_Wandering(reflex, 0f));
                    }
                    return;
                }
                case ReflexSession.Chase:
                {
                    var chase = (ReflexRemoteChase)state;
                    if (sees && !p.Tagged)
                    {
                        s.LastSeen = p.Position;
                        if (now >= s.NextPath) { s.NextPath = now + 0.2f; chase.ChangeNavigationState(chase.Target); chase.Target.UpdatePosition(p.Position); }
                    }
                    if (now - p.PoseAt < 2f && FlatDistance(p.Position, reflex.transform.position) < 5f && ClearWorldLine(reflex.transform.position, p.Position))
                    {
                        s.LastSeen = p.Position;
                        if (p.Tagged) { reflex.AudioManager.FlushQueue(true); reflex.behaviorStateMachine.ChangeState(new DrReflex_Wandering(reflex, 10f)); }
                        else reflex.behaviorStateMachine.ChangeState(new ReflexRemoteTest(reflex, s));
                    }
                    return;
                }
                case ReflexSession.Test:
                {
                    s.LastSeen = p.Position;
                    bool open = NpcField<GameObject>(reflex, "leftHotSpot").activeSelf;
                    if (open != s.Open) { s.Open = open; SendReflexCommand(s, 2, w => w.Write(open)); }
                    return;
                }
                case ReflexSession.Done:
                case ReflexSession.Failed:
                    s.LastSeen = p.Position; return;
                case ReflexSession.Angry:
                    if (sees) s.LastSeen = p.Position; return;
                case ReflexSession.Hunt:
                {
                    var hunt = (ReflexRemoteHunt)state;
                    if (sees) { s.LastSeen = p.Position; if (!s.Rapid && now >= s.NextPath) { s.NextPath = now + 0.2f; hunt.ChangeNavigationState(hunt.Target); hunt.Target.UpdatePosition(p.Position); } }
                    if (s.Rapid || p.Tagged || now - p.PoseAt >= 2f || reflex.Navigator.Entity.InteractionDisabled || !reflex.Navigator.Entity.InBounds) return;
                    // Native HammerCheck: overlap sphere 5 ahead with radius 4, then a hammerRaycastMask line check.
                    if (FlatDistance(reflex.transform.position + reflex.transform.forward * 5f, p.Position) > 4.5f) return;
                    if (!ReflexRaycast(reflex, p.Position, NpcField<LayerMask>(reflex, "hammerRaycastMask"), true)) return;
                    s.LastSeen = p.Position; s.Rapid = true; s.RapidTime = UnityEngine.Random.Range(0.5f, 1.5f);
                    reflex.Animator.SetBool("RapidSwing", true); reflex.Hammer(null); reflex.Animator.SetBool("Chill", false);
                    if (!s.HoldAttached) { reflex.Navigator.Am.moveMods.Add(s.Hold); s.HoldAttached = true; }
                    float squish = NpcField<float>(reflex, "squishTime"), hold = s.RapidTime;
                    SendReflexCommand(s, 4, w => { w.Write(squish); w.Write(hold); });
                    reflexHits++;
                    return;
                }
            }
        }

        // Called from ReflexRemoteTest.Enter: native StartTest without the host-only TurnPlayer.
        internal void StartRemoteReflexTest(DrReflex reflex, ReflexSession s)
        {
            reflex.Navigator.SetSpeed(0f); reflex.Navigator.maxSpeed = 0f;
            NpcField<GameObject>(reflex, "leftHotSpot").SetActive(false); NpcField<GameObject>(reflex, "rightHotSpot").SetActive(false);
            SetReflexField(reflex, "currentYtps", NpcField<int>(reflex, "initialYtps"));
            reflex.ResetTest();
            var sequence = (IEnumerator)reflexTestSequence.Invoke(reflex, null);
            SetReflexField(reflex, "testCoroutine", sequence);
            reflex.StartCoroutine(sequence);
            reflex.AudioManager.SetLoop(false);
            reflex.AudioManager.QueueAudio(NpcField<SoundObject>(reflex, "audThinkEnd"));
            s.Phase = ReflexSession.Test; s.Token = ++nextReflexToken; s.LastClick = 0; s.Open = false; s.Points = 0;
            reflexTestsStarted++;
            SendReflexCommand(s, 1, null);
        }

        void StopNativeReflexTest(DrReflex reflex)
        {
            foreach (string name in new[] { "leftHotSpot", "rightHotSpot", "leftHighlight", "rightHighlight" })
            { var part = NpcField<GameObject>(reflex, name); if (part != null) part.SetActive(false); }
            var sequence = NpcField<IEnumerator>(reflex, "testCoroutine");
            if (sequence != null) reflex.StopCoroutine(sequence);
            SetReflexField(reflex, "testComplete", true);
            reflex.AudioManager.pitchModifier = 1f;
            reflex.Animator.SetTrigger("CloseHands");
        }

        // Native EndTest body for a remote test: the YTPs go to the owner and failure goes to the remote angry state.
        void EndRemoteReflexTest(ReflexSession s, bool success)
        {
            var reflex = s.Npc;
            foreach (string name in new[] { "leftHotSpot", "rightHotSpot", "leftHighlight", "rightHighlight" })
            { var part = NpcField<GameObject>(reflex, name); if (part != null) part.SetActive(false); }
            var sequence = NpcField<IEnumerator>(reflex, "testCoroutine");
            if (sequence != null) reflex.StopCoroutine(sequence);
            SetReflexField(reflex, "testComplete", true);
            s.Open = false;
            if (success)
            {
                s.Points = Mathf.Max(NpcField<int>(reflex, "currentYtps"), 0);
                reflex.Animator.SetTrigger("CloseHands");
                s.Phase = ReflexSession.Done; reflexPasses++;
                int points = s.Points;
                // Native EndTest pays the shared (host-owned) wallet; clients cannot add points themselves.
                Singleton<CoreGameManager>.Instance.AddPoints(points, 0, points > 0);
                SendReflexCommand(s, 3, w => { w.Write((byte)1); w.Write(points); });
            }
            else
            {
                s.Phase = ReflexSession.Failed; s.FailTimer = 2f;
                reflex.Animator.SetTrigger("Failed");
                reflex.AudioManager.FlushQueue(true); reflex.AudioManager.QueueAudio(NpcField<SoundObject>(reflex, "audSession"));
                reflexFails++;
                SendReflexCommand(s, 3, w => { w.Write((byte)2); w.Write(0); });
            }
            SetReflexField(reflex, "level", NpcField<int>(reflex, "level") + 1);
            reflex.AudioManager.pitchModifier = 1f;
        }

        static readonly System.Reflection.MethodInfo reflexRapidHammer = AccessTools.Method(typeof(DrReflex), "RapidHammer");

        // Native HammerCheck, except no local entity is ever the final target while the session target is remote:
        // a host walking by gets the native non-final squish and rapid hammer, and the hunt for the remote player goes on.
        internal void RemoteReflexHammerCheck(DrReflex reflex)
        {
            if (reflex.Navigator.Entity.InteractionDisabled || !reflex.Navigator.Entity.InBounds) return;
            var colliders = NpcField<Collider[]>(reflex, "hammerColliders");
            int count = Physics.OverlapSphereNonAlloc(reflex.transform.position + reflex.transform.forward * 5f, 4f, colliders, NpcField<LayerMask>(reflex, "hammerMask"), QueryTriggerInteraction.Collide);
            var core = Singleton<CoreGameManager>.Instance; var player = core == null ? null : core.GetPlayer(0);
            for (int i = 0; i < count; i++)
            {
                var hit = colliders[i];
                if (!hit.isTrigger || hit.transform == reflex.transform) continue;
                Entity entity = hit.GetComponent<Entity>();
                if (entity == null || entity.Squished || reflex.Entity.IsIgnoring(entity) || !reflex.Entity.CanCollideWith(entity)) continue;
                bool sighted;
                reflex.looker.Raycast(hit.transform, Vector3.Magnitude(reflex.transform.position - hit.transform.position), NpcField<LayerMask>(reflex, "hammerRaycastMask"), out sighted);
                if (!sighted) continue;
                reflex.Animator.SetBool("RapidSwing", true);
                reflex.Hammer(entity);
                reflex.StartCoroutine((IEnumerator)reflexRapidHammer.Invoke(reflex, new object[] { entity, false }));
                if (player != null && hit.transform == player.transform) reflexBystanderHits++;
            }
        }

        internal void FinishRemoteReflexHammer(DrReflex reflex, ReflexSession s)
        {
            reflex.Animator.SetBool("Chill", false);
            ReleaseReflexHold(s);
            s.Rapid = false;
            reflex.behaviorStateMachine.ChangeState(new DrReflex_Wandering(reflex, NpcField<float>(reflex, "cooldown") + NpcField<float>(reflex, "squishTime")));
            reflex.AudioManager.QueueAudio(NpcField<SoundObject>(reflex, "audNotBad"));
        }

        void ReleaseReflexHold(ReflexSession s)
        {
            if (!s.HoldAttached) return;
            if (s.Npc != null) s.Npc.Navigator.Am.moveMods.Remove(s.Hold);
            s.HoldAttached = false;
        }

        void EndReflexSession(ReflexSession s, bool toWander)
        {
            reflexSessions.Remove(s);
            if (s.Phase == ReflexSession.Test)
            {
                if (s.Npc != null) StopNativeReflexTest(s.Npc);
                SendReflexCommand(s, 3, w => { w.Write((byte)0); w.Write(0); });
            }
            ReleaseReflexHold(s);
            if (s.Npc != null) s.Npc.Animator.SetBool("RapidSwing", false);
            if (toWander && s.Npc != null)
            {
                var state = s.Npc.behaviorStateMachine.CurrentState as ReflexRemoteState;
                if (state != null && state.Session == s)
                { s.Npc.AudioManager.FlushQueue(true); s.Npc.behaviorStateMachine.ChangeState(new DrReflex_Wandering(s.Npc, 0f)); }
            }
        }

        // ---------------- Sight helpers ----------------

        bool ReflexTargetable(Peer p, float now)
        {
            return p.Id != self && p.Accepted && p.Ready && p.HasPose && !p.Eliminated && !p.InLocker && !p.Invisible && now - p.PoseAt < 2f;
        }

        static float FlatDistance(Vector3 a, Vector3 b) { a.y = 0; b.y = 0; return Vector3.Distance(a, b); }

        static bool Facing(DrReflex reflex, Vector3 position)
        { return Vector3.Angle(reflex.transform.forward, position - reflex.transform.position) <= 22.5f; }

        bool ReflexSees(DrReflex reflex, Vector3 position)
        {
            if (reflex.Blinded || reflex.Navigator.Entity.Blinded || reflex.looker == null) return false;
            Vector3 delta = position - reflex.transform.position;
            if (delta.magnitude > Mathf.Min(reflex.looker.distance, reflex.ec.MaxRaycast)) return false;
            bool fov = (bool)AccessTools.Field(typeof(Looker), "hasFov").GetValue(reflex.looker);
            float angle = (float)AccessTools.Field(typeof(Looker), "fieldOfView").GetValue(reflex.looker);
            if (fov && Vector3.Angle(reflex.transform.forward, delta) > angle) return false;
            return ReflexRaycast(reflex, position, default(LayerMask), false);
        }

        // Native Looker.Raycast against a stand-in transform on the player's target layer, as BaldiContact does.
        bool ReflexRaycast(DrReflex reflex, Vector3 position, LayerMask mask, bool useMask)
        {
            var core = Singleton<CoreGameManager>.Instance;
            if (core == null || core.GetPlayer(0) == null || reflex.looker == null) return false;
            if (reflexSightTarget == null) { reflexSightTarget = new GameObject("Reflex remote sight target").transform; reflexSightTarget.SetParent(transform, false); }
            reflexSightTarget.gameObject.layer = (int)AccessTools.Field(typeof(Entity), "targetLayer").GetValue(core.GetPlayer(0).plm.Entity);
            reflexSightTarget.position = position;
            bool visible; float distance = Vector3.Distance(reflex.transform.position, position);
            if (useMask) reflex.looker.Raycast(reflexSightTarget, distance, mask, out visible);
            else reflex.looker.Raycast(reflexSightTarget, distance, out visible);
            return visible;
        }

        // ---------------- Wire ----------------

        byte[] ReflexCommandPacket(ReflexSession s, byte sub, Action<BinaryWriter> extra)
        {
            int index = Array.IndexOf(npcs, (NPC)s.Npc);
            if (index < 0) return null;
            int character = (int)s.Npc.Character, token = s.Token;
            return Wire.Packet(Op.ReflexToClient, w => { w.Write(epoch); w.Write(sub); w.Write(index); w.Write(character); w.Write(token); if (extra != null) extra(w); });
        }

        void SendReflexCommand(ReflexSession s, byte sub, Action<BinaryWriter> extra)
        {
            if (!peers.ContainsKey(s.Peer) || s.Token < 1) return;
            byte[] packet = ReflexCommandPacket(s, sub, extra);
            if (packet != null) Network.Send(s.Peer, packet);
        }

        bool HandleReflexClient(int id, Peer sender, Op op, BinaryReader r)
        {
            if (op != Op.ReflexToHost) return false;
            int generation = r.ReadInt32(); byte sub = r.ReadByte();
            int index = r.ReadInt32(), token = r.ReadInt32(), seq = r.ReadInt32(); byte side = r.ReadByte();
            if (sub != 1 || token < 1 || seq < 1 || (side != (byte)Direction.East && side != (byte)Direction.West)) throw new IOException("Invalid Reflex click.");
            if (generation != epoch || !worldReady || !Hosting || !sender.Ready || sender.Eliminated || sender.InLocker || index < 0 || index >= npcs.Length) return true;
            var reflex = npcs[index] as DrReflex;
            var s = reflex == null ? null : FindReflexSession(reflex);
            if (s == null || s.Peer != id || s.Token != token || s.Phase != ReflexSession.Test || seq <= s.LastClick
                || !NpcField<GameObject>(reflex, "leftHotSpot").activeSelf
                || Vector3.Distance(sender.Position, reflex.transform.position) > NpcField<float>(reflex, "maxTestDistance"))
            { reflexRejectedClicks++; return true; }
            s.LastClick = seq; reflexClicks++;
            Applying = true;
            try { reflex.HotspotClicked((Direction)side); }
            finally { Applying = false; }
            return true;
        }

        bool HandleReflexHost(Op op, BinaryReader r)
        {
            if (op != Op.ReflexToClient) return false;
            int generation = r.ReadInt32(); byte sub = r.ReadByte();
            int index = r.ReadInt32(), character = r.ReadInt32(), token = r.ReadInt32();
            bool open = false; byte result = 0; int points = 0; float squish = 0, hold = 0;
            if (sub == 2) open = r.ReadBoolean();
            else if (sub == 3) { result = r.ReadByte(); points = r.ReadInt32(); }
            else if (sub == 4) { squish = Wire.Number(r); hold = Wire.Number(r); }
            else if (sub != 1) throw new IOException("Invalid Reflex command.");
            if (token < 1 || result > 2 || points < 0 || points > 100 || squish < 0 || squish > 60 || hold < 0 || hold > 5) throw new IOException("Invalid Reflex command.");
            if (generation != epoch || !worldReady || index < 0 || index >= npcs.Length) return true;
            var reflex = npcs[index] as DrReflex;
            if (reflex == null || (int)reflex.Character != character) return true;
            var core = Singleton<CoreGameManager>.Instance; var player = core == null ? null : core.GetPlayer(0);
            if (player == null) return true;
            float now = Time.realtimeSinceStartup;
            if (sub == 1)
            {
                if (reflexStartTokens.Count >= 4096) reflexStartTokens.Clear();
                if (!reflexStartTokens.Add(token) || LocalEliminated) return true;
                EndLocalReflex(false);
                localReflex = new LocalReflexTest { Npc = reflex, Index = index, Token = token, At = now };
                SetLocalReflexHotspots(false);
                Applying = true;
                try { StartReflexTurn(reflex, player, 1f); }
                finally { Applying = false; }
                reflexLocalStarts++;
            }
            else if (sub == 2)
            {
                if (localReflex == null || localReflex.Token != token || localReflex.Index != index) return true;
                SetLocalReflexHotspots(open);
                if (open && !Hosting)
                { var audio = reflex.AudioManager; audio.FlushQueue(true); audio.QueueAudio(NpcField<SoundObject>(reflex, "audFast")); }
            }
            else if (sub == 3)
            {
                if (localReflex != null && localReflex.Token == token && localReflex.Index == index) EndLocalReflex(false);
                // The host already paid the wallet; the owner only acknowledges the pass with the native cue.
                if (result == 1 && reflexStartTokens.Contains(token) && reflexAwardTokens.Add(token) && !LocalEliminated)
                { reflexLocalPasses++; if (!Hosting) reflex.AudioManager.QueueRandomAudio(NpcField<SoundObject[]>(reflex, "audCorrect")); }
            }
            else
            {
                if (reflexSquishTokens.Count >= 4096) reflexSquishTokens.Clear();
                if (!reflexSquishTokens.Add(token) || LocalEliminated) return true;
                Applying = true;
                try
                {
                    player.plm.Entity.Squish(squish);
                    ReleaseLocalReflexHold();
                    reflexHoldTarget = player.plm.Entity.ExternalActivity; reflexHoldTarget.moveMods.Add(reflexHoldMod); reflexHoldLeft = hold;
                    StartReflexTurn(reflex, player, 3f);
                }
                finally { Applying = false; }
                reflexLocalSquishes++;
            }
            return true;
        }

        // ---------------- Owner side ----------------

        void StartReflexTurn(DrReflex reflex, PlayerManager player, float speed)
        {
            if (reflexTurnTarget != null && reflexTurnMod != null) reflexTurnTarget.moveMods.Remove(reflexTurnMod);
            reflexTurnMod = NpcField<MovementModifier>(reflex, "moveMod"); reflexTurnTarget = player.plm.am;
            reflexTurnUntil = Time.realtimeSinceStartup + 1.5f;
            reflex.StartCoroutine((IEnumerator)reflexTurnPlayer.Invoke(reflex, new object[] { player, speed }));
        }

        void SetLocalReflexHotspots(bool open)
        {
            if (localReflex == null) return;
            localReflex.Open = open;
            if (localReflex.Npc == null) return;
            // On the host (lab) the NPC is the real one; its hotspots belong to the native sequence.
            if (Hosting) return;
            NpcField<GameObject>(localReflex.Npc, "leftHotSpot").SetActive(open);
            NpcField<GameObject>(localReflex.Npc, "rightHotSpot").SetActive(open);
        }

        void EndLocalReflex(bool cancelled)
        {
            if (localReflex == null) return;
            SetLocalReflexHotspots(false);
            if (reflexTurnTarget != null && reflexTurnMod != null) { reflexTurnTarget.moveMods.Remove(reflexTurnMod); reflexTurnTarget = null; }
            if (cancelled) reflexLocalCancels++;
            localReflex = null;
        }

        void ReleaseLocalReflexHold()
        {
            if (reflexHoldTarget != null) reflexHoldTarget.moveMods.Remove(reflexHoldMod);
            reflexHoldTarget = null; reflexHoldLeft = 0;
        }

        void UpdateLocalReflex(PlayerManager player)
        {
            float now = Time.realtimeSinceStartup;
            if (reflexTurnTarget != null && now > reflexTurnUntil) { reflexTurnTarget.moveMods.Remove(reflexTurnMod); reflexTurnTarget = null; }
            if (reflexHoldTarget != null)
            {
                reflexHoldLeft -= Time.deltaTime;
                if (reflexHoldLeft <= 0 || LocalEliminated) ReleaseLocalReflexHold();
            }
            if (localReflex != null && (localReflex.Npc == null || LocalEliminated)) EndLocalReflex(true);
        }

        public bool AllowReflexHotspot(DrReflex reflex, Direction side)
        {
            if (!Active) return true;
            if (Hosting)
            {
                // The host's own player cannot answer a test that belongs to a remote player.
                var s = FindReflexSession(reflex);
                return s == null || s.Phase != ReflexSession.Test || Applying;
            }
            if (!ModHooks.ClientWorld) return true;
            if (localReflex != null && ReferenceEquals(localReflex.Npc, reflex) && localReflex.Open && !LocalEliminated)
            {
                int index = localReflex.Index, token = localReflex.Token, seq = ++localReflex.Seq;
                Network.Send(0, Wire.Packet(Op.ReflexToHost, w => { w.Write(epoch); w.Write((byte)1); w.Write(index); w.Write(token); w.Write(seq); w.Write((byte)side); }));
                SetLocalReflexHotspots(false);
                reflex.AudioManager.PlaySingle(NpcField<SoundObject>(reflex, "audHit"));
                reflexLocalClicks++;
            }
            return false;
        }

        public bool BeforeReflexEndTest(DrReflex reflex, bool success)
        {
            if (!Active || !Hosting) return true;
            var s = FindReflexSession(reflex);
            if (s == null || s.Phase != ReflexSession.Test) return true;
            EndRemoteReflexTest(s, success);
            return false;
        }

        // ---------------- World section ----------------

        void WriteReflexWorld(BinaryWriter w)
        {
            var tests = new List<ReflexSession>();
            foreach (var s in reflexSessions)
                if (s.Phase == ReflexSession.Test && s.Npc != null && Array.IndexOf(npcs, (NPC)s.Npc) >= 0 && tests.Count < 16) tests.Add(s);
            w.Write((byte)tests.Count);
            foreach (var s in tests) { w.Write(Array.IndexOf(npcs, (NPC)s.Npc)); w.Write(s.Peer); w.Write(s.Token); w.Write(s.Open); }
        }

        void ReadReflexWorld(BinaryReader r, bool apply)
        {
            int count = r.ReadByte();
            if (count > 16) throw new IOException("Reflex test limit exceeded.");
            bool found = false, open = false;
            for (int i = 0; i < count; i++)
            {
                int index = r.ReadInt32(), owner = r.ReadInt32(), token = r.ReadInt32(); bool entryOpen = r.ReadBoolean();
                if (index < 0 || index > 255 || owner < 0 || owner > 255 || token < 1) throw new IOException("Invalid Reflex test state.");
                if (localReflex != null && localReflex.Token == token && localReflex.Index == index) { found = true; open = entryOpen; }
            }
            if (!apply || localReflex == null) return;
            if (found) { if (open != localReflex.WorldOpen) { localReflex.WorldOpen = open; SetLocalReflexHotspots(open); } }
            else if (Time.realtimeSinceStartup - localReflex.At > 0.5f) EndLocalReflex(true);
        }

        string ReflexDiagnostic()
        {
            return ",\"reflexRemoteTargets\":" + reflexRemoteTargets + ",\"reflexTestsStarted\":" + reflexTestsStarted + ",\"reflexClicks\":" + reflexClicks
                + ",\"reflexRejectedClicks\":" + reflexRejectedClicks + ",\"reflexPasses\":" + reflexPasses + ",\"reflexFails\":" + reflexFails + ",\"reflexHits\":" + reflexHits
                + ",\"reflexCancels\":" + reflexCancels + ",\"reflexBystanderHits\":" + reflexBystanderHits + ",\"reflexSessions\":" + reflexSessions.Count + ",\"reflexLocalStarts\":" + reflexLocalStarts
                + ",\"reflexLocalClicks\":" + reflexLocalClicks + ",\"reflexLocalPasses\":" + reflexLocalPasses + ",\"reflexLocalSquishes\":" + reflexLocalSquishes
                + ",\"reflexLocalCancels\":" + reflexLocalCancels + ",\"reflexLocalTest\":" + (localReflex != null ? "true" : "false")
                + ",\"reflexHoldAttached\":" + (reflexHoldTarget != null ? "true" : "false");
        }

        // ---------------- Lab tour ----------------

        void UpdateReflexLab()
        {
            if (reflexLabStarted || !Lab.Enabled || !TitleVisible || !Lab.HasArg("-bt-reflex-tour")) return;
            reflexLabStarted = true; StartCoroutine(TourReflex());
        }

        static BinaryReader ReflexPacketReader(byte[] packet)
        { var reader = new BinaryReader(new MemoryStream(packet)); reader.ReadByte(); return reader; }

        byte[] ReflexClickPacket(int generation, int index, int token, int seq, Direction side)
        { return Wire.Packet(Op.ReflexToHost, w => { w.Write(generation); w.Write((byte)1); w.Write(index); w.Write(token); w.Write(seq); w.Write((byte)side); }); }

        byte[] ReflexWorldBytes()
        { using (var stream = new MemoryStream()) { using (var writer = new BinaryWriter(stream)) WriteReflexWorld(writer); return stream.ToArray(); } }

        bool ApplyReflexWorld(byte[] data, bool apply)
        { using (var stream = new MemoryStream(data)) using (var reader = new BinaryReader(stream)) { ReadReflexWorld(reader, apply); return stream.Position == stream.Length; } }

        System.Collections.IEnumerator TourReflex()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            portText = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(); listener.Stop();
            Connect(true); if (!Active) yield break; AutoStartRun(); while (!worldReady) yield return null;
            yield return new WaitForSecondsRealtime(2);
            var core = Singleton<CoreGameManager>.Instance; var ec = Singleton<BaseGameManager>.Instance.Ec; var player = core.GetPlayer(0);
            var reflex = ec.Npcs.OfType<DrReflex>().FirstOrDefault();
            if (reflex == null)
            {
                var prefab = Resources.FindObjectsOfTypeAll<DrReflex>().FirstOrDefault(n => !n.gameObject.scene.IsValid());
                if (prefab != null) reflex = (DrReflex)ec.SpawnNPC(prefab, ec.CellFromPosition(player.transform.position).position);
            }
            npcs = ec.Npcs.ToArray();
            if (reflex == null) { CheckNativeUi("reflex-present", false); WriteLabResults("reflex-results.json", "Reflex"); yield break; }
            int index = Array.IndexOf(npcs, (NPC)reflex);
            // Keep the host's own player far away so native sight does not compete with the remote peer.
            var far = ec.AllTilesNoGarbage(false, false).OrderByDescending(c => (c.FloorWorldPosition - player.transform.position).sqrMagnitude).First();
            Vector3 spot = far.FloorWorldPosition; spot.y = reflex.transform.position.y;
            reflex.Navigator.Entity.Teleport(spot); reflex.transform.rotation = Quaternion.identity;
            var peer = new Peer { Id = 3, Name = "Reflex patient", Accepted = true, Ready = true, HasPose = true, PoseAt = Time.realtimeSinceStartup };
            peers[3] = peer;
            Action<float> place = d => { peer.Position = reflex.transform.position + reflex.transform.forward * d; peer.PoseAt = Time.realtimeSinceStartup; };
            Func<ReflexSession> beginTest = () =>
            {
                reflex.behaviorStateMachine.ChangeState(new DrReflex_Wandering(reflex, 0f));
                place(3); UpdateReflexHost();
                var started = FindReflexSession(reflex);
                if (started == null) return null;
                started.Phase = ReflexSession.Chase; reflex.behaviorStateMachine.ChangeState(new ReflexRemoteChase(reflex, started));
                place(2); UpdateReflexHost();
                return started;
            };

            // 1. Eliminated and locker peers are never targets.
            reflex.behaviorStateMachine.ChangeState(new DrReflex_Wandering(reflex, 0f));
            peer.Eliminated = true; place(3); UpdateReflexHost(); bool excluded = FindReflexSession(reflex) == null;
            peer.Eliminated = false; peer.InLocker = true; place(3); UpdateReflexHost(); excluded &= FindReflexSession(reflex) == null;
            peer.InLocker = false;
            CheckNativeUi("excluded-peers-not-targeted", excluded);

            // 2. Host notices the remote peer from its pose.
            place(3); UpdateReflexHost();
            var s = FindReflexSession(reflex);
            CheckNativeUi("host-targets-remote", s != null && s.Peer == 3 && (reflex.behaviorStateMachine.CurrentState is ReflexRemoteChase || reflex.behaviorStateMachine.CurrentState is ReflexRemoteNotice));

            // 3. Contact starts the native test sequence for the remote owner.
            s = beginTest();
            CheckNativeUi("remote-test-starts", s != null && s.Phase == ReflexSession.Test && s.Token > 0 && reflex.behaviorStateMachine.CurrentState is ReflexRemoteTest
                && reflex.Navigator.maxSpeed == 0f && NpcField<IEnumerator>(reflex, "testCoroutine") != null);
            if (s == null) { WriteLabResults("reflex-results.json", "Reflex"); peers.Remove(3); yield break; }

            // 4. The owner applies the start once (native TurnPlayer), duplicates ignored.
            byte[] start = ReflexCommandPacket(s, 1, null);
            int startsBefore = reflexLocalStarts;
            HandleReflexHost(Op.ReflexToClient, ReflexPacketReader(start)); HandleReflexHost(Op.ReflexToClient, ReflexPacketReader(start));
            var turnMod = NpcField<MovementModifier>(reflex, "moveMod");
            CheckNativeUi("owner-start-once", reflexLocalStarts == startsBefore + 1 && localReflex != null && localReflex.Token == s.Token && player.plm.am.moveMods.Count(m => m == turnMod) == 1);

            // 5. Wait for the native sequence to open the hands, then mirror the hotspots to the owner.
            float until = Time.realtimeSinceStartup + 15;
            while (Time.realtimeSinceStartup < until && !s.Open) { place(2); yield return null; }
            HandleReflexHost(Op.ReflexToClient, ReflexPacketReader(ReflexCommandPacket(s, 2, w => w.Write(true))));
            CheckNativeUi("hotspots-open-mirrored", s.Open && localReflex != null && localReflex.Open && NpcField<GameObject>(reflex, "leftHotSpot").activeSelf);

            // 6. Stale epoch and forged token clicks are ignored.
            int clicksBefore = reflexClicks;
            HandleReflexClient(3, peer, Op.ReflexToHost, ReflexPacketReader(ReflexClickPacket(epoch + 1, index, s.Token, 1, Direction.East)));
            HandleReflexClient(3, peer, Op.ReflexToHost, ReflexPacketReader(ReflexClickPacket(epoch, index, s.Token + 1000, 1, Direction.East)));
            HandleReflexClient(2, peer, Op.ReflexToHost, ReflexPacketReader(ReflexClickPacket(epoch, index, s.Token, 1, Direction.East)));
            CheckNativeUi("stale-and-forged-clicks-ignored", reflexClicks == clicksBefore && s.Phase == ReflexSession.Test);

            // 7. The owner's correct click passes; YTPs are not given to the host.
            var correct = NpcField<Direction>(reflex, "correctSide");
            if (reflex.Entity.Flipped) correct = correct == Direction.East ? Direction.West : Direction.East;
            int hostPoints = core.GetPoints(0), passesBefore = reflexPasses, nativeAmount = Mathf.Max(NpcField<int>(reflex, "currentYtps"), 0);
            byte[] click = ReflexClickPacket(epoch, index, s.Token, 1, correct);
            HandleReflexClient(3, peer, Op.ReflexToHost, ReflexPacketReader(click));
            CheckNativeUi("valid-click-pays-host-wallet", s.Phase == ReflexSession.Done && reflexPasses == passesBefore + 1 && NpcField<bool>(reflex, "testComplete")
                && s.Points == nativeAmount && core.GetPoints(0) == hostPoints + nativeAmount && !NpcField<GameObject>(reflex, "leftHotSpot").activeSelf);

            // 8. Replayed click ignored.
            clicksBefore = reflexClicks;
            HandleReflexClient(3, peer, Op.ReflexToHost, ReflexPacketReader(click));
            CheckNativeUi("replayed-click-ignored", reflexClicks == clicksBefore && reflexPasses == passesBefore + 1 && core.GetPoints(0) == hostPoints + nativeAmount);

            // 9. The owner acknowledges the pass once and never adds to the wallet itself.
            int points = s.Points, localPassesBefore = reflexLocalPasses;
            byte[] end = ReflexCommandPacket(s, 3, w => { w.Write((byte)1); w.Write(points); });
            int ownerPoints = core.GetPoints(0);
            HandleReflexHost(Op.ReflexToClient, ReflexPacketReader(end)); HandleReflexHost(Op.ReflexToClient, ReflexPacketReader(end));
            CheckNativeUi("owner-pass-ack-no-double-pay", core.GetPoints(0) == ownerPoints && reflexLocalPasses == localPassesBefore + 1 && localReflex == null && !player.plm.am.moveMods.Contains(turnMod));

            // 10. Native ResetDelay returns to Wandering and the session is released.
            until = Time.realtimeSinceStartup + 6;
            while (Time.realtimeSinceStartup < until && FindReflexSession(reflex) != null) { place(2); yield return null; }
            CheckNativeUi("pass-returns-native-wander", FindReflexSession(reflex) == null && reflex.behaviorStateMachine.CurrentState is DrReflex_Wandering);

            // 11. Walking away fails the test; native-timed angry trip follows.
            s = beginTest();
            int failsBefore = reflexFails;
            if (s != null) place(NpcField<float>(reflex, "maxTestDistance") + 15);
            until = Time.realtimeSinceStartup + 8;
            while (Time.realtimeSinceStartup < until && s != null && s.Phase != ReflexSession.Angry && s.Phase != ReflexSession.Hunt) { peer.PoseAt = Time.realtimeSinceStartup; yield return null; }
            CheckNativeUi("fail-sends-angry", s != null && reflexFails == failsBefore + 1
                && (reflex.behaviorStateMachine.CurrentState is ReflexRemoteAngry || reflex.behaviorStateMachine.CurrentState is ReflexRemoteHunt));

            // 11b. While hunting a remote target, a host walking by takes a non-final hit; the hunt continues.
            bool bystander = false;
            if (s != null && FindReflexSession(reflex) == s)
            {
                if (!(reflex.behaviorStateMachine.CurrentState is ReflexRemoteHunt)) { s.Phase = ReflexSession.Hunt; reflex.behaviorStateMachine.ChangeState(new ReflexRemoteHunt(reflex, s)); }
                Vector3 home = player.transform.position;
                if (player.plm.Entity.Squished) player.plm.Entity.Unsquish();
                reflex.Navigator.Entity.Teleport(spot); reflex.transform.rotation = Quaternion.identity;
                Vector3 front = spot + Vector3.forward * 2.5f; front.y = home.y;
                player.Teleport(front); Physics.SyncTransforms();
                int hitsBefore = reflexBystanderHits;
                RemoteReflexHammerCheck(reflex);
                bystander = reflexBystanderHits == hitsBefore + 1 && player.plm.Entity.Squished && reflex.behaviorStateMachine.CurrentState is ReflexRemoteHunt
                    && FindReflexSession(reflex) == s && !s.Rapid;
                player.Teleport(home); Physics.SyncTransforms();
                yield return new WaitForSecondsRealtime(1.7f);
                bystander &= reflex.behaviorStateMachine.CurrentState is ReflexRemoteHunt && FindReflexSession(reflex) == s
                    && !player.plm.Entity.ExternalActivity.moveMods.Contains(turnMod);
                if (player.plm.Entity.Squished) player.plm.Entity.Unsquish();
            }
            CheckNativeUi("bystander-host-hit-non-final", bystander);

            // 12. A hammer hit squishes the owner's own player once and holds it with a balanced modifier.
            var squishSession = new ReflexSession { Npc = reflex, Peer = 3, Token = ++nextReflexToken };
            byte[] squish = ReflexCommandPacket(squishSession, 4, w => { w.Write(30f); w.Write(5f); });
            int squishBefore = reflexLocalSquishes;
            HandleReflexHost(Op.ReflexToClient, ReflexPacketReader(squish)); HandleReflexHost(Op.ReflexToClient, ReflexPacketReader(squish));
            CheckNativeUi("owner-squish-once", reflexLocalSquishes == squishBefore + 1 && player.plm.Entity.Squished
                && player.plm.Entity.ExternalActivity.moveMods.Count(m => m == reflexHoldMod) == 1);

            // 13. Disconnect cancels the hunt and restores native wandering.
            peers.Remove(3); UpdateReflexHost();
            CheckNativeUi("disconnect-cancels", FindReflexSession(reflex) == null && reflex.behaviorStateMachine.CurrentState is DrReflex_Wandering
                && (s == null || !reflex.Navigator.Am.moveMods.Contains(s.Hold)));

            // 14. World section round trip: open state applied to the owner; a locker cancel clears the owner test.
            peers[3] = peer;
            s = beginTest();
            bool world = s != null;
            if (s != null)
            {
                HandleReflexHost(Op.ReflexToClient, ReflexPacketReader(ReflexCommandPacket(s, 1, null)));
                s.Open = true;
                byte[] section = ReflexWorldBytes();
                world &= ApplyReflexWorld(section, false) && localReflex != null && !localReflex.WorldOpen;
                world &= ApplyReflexWorld(section, true) && localReflex != null && localReflex.WorldOpen && localReflex.Open;
                peer.InLocker = true; UpdateReflexHost();
                world &= FindReflexSession(reflex) == null && reflex.behaviorStateMachine.CurrentState is DrReflex_Wandering && !NpcField<GameObject>(reflex, "leftHotSpot").activeSelf;
                yield return new WaitForSecondsRealtime(0.6f);
                world &= ApplyReflexWorld(ReflexWorldBytes(), true) && localReflex == null;
                peer.InLocker = false;
            }
            CheckNativeUi("world-roundtrip", world);

            // 15. Reset releases every reference-counted modifier; without a session the native path runs.
            ResetReflex();
            CheckNativeUi("reset-balanced-native-path", reflexSessions.Count == 0 && localReflex == null
                && !player.plm.Entity.ExternalActivity.moveMods.Contains(reflexHoldMod) && !player.plm.am.moveMods.Contains(turnMod)
                && AllowReflexHotspot(reflex, Direction.East) && BeforeReflexEndTest(reflex, true));
            if (player.plm.Entity.Squished) player.plm.Entity.Unsquish();
            peers.Remove(3);
            WriteLabResults("reflex-results.json", "Reflex");
        }
    }

    [HarmonyPatch(typeof(DrReflex), "HotspotClicked")]
    static class ReflexHotspotPatch
    { static bool Prefix(DrReflex __instance, Direction side) { var r = ModHooks.Current; return r == null || r.AllowReflexHotspot(__instance, side); } }

    [HarmonyPatch(typeof(DrReflex), "EndTest")]
    static class ReflexEndTestPatch
    { static bool Prefix(DrReflex __instance, bool success) { var r = ModHooks.Current; return r == null || r.BeforeReflexEndTest(__instance, success); } }
}
