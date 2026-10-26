using System;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        public bool FocusedSync { get; private set; }
        int focusedStage;
        float focusedAt;
        bool focusedRemoteNearest, focusedHostNearest, focusedSwitchedBack;
        Vector3 focusedBaldiPosition;
        [Serializable] public sealed class FocusCommand { public int Phase; public Vector3 Position; }
        string FocusPath { get { return Path.GetFullPath(Path.Combine(Root,Hosting ? "MultiplayerMod/private/focused-command.json" : "../../../MultiplayerMod/private/focused-command.json")); } }

        void UpdateFocusedSync()
        {
            if (!FocusedSync || !worldReady) return;
            if (epoch > 1) { RunShopFocusedTest(); return; }
            if (!Hosting)
            {
                if (!File.Exists(FocusPath)) return;
                var command = JsonUtility.FromJson<FocusCommand>(File.ReadAllText(FocusPath));
                if (command.Phase == focusedStage) return;
                focusedStage = command.Phase;
                Singleton<CoreGameManager>.Instance.GetPlayer(0).Teleport(command.Position);
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (!hidingFinished || now < focusedAt) return;
            var manager = Singleton<BaseGameManager>.Instance;
            var core = Singleton<CoreGameManager>.Instance;
            Baldi baldi = manager.Ec.GetBaldi();
            if (baldi == null || !peers.Values.Any(p => p.Id != self && p.Ready && p.HasPose)) return;
            var remote = peers.Values.First(p => p.Id != self && p.Ready && p.HasPose);
            if (focusedStage == 0)
            {
                focusedBaldiPosition = baldi.transform.position;
                baldi.Navigator.enabled = false; disabledNavigators.Add(baldi.Navigator);
                core.GetPlayer(0).Teleport(focusedBaldiPosition + Vector3.right * 35);
                File.WriteAllText(FocusPath,JsonUtility.ToJson(new FocusCommand { Phase = 1, Position = focusedBaldiPosition + Vector3.forward * 12 }));
                focusedStage = 1; focusedAt = now + 2;
            }
            else if (focusedStage == 1)
            {
                focusedRemoteNearest = FocusHasTarget(baldi,remote);
                core.GetPlayer(0).Teleport(focusedBaldiPosition + Vector3.right * 7);
                File.WriteAllText(FocusPath,JsonUtility.ToJson(new FocusCommand { Phase = 2, Position = focusedBaldiPosition + Vector3.forward * 30 }));
                focusedStage = 2; focusedAt = now + 2;
            }
            else if (focusedStage == 2)
            {
                focusedHostNearest = FocusHasTarget(baldi,peers[self]);
                core.GetPlayer(0).Teleport(focusedBaldiPosition + Vector3.right * 35);
                File.WriteAllText(FocusPath,JsonUtility.ToJson(new FocusCommand { Phase = 3, Position = focusedBaldiPosition + Vector3.forward * 12 }));
                focusedStage = 3; focusedAt = now + 2;
            }
            else if (focusedStage == 3)
            {
                focusedSwitchedBack = FocusHasTarget(baldi,remote);
                focusedStage = 4;
                core.StopAllCoroutines(); manager.StopAllCoroutines(); manager.Ec.ResetEvents(); manager.Ec.gameObject.SetActive(false); core.PrepareForReload();
                AccessTools.Method(typeof(BaseGameManager),"LoadSceneObject",new[] { typeof(SceneObject),typeof(bool) }).Invoke(manager,new object[] { core.sceneObject,true });
            }
        }

        bool FocusHasTarget(Baldi baldi, Peer peer)
        {
            ChaseTarget target;
            var actual = (NavigationState)AccessTools.Field(typeof(NavigationStateMachine),"currentState").GetValue(baldi.navigationStateMachine);
            return nearestBaldiPeer == peer.Id && chaseTargets.TryGetValue(baldi,out target) && actual == target.State && Vector3.Distance(actual.destination,peer.Position) < 1;
        }

        string FocusedDiagnostic()
        {
            return ",\"focusedStage\":" + focusedStage + ",\"nearestRemote\":" + focusedRemoteNearest.ToString().ToLowerInvariant()
                + ",\"nearestHost\":" + focusedHostNearest.ToString().ToLowerInvariant() + ",\"nearestSwitchedBack\":" + focusedSwitchedBack.ToString().ToLowerInvariant();
        }
    }
}
