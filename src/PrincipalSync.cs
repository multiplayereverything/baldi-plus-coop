using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        sealed class RemoteRule { public string Rule; public float Sensitivity, At; public bool Visible; }
        sealed class RemoteDetention { public int Peer; public RoomController Room; public float Time, Total; public HudGauge Gauge; }
        readonly Dictionary<int,RemoteRule> principalRules = new Dictionary<int,RemoteRule>();
        readonly Dictionary<Principal,Dictionary<int,float>> principalSight = new Dictionary<Principal,Dictionary<int,float>>();
        readonly List<RemoteDetention> principalDetentions = new List<RemoteDetention>();
        float nextPrincipalReport;
        int principalRemoteChases, principalRemoteDetentions, principalDetentionCommands;
        static readonly string[] PrincipalRules = { "Running", "Faculty", "Drinking", "Eating", "Escaping", "Lockers", "AfterHours", "Bullying", "Stabbing" };

        void ResetPrincipalSync()
        {
            foreach (var d in principalDetentions) if (d.Gauge != null) d.Gauge.Deactivate();
            var rooms=principalDetentions.Select(d=>d.Room).Distinct().ToArray();
            principalRules.Clear(); principalSight.Clear(); principalDetentions.Clear(); nextPrincipalReport = 0;
            foreach(var room in rooms)UpdateRemoteDetentionClocks(room);
        }

        bool HandlePrincipalClient(int id, Peer sender, Op op, BinaryReader r)
        {
            if (op != Op.PrincipalState) return false;
            int generation = r.ReadInt32(); string rule = Wire.Text(r,16); float sensitivity = Wire.Number(r); bool visible = r.ReadBoolean();
            if (generation != epoch || !worldReady || !sender.Accepted || !sender.Ready) return true;
            float now = Time.realtimeSinceStartup; RemoteRule old;
            if (principalRules.TryGetValue(id,out old) && now-old.At < 0.08f) return true;
            if (rule != "" && !PrincipalRules.Contains(rule)) return true;
            principalRules[id] = new RemoteRule { Rule=rule, Sensitivity=Mathf.Clamp(sensitivity,0.1f,5f), Visible=visible, At=now };
            return true;
        }

        bool HandlePrincipalHost(Op op, BinaryReader r)
        {
            if (op != Op.PrincipalDetention) return false;
            int generation=r.ReadInt32(), culprit=r.ReadInt32(), office=r.ReadInt32(); float duration=Wire.Number(r); Vector3 destination=ReadVector(r);
            var manager=Singleton<BaseGameManager>.Instance;
            if (generation!=epoch || !worldReady || !readyAcknowledged || manager==null || office<0 || office>=manager.Ec.offices.Count || duration<1 || duration>99) return true;
            var room=manager.Ec.offices[office];
            if ((destination-manager.Ec.RealRoomMid(room)).sqrMagnitude>1) throw new IOException("Invalid detention destination.");
            BeginRemoteDetention(culprit,room,duration);
            principalDetentionCommands++;
            if (culprit==self)
            {
                var player=Singleton<CoreGameManager>.Instance.GetPlayer(0);
                player.ClearGuilt(); player.Teleport(destination);
            }
            return true;
        }

        void BeginRemoteDetention(int culprit, RoomController room, float duration)
        {
            foreach (var previous in principalDetentions.Where(d=>d.Peer==culprit).ToArray())
            { if(previous.Gauge!=null) previous.Gauge.Deactivate(); principalDetentions.Remove(previous); }
            var detention=new RemoteDetention { Peer=culprit, Room=room, Time=duration, Total=duration };
            var roomFunction=room.functionObject==null?null:room.functionObject.GetComponent<DetentionRoomFunction>();
            if (culprit==self && roomFunction!=null)
            {
                var sprite=(Sprite)AccessTools.Field(typeof(DetentionRoomFunction),"gaugeSprite").GetValue(roomFunction);
                detention.Gauge=Singleton<CoreGameManager>.Instance.GetHud(0).gaugeManager.ActivateNewGauge(sprite,duration);
            }
            principalDetentions.Add(detention);
            if (Hosting)
            {
                float lockTime=principalDetentions.Where(d=>d.Room==room).Max(d=>d.Time);
                if(roomFunction!=null && (bool)AccessTools.Field(typeof(DetentionRoomFunction),"active").GetValue(roomFunction))
                    lockTime=Mathf.Max(lockTime,(float)AccessTools.Field(typeof(DetentionRoomFunction),"time").GetValue(roomFunction));
                foreach (Door door in room.doors) { door.Shut(); door.LockTimed(lockTime/1.25f); }
            }
        }

        void UpdatePrincipalSync()
        {
            if (!Active || !worldReady) return;
            var manager=Singleton<BaseGameManager>.Instance; var core=Singleton<CoreGameManager>.Instance;
            if(manager==null || core==null || !manager.Ec.Active) return;
            float now=Time.realtimeSinceStartup;
            foreach(var d in principalDetentions.ToArray())
            {
                if(d.Room==null) { principalDetentions.Remove(d); continue; }
                d.Time=Mathf.Max(0,d.Time-Time.deltaTime*manager.Ec.EnvironmentTimeScale*1.25f);
                if(d.Gauge!=null) d.Gauge.SetValue(d.Total,d.Time);
                UpdateRemoteDetentionClocks(d.Room);
                if(d.Peer==self)
                {
                    var player=core.GetPlayer(0);
                    var cell=manager.Ec.CellFromPosition(player.transform.position);
                    if(d.Time>0) { player.ClearGuilt(); if(cell==null || cell.room!=d.Room) player.RuleBreak("Escaping",d.Time*0.8f,0.25f); }
                }
                if(d.Time<=0) { if(d.Gauge!=null)d.Gauge.Deactivate(); principalDetentions.Remove(d); }
            }
            if(!Hosting)
            {
                if(!readyAcknowledged || now<nextPrincipalReport)return;
                nextPrincipalReport=now+0.2f; var player=core.GetPlayer(0);
                string rule=!LocalEliminated && player.Disobeying && !player.Tagged && PrincipalRules.Contains(player.ruleBreak)?player.ruleBreak:"";
                Network.Send(0,Wire.Packet(Op.PrincipalState,w=> {w.Write(epoch);Wire.Text(w,rule);w.Write(Mathf.Clamp(player.GuiltySensitivity,0.1f,5));w.Write(!player.Invisible);}));
                return;
            }
            foreach(var d in principalDetentions)
            {
                Peer culprit; RemoteRule guilt;
                if(peers.TryGetValue(d.Peer,out culprit) && principalRules.TryGetValue(d.Peer,out guilt))
                {
                    var cell=manager.Ec.CellFromPosition(culprit.Position);
                    guilt.Rule=cell!=null && cell.room==d.Room?"":"Escaping";
                    guilt.Sensitivity=0.25f;
                }
            }
            foreach(var principal in manager.Ec.Npcs.OfType<Principal>())
            {
                var remote=principal.behaviorStateMachine.CurrentState as TogetherPrincipalChase;
                if(remote!=null) { UpdateRemotePrincipal(principal,remote); continue; }
                if(!(principal.behaviorStateMachine.CurrentState is Principal_Wandering) || principal.Blinded)continue;
                Dictionary<int,float> sight;
                if(!principalSight.TryGetValue(principal,out sight))principalSight[principal]=sight=new Dictionary<int,float>();
                foreach(var peer in peers.Values.Where(p=>p.Id!=self))
                {
                    RemoteRule rule; float elapsed; sight.TryGetValue(peer.Id,out elapsed);
                    if(!principalRules.TryGetValue(peer.Id,out rule) || !ValidPrincipalPeer(peer,rule,now) || rule.Rule=="" || !PrincipalSees(principal,peer.Position)
                        || manager.Ec.Npcs.OfType<Principal>().Any(p=>p.behaviorStateMachine.CurrentState is TogetherPrincipalChase && ((TogetherPrincipalChase)p.behaviorStateMachine.CurrentState).Peer==peer.Id)) { sight[peer.Id]=0;continue; }
                    elapsed+=Time.deltaTime*principal.TimeScale; sight[peer.Id]=elapsed;
                    if(elapsed<rule.Sensitivity)continue;
                    principal.behaviorStateMachine.ChangeState(new TogetherPrincipalChase(principal,peer.Id,peer.Position));
                    principal.Scold(rule.Rule); principalRemoteChases++; sight.Clear(); break;
                }
            }
        }

        bool ValidPrincipalPeer(Peer peer,RemoteRule rule,float now)
        { return peer.Accepted && peer.Ready && peer.HasPose && !peer.Eliminated && !peer.InLocker && !peer.Tagged && !peer.Invisible && now-peer.PoseAt<1.5f && now-rule.At<1.5f && rule.Visible; }

        void UpdateRemoteDetentionClocks(RoomController room)
        {
            if(room==null || room.functionObject==null)return;
            var function=room.functionObject.GetComponent<DetentionRoomFunction>();
            if(function==null || (bool)AccessTools.Field(typeof(DetentionRoomFunction),"active").GetValue(function))return;
            float remaining=principalDetentions.Where(d=>d.Room==room).Select(d=>d.Time).DefaultIfEmpty(0).Max();
            var clocks=(List<DigitalClock>)AccessTools.Field(typeof(DetentionRoomFunction),"clocks").GetValue(function);
            if(clocks==null)return;
            foreach(var clock in clocks) if(clock!=null){clock.UpdateDisplay(Mathf.CeilToInt(remaining));clock.SetColor(remaining>0?Color.red:Color.green);}
        }

        bool PrincipalSees(Principal principal,Vector3 position)
        {
            if(principal.Blinded || principal.Navigator.Entity.Blinded)return false;
            var delta=position-principal.transform.position;
            if(delta.magnitude>Mathf.Min(principal.looker.distance,principal.ec.MaxRaycast))return false;
            bool fov=(bool)AccessTools.Field(typeof(Looker),"hasFov").GetValue(principal.looker);
            float angle=(float)AccessTools.Field(typeof(Looker),"fieldOfView").GetValue(principal.looker);
            return (!fov || Vector3.Angle(principal.transform.forward,delta)<=angle) && ClearWorldLine(principal.transform.position,position);
        }

        void UpdateRemotePrincipal(Principal principal,TogetherPrincipalChase chase)
        {
            Peer peer; RemoteRule rule; float now=Time.realtimeSinceStartup;
            if(!peers.TryGetValue(chase.Peer,out peer) || !principalRules.TryGetValue(chase.Peer,out rule) || !ValidPrincipalPeer(peer,rule,now))
            { principal.behaviorStateMachine.ChangeState(new Principal_Wandering(principal)); return; }
            bool sees=PrincipalSees(principal,peer.Position);
            if(sees) { chase.SeenAt=now; if(now>=chase.NextPath) { chase.Target.UpdatePosition(peer.Position);chase.NextPath=now+0.2f; } }
            if(!sees && now-chase.SeenAt>8) {principal.behaviorStateMachine.ChangeState(new Principal_Wandering(principal));return;}
            if(!sees || (peer.Position-principal.transform.position).sqrMagnitude>9 || principal.ec.offices.Count==0)return;
            int office=UnityEngine.Random.Range(0,principal.ec.offices.Count);
            var room=principal.ec.offices[office]; var destination=principal.ec.RealRoomMid(room);
            int level=(int)AccessTools.Field(typeof(Principal),"detentionLevel").GetValue(principal);
            var times=(SoundObject[])AccessTools.Field(typeof(Principal),"audTimes").GetValue(principal);
            float duration=level>=times.Length-1?99: (float)AccessTools.Field(typeof(Principal),"detentionInit").GetValue(principal)+(float)AccessTools.Field(typeof(Principal),"detentionInc").GetValue(principal)*level;
            duration=Mathf.Clamp(duration,1,99);
            BeginRemoteDetention(peer.Id,room,duration);
            SendAll(Wire.Packet(Op.PrincipalDetention,w=>{w.Write(epoch);w.Write(peer.Id);w.Write(office);w.Write(duration);WriteVector(w,destination);}));
            peer.Position=destination; rule.Rule="";
            principal.Navigator.Entity.Teleport(destination+Vector3.forward*10);
            AccessTools.Field(typeof(Principal),"detentionLevel").SetValue(principal,Mathf.Min(level+1,Mathf.Max(0,times.Length-1)));
            var audio=(AudioManager)AccessTools.Field(typeof(Principal),"audMan").GetValue(principal);
            if(times.Length>0)audio.QueueAudio(times[Mathf.Min(level,times.Length-1)]);
            audio.QueueAudio((SoundObject)AccessTools.Field(typeof(Principal),"audDetention").GetValue(principal));
            var scolds=(SoundObject[])AccessTools.Field(typeof(Principal),"audScolds").GetValue(principal);
            if(scolds.Length>0)audio.QueueAudio(scolds[UnityEngine.Random.Range(0,scolds.Length)]);
            var baldi=principal.ec.GetBaldi();
            if(baldi!=null)baldi.ClearSoundLocations(true,destination);
            principal.ec.MakeNoise(destination,(int)AccessTools.Field(typeof(Principal),"detentionNoise").GetValue(principal));
            principal.behaviorStateMachine.ChangeState(new Principal_Detention(principal,3)); principalRemoteDetentions++;
        }

        string PrincipalDiagnostic()
        {return ",\"principalRemoteChases\":"+principalRemoteChases+",\"principalRemoteDetentions\":"+principalRemoteDetentions+",\"principalDetentionCommands\":"+principalDetentionCommands+",\"detentionRemaining\":"+(principalDetentions.Count==0?0:principalDetentions.Max(d=>d.Time)).ToString(System.Globalization.CultureInfo.InvariantCulture);}

        void WritePrincipalWorld(BinaryWriter writer)
        {
            var ec = Singleton<BaseGameManager>.Instance.Ec;
            var entries = principalDetentions.ToList();
            foreach (var room in ec.offices)
            {
                var function = room.functionObject == null ? null : room.functionObject.GetComponent<DetentionRoomFunction>();
                if (function == null || !(bool)AccessTools.Field(typeof(DetentionRoomFunction),"active").GetValue(function)) continue;
                entries.Add(new RemoteDetention { Peer=self, Room=room, Time=NpcField<float>(function,"time"), Total=NpcField<float>(function,"totalTime") });
            }
            if (entries.Count > 16) throw new IOException("Detention state limit exceeded.");
            writer.Write((byte)entries.Count);
            foreach (var d in entries) { writer.Write(d.Peer); writer.Write(ec.offices.IndexOf(d.Room)); writer.Write(d.Time); writer.Write(d.Total); }
        }

        void ReadPrincipalWorld(BinaryReader reader, bool apply)
        {
            int count = reader.ReadByte(); if (count > 16) throw new IOException("Detention state limit exceeded.");
            var ec = Singleton<BaseGameManager>.Instance == null ? null : Singleton<BaseGameManager>.Instance.Ec;
            var present = new HashSet<int>();
            for (int i=0;i<count;i++)
            {
                int peer=reader.ReadInt32(), office=reader.ReadInt32(); float remaining=Wire.Number(reader), total=Wire.Number(reader);
                if (peer<0 || peer>3 || remaining<0 || total<1 || total>99 || remaining>total) throw new IOException("Invalid detention state.");
                if (!apply || ec==null || office<0 || office>=ec.offices.Count) continue;
                present.Add(peer); var room=ec.offices[office];
                var current=principalDetentions.FirstOrDefault(d=>d.Peer==peer && d.Room==room);
                if (current==null) { BeginRemoteDetention(peer,room,total); current=principalDetentions.First(d=>d.Peer==peer); }
                current.Time=remaining; current.Total=total;
            }
            if (apply)
                foreach (var d in principalDetentions.Where(d=>!present.Contains(d.Peer)).ToArray())
                { if (d.Gauge!=null) d.Gauge.Deactivate(); principalDetentions.Remove(d); UpdateRemoteDetentionClocks(d.Room); }
        }

        public void ShareNativeDetention(Principal principal)
        {
            if(!Hosting || !worldReady || principal==null)return;
            var player=(PlayerManager)AccessTools.Field(typeof(Principal),"targetedPlayer").GetValue(principal);
            var core=Singleton<CoreGameManager>.Instance;
            if(player==null || core==null || player!=core.GetPlayer(0))return;
            // Native detention may leave the offender outside the room on invalid collision.
            // The Principal is teleported to the selected office in either native branch.
            for(int i=0;i<principal.ec.offices.Count;i++)
            {
                var room=principal.ec.offices[i]; var cell=principal.ec.CellFromPosition(principal.transform.position);
                if(cell==null || cell.room!=room)continue;
                var function=room.functionObject==null?null:room.functionObject.GetComponent<DetentionRoomFunction>();
                if(function==null)return;
                if(!(bool)AccessTools.Field(typeof(DetentionRoomFunction),"active").GetValue(function))return;
                float duration=(float)AccessTools.Field(typeof(DetentionRoomFunction),"time").GetValue(function);
                int office=i;var destination=principal.ec.RealRoomMid(room);
                SendAll(Wire.Packet(Op.PrincipalDetention,w=>{w.Write(epoch);w.Write(self);w.Write(office);w.Write(Mathf.Clamp(duration,1,99));WriteVector(w,destination);}));
                return;
            }
        }
    }

    sealed class TogetherPrincipalChase : Principal_StateBase
    {
        public readonly int Peer;
        public NavigationState_TargetPlayer Target;
        public float SeenAt=Time.realtimeSinceStartup,NextPath;
        readonly Vector3 initial;
        public TogetherPrincipalChase(Principal principal,int peer,Vector3 position):base(principal){Peer=peer;initial=position;}
        public override void Enter(){base.Enter();Target=new NavigationState_TargetPlayer(principal,63,initial,true);ChangeNavigationState(Target);principal.SwitchToChaseSprite();}
        public override void DestinationEmpty() { }
        // holdPriority keeps 63 until released; native DrReflex_Chasing does the same on exit.
        public override void Exit(){base.Exit();if(Target!=null)Target.priority=0;}
    }

    [HarmonyPatch(typeof(Principal),"SendToDetention")]
    static class PrincipalDetentionSyncPatch
    { static void Postfix(Principal __instance) { var runtime=ModHooks.Current;if(runtime!=null)runtime.ShareNativeDetention(__instance); } }
}
