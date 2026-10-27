using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        bool npcExpansionLabStarted;
        public void UpdateNpcExpansionLab()
        {
            if (!Lab.Enabled || npcExpansionLabStarted || !TitleVisible || !Lab.HasArg("-bt-npc-expansion-tour")) return;
            npcExpansionLabStarted=true; StartCoroutine(TourNpcExpansion());
        }

        byte[] ExpansionNpcSnapshot()
        {
            using(var stream=new MemoryStream())
            { using(var writer=new BinaryWriter(stream)) WriteNpcPresentation(writer); return stream.ToArray(); }
        }

        bool ApplyExpansionNpcSnapshot(byte[] data)
        {
            using(var stream=new MemoryStream(data))
            using(var reader=new BinaryReader(stream))
            { ReadNpcPresentation(reader,true); return stream.Position==stream.Length; }
        }

        IEnumerator TourNpcExpansion()
        {
            var checks=new Dictionary<string,bool>();
            portText="27035";Connect(true);AutoStartRun();
            while(!worldReady)yield return null;
            yield return new WaitForSecondsRealtime(2);
            var ec=Singleton<BaseGameManager>.Instance.Ec;
            var player=Singleton<CoreGameManager>.Instance.GetPlayer(0);
            foreach(var type in new[]{typeof(Bully),typeof(Cumulo),typeof(ChalkFace)})
                if(!ec.Npcs.Any(n=>n.GetType()==type))
                {
                    var prefab=Resources.FindObjectsOfTypeAll<NPC>().First(n=>n.GetType()==type&&!n.gameObject.scene.IsValid());
                    ec.SpawnNPC(prefab,ec.CellFromPosition(player.transform.position).position);
                }
            npcs=ec.Npcs.ToArray();
            var bully=ec.Npcs.OfType<Bully>().First();var cloud=ec.Npcs.OfType<Cumulo>().First();var chalk=ec.Npcs.OfType<ChalkFace>().First();
            checks["native-three-prefabs"]=bully!=null&&cloud!=null&&chalk!=null;
            var colliders=NpcField<Collider[]>(bully,"collidersToHide");
            var obstacle=NpcField<UnityEngine.AI.NavMeshObstacle>(bully,"obstacleToHide");
            var chalkSprite=NpcField<SpriteRenderer>(chalk,"chalkRenderer");
            var flying=NpcField<SpriteRenderer>(chalk,"flyingRenderer");
            var wind=NpcField<BeltManager>(cloud,"windManager");
            bully.SetComponents(true);chalkSprite.gameObject.SetActive(false);flying.gameObject.SetActive(true);
            wind.transform.position=player.transform.position;wind.BoxCollider.size=new Vector3(10,10,10);wind.SetDirection(Direction.North);wind.SetSpeed(12);wind.gameObject.SetActive(true);
            byte[] active=ExpansionNpcSnapshot();
            bully.SetComponents(false);chalkSprite.gameObject.SetActive(true);flying.gameObject.SetActive(false);wind.gameObject.SetActive(false);wind.SetSpeed(0);
            checks["presentation-record-consumed"]=ApplyExpansionNpcSnapshot(active);
            checks["bully-show-physical"]=bully.Entity.Visible&&colliders.All(c=>c==null||c.enabled)&&obstacle.enabled;
            checks["chalk-flying-child"]=!chalkSprite.gameObject.activeSelf&&flying.gameObject.activeSelf;
            checks["wind-active"]=wind.gameObject.activeSelf;
            checks["wind-region"]=Vector3.Distance(wind.transform.position,player.transform.position)<.1f&&wind.BoxCollider.size==new Vector3(10,10,10);
            checks["wind-direction-speed"]=NpcField<Direction>(wind,"dir")==Direction.North&&Mathf.Abs(NpcField<float>(wind,"setSpeed")-12)<.01f;
            yield return new WaitForFixedUpdate();yield return new WaitForFixedUpdate();
            var modifier=NpcField<MovementModifier>(wind,"moveMod");
            checks["wind-native-force"]=player.plm.am.moveMods.Contains(modifier);
            bully.SetComponents(false);chalkSprite.gameObject.SetActive(true);flying.gameObject.SetActive(false);wind.gameObject.SetActive(false);
            byte[] inactive=ExpansionNpcSnapshot();
            bully.SetComponents(true);chalkSprite.gameObject.SetActive(false);flying.gameObject.SetActive(true);wind.gameObject.SetActive(true);
            ApplyExpansionNpcSnapshot(inactive);
            checks["bully-hide-physical"]=!bully.Entity.Visible&&colliders.All(c=>c==null||!c.enabled)&&!obstacle.enabled;
            checks["chalk-chalk-child"]=chalkSprite.gameObject.activeSelf&&!flying.gameObject.activeSelf;
            checks["wind-disable-cleanup"]=!wind.gameObject.activeSelf&&!player.plm.am.moveMods.Contains(modifier);
            var remote=new Peer{Id=3,Accepted=true,Ready=true,HasPose=true,PoseAt=Time.realtimeSinceStartup,Position=bully.transform.position};
            peers.Add(3,remote);bully.SetComponents(true);bully.behaviorStateMachine.ChangeState(new Bully_Active(bully,bully,60));
            CheckBullySpawn(bully);
            checks["bully-remote-spawn-buffer"]=!bully.Entity.Visible&&bully.behaviorStateMachine.CurrentState is Bully_Wait;
            bool oldTagged=peers[self].Tagged;peers[self].Tagged=true;remote.Eliminated=true;
            checks["eliminated-remote-excluded"]=NearestVisiblePeer(bully)==null;
            remote.Eliminated=false;remote.InLocker=true;
            checks["locker-remote-excluded"]=NearestVisiblePeer(bully)==null;
            peers[self].Tagged=oldTagged;peers.Remove(3);
            string json="{\"passed\":"+checks.Count(p=>p.Value)+",\"failed\":"+checks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",checks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}";
            File.WriteAllText(Path.Combine(Lab.Workspace,"artifacts/tests/npc-expansion-results.json"),json);Log("LAB NPC expansion "+json);
        }
    }
}
