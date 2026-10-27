using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object=UnityEngine.Object;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        Transform contactTarget;
        bool NativeRemoteContactVisible(Baldi baldi,Vector3 position)
        {
            var core=Singleton<CoreGameManager>.Instance;
            if(core==null||core.GetPlayer(0)==null||baldi==null||baldi.looker==null)return false;
            if(contactTarget==null){contactTarget=new GameObject("Remote contact sight target").transform;contactTarget.SetParent(transform,false);}
            // A spectating host is on the non-interacting layer; use the player's native target layer instead.
            contactTarget.gameObject.layer=(int)AccessTools.Field(typeof(Entity),"targetLayer").GetValue(core.GetPlayer(0).plm.Entity);
            contactTarget.position=position;
            bool visible;baldi.looker.Raycast(contactTarget,Vector3.Distance(baldi.transform.position,position),out visible);return visible;
        }

        string DebugContacts()
        {
            var core=Singleton<CoreGameManager>.Instance;var player=core==null?null:core.GetPlayer(0);
            string local=player==null?"local player unavailable":"local layer="+player.gameObject.layer+" interactionDisabled="+player.plm.Entity.InteractionDisabled+
                " interactionLocks="+AccessTools.Field(typeof(Entity),"interactionDisables").GetValue(player.plm.Entity)+" frozen="+AccessTools.Field(typeof(Entity),"frozen").GetValue(player.plm.Entity)+
                " invincible="+player.invincible+" tagged="+player.Tagged+" invisible="+player.Invisible+" locker="+(localLocker!=null&&localLocker.playerInside);
            var manager=Singleton<BaseGameManager>.Instance;
            var lines=new List<string>{local,"host="+Hosting+" generation="+epoch+" worldReady="+worldReady+" deathSent="+deathSent};
            if(manager==null)return string.Join("\n",lines.ToArray());
            foreach(var baldi in manager.Ec.Npcs.OfType<Baldi>())
            {
                lines.Add("Baldi state="+baldi.behaviorStateMachine.CurrentState.GetType().Name+" layer="+baldi.gameObject.layer+" nativeSightMask="+(int)(LayerMask)AccessTools.Field(typeof(Looker),"layerMask").GetValue(baldi.looker));
                foreach(var peer in peers.Values.Where(p=>p.Accepted))
                {
                    var delta=peer.Position-baldi.transform.position;delta.y=0;
                    string blockers=string.Join(",",Physics.RaycastAll(baldi.transform.position,(peer.Position-baldi.transform.position).normalized,Vector3.Distance(baldi.transform.position,peer.Position),~0,QueryTriggerInteraction.Ignore)
                        .Where(h=>h.collider.GetComponentInParent<NPC>()==null&&h.collider.GetComponentInParent<PlayerManager>()==null).Take(8).Select(h=>h.collider.name+"[layer "+h.collider.gameObject.layer+"]").ToArray());
                    lines.Add("peer #"+peer.Id+" eliminated="+peer.Eliminated+" locker="+peer.InLocker+" ready="+peer.Ready+" pose="+peer.HasPose+" age="+(Time.realtimeSinceStartup-peer.PoseAt).ToString("0.00")+" distance="+delta.magnitude.ToString("0.00")+" nativeSight="+NativeRemoteContactVisible(baldi,peer.Position)+" allLayerBlockers="+blockers);
                }
            }
            return string.Join("\n",lines.ToArray());
        }

        bool contactLabStarted;
        void UpdateContactLab()
        {
            if(contactLabStarted||!Lab.Enabled||!TitleVisible||!Lab.HasArg("-bt-contact-tour"))return;
            contactLabStarted=true;StartCoroutine(TourContacts());
        }
        IEnumerator TourContacts()
        {
            var checks=new Dictionary<string,bool>();
            var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();portText=((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString();listener.Stop();
            Connect(true);AutoStartRun();
            for(int floor=1;floor<=2;floor++)
            {
                while(!worldReady)yield return null;
                var core=Singleton<CoreGameManager>.Instance;var manager=Singleton<BaseGameManager>.Instance;var ec=manager.Ec;var player=core.GetPlayer(0);
                var happy=Object.FindObjectOfType<HappyBaldi>();if(happy!=null)Object.Destroy(happy.gameObject);
                var prefab=Resources.FindObjectsOfTypeAll<Baldi>().First(n=>!n.gameObject.scene.IsValid());
                var cell=ec.CellFromPosition(player.transform.position);var baldi=(Baldi)ec.SpawnNPC(prefab,cell.position);
                baldi.behaviorStateMachine.ChangeState(new Baldi_Chase(baldi,baldi));baldi.Navigator.enabled=false;
                Vector3 origin=cell.FloorWorldPosition;origin.y=player.transform.position.y;
                baldi.Entity.Teleport(origin);player.Teleport(origin+Vector3.right*2);npcs=ec.Npcs.ToArray();
                var remote=new Peer{Id=3,Name="Contact survivor",Accepted=true,Ready=true,HasPose=true,Position=origin+Vector3.right,PoseAt=Time.realtimeSinceStartup};peers[3]=remote;
                peers[2]=new Peer{Id=2,Name="Other survivor",Accepted=true,Ready=true,HasPose=true,Position=origin+Vector3.right*100,PoseAt=Time.realtimeSinceStartup};
                var blocker=new GameObject("Contact mask regression collider");var box=blocker.AddComponent<BoxCollider>();box.size=Vector3.one*.1f;blocker.transform.position=origin+Vector3.right*.5f;
                int mask=(int)(LayerMask)AccessTools.Field(typeof(Looker),"layerMask").GetValue(baldi.looker);
                int excluded=Enumerable.Range(0,32).First(i=>(mask&(1<<i))==0);blocker.layer=excluded;Physics.SyncTransforms();
                checks["floor-"+floor+"-old-all-layer-false-block"]=!ClearWorldLine(origin,remote.Position);
                checks["floor-"+floor+"-native-mask-ignores-collider"]=NativeRemoteContactVisible(baldi,remote.Position);
                blocker.layer=Enumerable.Range(0,32).First(i=>(mask&(1<<i))!=0);Physics.SyncTransforms();
                checks["floor-"+floor+"-native-wall-blocks"]=!NativeRemoteContactVisible(baldi,remote.Position);
                blocker.layer=excluded;Physics.SyncTransforms();
                EliminatePeer(self,baldi);checks["floor-"+floor+"-host-spectating"]=LocalEliminated&&spectatorPlayer!=null;
                remote.InLocker=true;remote.PoseAt=Time.realtimeSinceStartup;CheckRemoteCatches();checks["floor-"+floor+"-locker-protected"]=!remote.Eliminated;
                remote.InLocker=false;remote.PoseAt=Time.realtimeSinceStartup;CheckRemoteCatches();checks["floor-"+floor+"-survivor-captured-without-locker-cycle"]=remote.Eliminated;
                Log("Contact lab floor="+floor+"\n"+DebugContacts());
                Object.Destroy(blocker);
                if(floor==1)
                {
                    var next=Resources.FindObjectsOfTypeAll<SceneObject>().First(s=>s.name=="MainLevel_2");Applying=true;
                    try{core.StopAllCoroutines();manager.StopAllCoroutines();ec.ResetEvents();ec.gameObject.SetActive(false);core.PrepareForReload();core.sceneObject=next;HostLoad(next);Singleton<AdditiveSceneManager>.Instance.LoadScene("Game");}finally{Applying=false;}
                }
            }
            string json="{\"passed\":"+checks.Count(p=>p.Value)+",\"failed\":"+checks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",checks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}";
            string path=Path.Combine(Lab.Workspace,"artifacts/tests/contact-results.json");Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,json);Log("LAB contacts "+json);
        }
    }
}

