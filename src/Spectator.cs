using System;
using System.IO;
using System.Linq;
using UnityEngine;
using Object=UnityEngine.Object;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        PlayerManager spectatorPlayer;
        Transform spectatorAnchor;
        bool spectatorMovementEnabled,spectatorClickEnabled,spectatorBlank;
        int spectatorTarget=-1;
        int labSpectatorStep;
        bool spectatorMatchRotation;
        GameObject spectatorHud;
        bool spectatorHudVisible;
        Vector3 spectatorCameraOffset;
        static readonly Renderer[] NoRenderers=new Renderer[0];
        Renderer[] watchedRenderers=NoRenderers;
        GameObject watchedAvatar;
        bool spectatorLabStarted;
        void UpdateSpectatorLab()
        {
            UpdateSpectatorUiLab();
            if(!Lab.Enabled||!TitleVisible||spectatorLabStarted||!Lab.HasArg("-bt-spectator-tour"))return;
            spectatorLabStarted=true;StartCoroutine(TourSpectator());
        }

        System.Collections.IEnumerator TourSpectator()
        {
            var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();portText=((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString();listener.Stop();
            Connect(true);if(!Active)yield break;AutoStartRun();while(!worldReady)yield return null;
            var core=Singleton<CoreGameManager>.Instance;var player=core.GetPlayer(0);var manager=Singleton<BaseGameManager>.Instance;
            if(manager.Ec.Npcs.Count==0)manager.Ec.SpawnNPCs();npcs=manager.Ec.Npcs.ToArray();
            var baldi=npcs.OfType<Baldi>().First();baldi.Navigator.enabled=false;
            // The level spawn point is always walkable; a fixed offset can land inside a wall on random seeds.
            var teammate=new Peer{Id=3,Name="Survivor",Accepted=true,Ready=true,HasPose=true,Position=manager.Ec.spawnPoint,PoseAt=Time.realtimeSinceStartup};
            var caught=new Peer{Id=2,Name="Caught teammate",Accepted=true,Ready=true,HasPose=true,Eliminated=true,Position=player.transform.position+Vector3.right*30,PoseAt=Time.realtimeSinceStartup};
            peers.Add(3,teammate);peers.Add(2,caught);UpdateAvatars();
            core.GetCamera(0).SetControllable(false);
            baldi.CaughtPlayer(player);
            CheckNativeUi("native-capture-keeps-chase",baldi.behaviorStateMachine.CurrentState is Baldi_Chase);
            CheckNativeUi("individual-capture",LocalEliminated&&!deathSent&&Time.timeScale>0);
            CheckNativeUi("spectator-interactions-disabled",!player.plm.enabled&&!player.pc.enabled&&player.Tagged);
            UpdateSpectator();
            CheckNativeUi("teammate-only-target",spectatorTarget==3&&spectatorAnchor.position==teammate.Position);
            CheckNativeUi("fixed-camera",!core.GetCamera(0).Controllable&&core.GetCamera(0).targets[0]==spectatorAnchor);
            baldi.Navigator.enabled=true;
            float distanceBefore=baldi.totalDistance;
            // Baldi moves in slaps on an anger-based timer; wait for the first slap rather than a fixed time.
            float until=Time.realtimeSinceStartup+8;
            while(Time.realtimeSinceStartup<until&&baldi.totalDistance<=distanceBefore+.1f)
            {teammate.PoseAt=Time.realtimeSinceStartup;yield return null;}
            CheckNativeUi("chase-retargets-survivor",nearestBaldiPeer==3&&baldi.behaviorStateMachine.CurrentState is Baldi_Chase);
            var navState=baldi.behaviorStateMachine.CurrentNavigationState;
            Log("LAB spectator movement: ecActive="+manager.Ec.Active+" timeScale="+Time.timeScale+" navEnabled="+baldi.Navigator.enabled+" speed="+baldi.Navigator.speed+" maxSpeed="+baldi.Navigator.maxSpeed
                +" state="+baldi.behaviorStateMachine.CurrentState.GetType().Name+" nav="+(navState==null?"none":navState.GetType().Name+"@"+navState.destination)+" baldi="+baldi.transform.position+" target="+teammate.Position
                +" distance="+(baldi.totalDistance-distanceBefore)+" hasPath="+baldi.Navigator.HasDestination+" slapDelay="+baldi.Delay);
            CheckNativeUi("baldi-keeps-moving",baldi.totalDistance>distanceBefore+.1f);
            manager.EndGame(player.transform,baldi);
            CheckNativeUi("manager-capture-keeps-round",!deathSent&&Time.timeScale>0&&baldi.behaviorStateMachine.CurrentState is Baldi_Chase);
            CheckNativeUi("dead-sprite-hidden",caught.Avatar==null||!caught.Avatar.activeSelf);
            CheckNativeUi("dead-actions-denied",!Request(null,0)&&!BeforePickup(null)&&!BeforeShopClicked(null));
            teammate.InLocker=true;UpdateSpectator();CheckNativeUi("locker-view-concealed",spectatorBlank&&spectatorTarget==3);
            teammate.InLocker=false;teammate.HasPose=false;UpdateSpectator();CheckNativeUi("no-free-camera",spectatorBlank&&spectatorTarget==-1);
            using(var reader=new BinaryReader(new MemoryStream(Wire.Packet(Op.RoundState,w=>{w.Write(epoch);w.Write((byte)1);w.Write(self);w.Write(false);})))){reader.ReadByte();HandleRoundHost(Op.RoundState,reader);}
            CheckNativeUi("no-mid-round-revive",LocalEliminated);
            ResetWorld();CheckNativeUi("new-round-restores-player",!LocalEliminated&&player.plm.enabled&&player.pc.enabled&&spectatorPlayer==null);
            bool externalLockPreserved=!core.GetCamera(0).Controllable;core.GetCamera(0).SetControllable(true);
            CheckNativeUi("camera-lock-balanced",externalLockPreserved&&core.GetCamera(0).Controllable);
            worldReady=true;teammate.HasPose=true;teammate.PoseAt=Time.realtimeSinceStartup;caught.Eliminated=true;
            EliminatePeer(self,baldi);EliminatePeer(3,baldi);
            CheckNativeUi("team-wipe-native-ending",deathSent&&Time.timeScale==0);
            string json="{\"passed\":"+nativeUiChecks.Count(p=>p.Value)+",\"failed\":"+nativeUiChecks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",nativeUiChecks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}";
            File.WriteAllText(Path.Combine(Lab.Workspace,"artifacts/tests/spectator-results.json"),json);Log("LAB spectator "+json);
        }
        public bool LocalEliminated {get{Peer peer;return peers.TryGetValue(self,out peer)&&peer.Eliminated;}}

        void ResetSpectator(bool clearDeaths)
        {
            ReleaseSpectator();spectatorTarget=-1;spectatorBlank=false;
            if(clearDeaths)foreach(var peer in peers.Values)peer.Eliminated=false;
        }

        void ReleaseSpectator()
        {
            UnhideWatchedAvatar();
            bool restoreHud=spectatorHud!=null;
            if(spectatorHud!=null)spectatorHud.SetActive(spectatorHudVisible);spectatorHud=null;
            if(spectatorPlayer!=null)
            {
                if(restoreHud)RestoreLocalHud(spectatorPlayer);
                spectatorPlayer.SetNametag(false);spectatorPlayer.SetHidden(false);
                spectatorPlayer.plm.Entity.SetFrozen(false);spectatorPlayer.plm.Entity.SetInteractionState(true);
                spectatorPlayer.plm.enabled=spectatorMovementEnabled;spectatorPlayer.pc.enabled=spectatorClickEnabled;
                var core=Singleton<CoreGameManager>.Instance;
                if(core!=null&&core.GetCamera(0)!=null)
                {
                    var camera=core.GetCamera(0);camera.UpdateTargets(null,0);camera.offestPos=spectatorCameraOffset;
                    camera.matchTargetRotation=spectatorMatchRotation;camera.SetControllable(true);
                }
                spectatorPlayer=null;
            }
            if(spectatorAnchor!=null)Object.Destroy(spectatorAnchor.gameObject);spectatorAnchor=null;
            DestroySpectatorUi();
        }

        void EnterSpectator()
        {
            var core=Singleton<CoreGameManager>.Instance;
            if(core==null||core.GetPlayer(0)==null)return;
            var player=core.GetPlayer(0);
            if(spectatorPlayer==player)return;
            ReleaseSpectator();
            if(localLocker!=null)localLocker.ForceOpen();
            spectatorPlayer=player;spectatorMovementEnabled=player.plm.enabled;spectatorClickEnabled=player.pc.enabled;
            player.SetNametag(true);player.SetHidden(true);player.plm.Entity.SetFrozen(true);player.plm.Entity.SetInteractionState(false);
            player.plm.enabled=false;player.pc.enabled=false;player.ClearGuilt();
            spectatorHud=core.GetHud(0).gameObject;spectatorHudVisible=spectatorHud.activeSelf;spectatorHud.SetActive(false);appliedHudSignature=null;
            var camera=core.GetCamera(0);spectatorMatchRotation=camera.matchTargetRotation;spectatorCameraOffset=camera.offestPos;
            spectatorAnchor=new GameObject("Teammate spectator viewpoint").transform;spectatorAnchor.SetParent(transform,false);
            camera.SetControllable(false);camera.matchTargetRotation=true;camera.offestPos=Vector3.zero;camera.UpdateTargets(spectatorAnchor,0);
            Notify("Caught! Spectate teammates until the next round.");
        }

        void SendRoundState()
        {
            if(!Hosting)return;
            SendAll(Wire.Packet(Op.RoundState,w=>{
                w.Write(epoch);var roster=peers.Values.Where(p=>p.Accepted).OrderBy(p=>p.Id).ToArray();w.Write((byte)roster.Length);
                foreach(var peer in roster){w.Write(peer.Id);w.Write(peer.Eliminated);}
            }));
        }

        bool HandleRoundHost(Op op,BinaryReader reader)
        {
            if(op!=Op.RoundState)return false;
            int generation=reader.ReadInt32(),count=reader.ReadByte();if(count<1||count>4)throw new IOException("Invalid round roster.");
            var seen=new System.Collections.Generic.HashSet<int>();
            for(int i=0;i<count;i++)
            {
                int id=reader.ReadInt32();bool eliminated=reader.ReadBoolean();if(id<0||id>3||!seen.Add(id))throw new IOException("Invalid round identity.");
                Peer peer;if(generation==epoch&&peers.TryGetValue(id,out peer)&&eliminated&&!peer.Eliminated){peer.Eliminated=true;if(worldReady&&!deathSent)PlayTeamSound("caught");}
            }
            if(LocalEliminated&&worldReady&&!deathSent)EnterSpectator();return true;
        }

        public bool BeforeMultiplayerDeath(Baldi baldi)
        {
            if(!Active||!Running||Applying)return true;
            if(NpcTestRunning)return false;
            if(Hosting)EliminatePeer(self,baldi);
            return false;
        }

        void EliminatePeer(int id,Baldi baldi)
        {
            Peer peer;if(!Hosting||deathSent||!peers.TryGetValue(id,out peer)||!peer.Accepted||peer.Eliminated)return;
            peer.Eliminated=true;peer.InLocker=false;
            if(peer.Avatar!=null)peer.Avatar.SetActive(false);
            if(id==self)EnterSpectator();
            Tell(peer.Name+" was caught.");PlayTeamSound("caught");SendRoundState();CheckTeamWipe(baldi);
        }

        void CheckTeamWipe(Baldi baldi)
        {
            if(!Hosting||!worldReady||deathSent||!peers.Values.Any(p=>p.Accepted)||peers.Values.Any(p=>p.Accepted&&!p.Eliminated))return;
            if(baldi==null)baldi=npcs.OfType<Baldi>().FirstOrDefault();if(baldi==null)return;
            deathSent=true;int index=Array.IndexOf(npcs,baldi);
            if(index>=0)SendAll(Wire.Packet(Op.End,w=>{w.Write(epoch);w.Write(index);}));
            ReleaseSpectator();Applying=true;
            try{var core=Singleton<CoreGameManager>.Instance;core.EndGame(core.GetPlayer(0).transform,baldi);}
            finally{Applying=false;}
        }

        void UpdateSpectator()
        {
            UpdateSpectatorTarget();
            UpdateSpectatorUi();
        }

        void UpdateSpectatorTarget()
        {
            if(!LocalEliminated||!worldReady||deathSent)return;
            EnterSpectator();if(spectatorPlayer==null)return;
            var alive=peers.Values.Where(p=>p.Id!=self&&p.Accepted&&!p.Eliminated&&p.HasPose&&(Hosting?p.Ready:true)&&Time.realtimeSinceStartup-p.PoseAt<3).OrderBy(p=>p.Id).ToArray();
            int index=Array.FindIndex(alive,p=>p.Id==spectatorTarget);
            if(index<0)index=0;
            spectatorAliveCount=alive.Length;
            if(!show&&!DebugConsoleOpen&&alive.Length>0)
            {
                if(Input.GetKeyDown(KeyCode.RightArrow)||Input.GetKeyDown(KeyCode.Tab))index=(index+1)%alive.Length;
                if(Input.GetKeyDown(KeyCode.LeftArrow))index=(index+alive.Length-1)%alive.Length;
                // Lab tours cannot press keys; they request one step instead.
                if(labSpectatorStep!=0){index=((index+labSpectatorStep)%alive.Length+alive.Length)%alive.Length;labSpectatorStep=0;}
            }
            var target=alive.Length==0?null:alive[index];
            if(target!=null&&spectatorTarget>=0&&target.Id!=spectatorTarget)PlayTeamSound("switch");
            spectatorTarget=target==null?-1:target.Id;
            spectatorBlank=target==null||target.InLocker;
            // The watched teammate's own sprite would block the view; hide it, re-scanning only when the avatar changes.
            GameObject avatar=target==null?null:target.Avatar;
            if((object)avatar!=(object)watchedAvatar)
            {
                UnhideWatchedAvatar();
                if(avatar!=null){watchedAvatar=avatar;watchedRenderers=avatar.GetComponentsInChildren<Renderer>(true);foreach(var renderer in watchedRenderers)renderer.forceRenderingOff=true;}
            }
            ApplySpectatorHud(target);
            if(target==null)return;
            spectatorAnchor.position=target.Position;spectatorAnchor.rotation=Quaternion.Euler(0,target.Yaw,0);
            // Keep the camera locked to the selected teammate; no free-flight or enemy targets.
            Singleton<CoreGameManager>.Instance.GetCamera(0).UpdateTargets(spectatorAnchor,0);
        }

        void UnhideWatchedAvatar()
        {
            foreach(var renderer in watchedRenderers)if(renderer!=null)renderer.forceRenderingOff=false;
            watchedRenderers=NoRenderers;watchedAvatar=null;
        }

        // The overlay in SpectatorUi.cs draws the spectator screen. IMGUI is only a fallback for a missing game font.
        void DrawSpectator()
        {
            if(spectatorUiState!="fallback"||!LocalEliminated||!worldReady||deathSent)return;
            if(spectatorBlank){var saved=GUI.color;GUI.color=new Color(0,0,0,.86f);GUI.DrawTexture(new Rect(0,0,Screen.width,Screen.height),Texture2D.whiteTexture);GUI.color=saved;}
            Peer target;string label=peers.TryGetValue(spectatorTarget,out target)?"SPECTATING "+target.Name+(target.InLocker?" - hiding":""):"WAITING FOR A TEAMMATE";
            GUI.Box(new Rect(Screen.width/2-240,Screen.height-80,480,65),label+"\nLeft / Right or Tab: switch teammate - No respawn until next round");
        }
    }
}
