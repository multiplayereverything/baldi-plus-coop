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
        readonly HashSet<EnvironmentController> countdownReleased=new HashSet<EnvironmentController>();
        static readonly System.Reflection.FieldInfo spawnTiles=AccessTools.Field(typeof(EnvironmentController),"npcSpawnTiles");
        static readonly System.Reflection.FieldInfo spawnRadius=AccessTools.Field(typeof(EnvironmentController),"npcSpawnBufferRadius");
        static readonly System.Reflection.FieldInfo spawnWidth=AccessTools.Field(typeof(EnvironmentController),"npcSpawnBufferWidth");

        bool SpawnCellSafe(EnvironmentController ec,Vector3 position)
        {
            float radius=(float)spawnRadius.GetValue(ec),half=(float)spawnWidth.GetValue(ec)/2;
            var core=Singleton<CoreGameManager>.Instance;var host=core==null?null:core.GetPlayer(0);
            if(host!=null&&!LocalEliminated&&!(localLocker!=null&&localLocker.playerInside))
            {var p=host.transform.position;if(!(Vector3.Distance(position,p)>radius&&(position.x>p.x+half||position.x<p.x-half)&&(position.z>p.z+half||position.z<p.z-half)))return false;}
            foreach(var peer in peers.Values.Where(p=>p.Id!=self&&p.Accepted&&p.Ready&&p.HasPose&&!p.Eliminated&&!p.InLocker&&Time.realtimeSinceStartup-p.PoseAt<2))
            {
                Vector3 player=peer.Position;
                // Retain the native radius and both-axis buffer for each exposed teammate.
                if(!(Vector3.Distance(position,player)>radius&&(position.x>player.x+half||position.x<player.x-half)&&(position.z>player.z+half||position.z<player.z-half)))return false;
            }
            return true;
        }

        public bool UsesSharedSpawner(EnvironmentController ec)
        {var manager=Singleton<BaseGameManager>.Instance;return Hosting&&Running&&manager!=null&&manager.Ec==ec;}

        public IEnumerator SharedNpcSpawner(EnvironmentController ec)
        {
            int generation=epoch;
            var tiles=(List<Cell>)spawnTiles.GetValue(ec);
            while(Hosting&&generation==epoch&&ec!=null&&ec.npcsLeftToSpawn.Count>0)
            {
                var core=Singleton<CoreGameManager>.Instance;
                if(core!=null&&core.GetPlayer(0)!=null)
                {
                    for(int i=0;i<ec.npcsLeftToSpawn.Count;i++)
                    {
                        var prefab=ec.npcsLeftToSpawn[i];
                        if(prefab.IgnorePlayerOnSpawn||SpawnCellSafe(ec,ec.CellFromPosition(tiles[i].position).FloorWorldPosition))
                        {
                            ec.SpawnNPC(prefab,tiles[i].position);
                            if(prefab.Character==Character.Baldi&&ec.angerOnSpawn)Singleton<BaseGameManager>.Instance.AngerBaldi(Singleton<BaseGameManager>.Instance.NotebookAngerVal);
                            ec.npcsLeftToSpawn.RemoveAt(i);tiles.RemoveAt(i);i--;
                        }
                    }
                }
                yield return null;
            }
            if(ec!=null&&generation==epoch)Log("NPC spawn queue completed generation="+epoch+" NPCs="+ec.Npcs.Count);
        }

        bool countdownLabStarted;
        void UpdateCountdownLab()
        {
            if(countdownLabStarted||!Lab.Enabled||!TitleVisible||!Lab.HasArg("-bt-locker-countdown-tour"))return;
            countdownLabStarted=true;StartCoroutine(TourLockerCountdown());
        }

        IEnumerator TourLockerCountdown()
        {
            var checks=new Dictionary<string,bool>();
            var reserve=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);reserve.Start();portText=((System.Net.IPEndPoint)reserve.LocalEndpoint).Port.ToString();reserve.Stop();
            Connect(true);AutoStartRun();
            for(int floor=1;floor<=2;floor++)
            {
                while(!worldReady)yield return null;
                var manager=Singleton<BaseGameManager>.Instance;var ec=manager.Ec;var core=Singleton<CoreGameManager>.Instance;
                var locker=Object.FindObjectOfType<HideableLocker>();var happy=Object.FindObjectOfType<HappyBaldi>();
                checks["floor-"+floor+"-native-objects"]=locker!=null&&happy!=null;
                if(locker==null||happy==null)break;
                // Guarantee the original stationary-host buffer would hold the queue.
                var cell=ec.CellFromPosition(core.GetPlayer(0).transform.position);
                for(int i=0;i<ec.npcSpawnTile.Length;i++)ec.npcSpawnTile[i]=cell;
                var probe=new Peer{Id=3,Accepted=true,Ready=true,HasPose=true,Position=cell.FloorWorldPosition,PoseAt=Time.realtimeSinceStartup};peers.Add(3,probe);
                checks["floor-"+floor+"-exposed-player-buffer"]=!SpawnCellSafe(ec,cell.FloorWorldPosition);peers.Remove(3);
                locker.Clicked(0);yield return null;SendPose();
                checks["floor-"+floor+"-locker-active"]=locker.playerInside&&peers[self].InLocker;
                happy.Activate();float deadline=Time.realtimeSinceStartup+45;
                while(Time.realtimeSinceStartup<deadline&&(!hidingFinished||ec.npcsLeftToSpawn.Count>0))yield return null;
                checks["floor-"+floor+"-released"]=hidingFinished&&hidingStep==-2;
                checks["floor-"+floor+"-npcs-spawned"]=ec.Npcs.Count>0&&ec.npcsLeftToSpawn.Count==0;
                checks["floor-"+floor+"-still-hiding"]=locker.playerInside;
                locker.ForceOpen();yield return null;yield return null;
                if(floor==1)
                {
                    var next=Resources.FindObjectsOfTypeAll<SceneObject>().First(s=>s.name=="MainLevel_2");
                    Applying=true;
                    try{core.StopAllCoroutines();manager.StopAllCoroutines();ec.ResetEvents();ec.gameObject.SetActive(false);core.PrepareForReload();core.sceneObject=next;HostLoad(next);Singleton<AdditiveSceneManager>.Instance.LoadScene("Game");}
                    finally{Applying=false;}
                }
            }
            string json="{\"passed\":"+checks.Count(p=>p.Value)+",\"failed\":"+checks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",checks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}";
            File.WriteAllText(Path.Combine(Root,"MultiplayerMod/artifacts/tests/countdown-locker-results.json"),json);Log("LAB locker countdown "+json);
        }
    }

    [HarmonyPatch(typeof(EnvironmentController),"SpawnNPCs")]
    static class MultiplayerSpawnBufferPatch
    {
        static bool Prefix(EnvironmentController __instance)
        {
            var runtime=ModHooks.Current;if(runtime==null||!runtime.UsesSharedSpawner(__instance))return true;
            // Hook the public entry: Mono may inline the tiny native iterator factory.
            var tiles=(List<Cell>)AccessTools.Field(typeof(EnvironmentController),"npcSpawnTiles").GetValue(__instance);
            tiles.Clear();tiles.AddRange(__instance.npcSpawnTile);
            __instance.npcsLeftToSpawn.Clear();__instance.npcsLeftToSpawn.AddRange(__instance.npcsToSpawn);
            __instance.StartCoroutine(runtime.SharedNpcSpawner(__instance));return false;
        }
    }
}
