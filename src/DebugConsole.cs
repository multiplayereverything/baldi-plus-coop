using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        const int DebugCapacity=400, DebugLineLimit=1800;
        readonly object debugGate=new object();
        readonly Queue<string> debugLines=new Queue<string>();
        bool debugOpen,debugInitialized,debugLabDone,debugFocusPending;
        CursorLockMode debugPreviousLock;
        bool debugPreviousVisible;
        int debugConsumedFrame=-1;
        string debugCommand="",debugSnapshot="";
        float debugNextSnapshot;
        Vector2 debugScroll;
        public bool DebugConsoleOpen {get{return debugOpen;}}
        public bool DebugConsoleConsumedKeyFrame {get{return debugConsumedFrame==Time.frameCount;}}

        void InitializeDebugConsole()
        {
            if(debugInitialized)return;
            debugInitialized=true;
            Application.logMessageReceivedThreaded+=ReceiveDebugLog;
            CaptureDebugMessage("F2 debug console ready. Type help for commands.");
        }
        void ReceiveDebugLog(string message,string stack,LogType type)
        {
            if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert)
                message+="\n"+stack;
            AppendDebugLine(type.ToString(),message);
        }
        void CaptureDebugMessage(string message){AppendDebugLine("MOD",message);}
        void AppendDebugLine(string kind,string message)
        {
            // This callback also runs on worker threads: no Unity API calls here.
            string value=message??"";
            if(value.Length>DebugLineLimit)value=value.Substring(0,DebugLineLimit)+"...";
            string line=DateTime.UtcNow.ToString("HH:mm:ss.fff")+" UTC ["+kind+"] "+value;
            lock(debugGate){debugLines.Enqueue(line);while(debugLines.Count>DebugCapacity)debugLines.Dequeue();}
        }
        string[] debugFrameLines;
        string[] DebugLines(){lock(debugGate)return debugLines.ToArray();}
        void SetDebugConsole(bool open)
        {
            if(debugOpen==open)return;
            debugOpen=open;
            if(open)
            {
                debugPreviousLock=Cursor.lockState;debugPreviousVisible=Cursor.visible;
                Cursor.lockState=CursorLockMode.None;Cursor.visible=true;
                SetMenuInput(true);debugNextSnapshot=0;debugFocusPending=true;
            }
            else
            {
                SetMenuInput(show);Cursor.lockState=debugPreviousLock;Cursor.visible=debugPreviousVisible;
                debugCommand="";
            }
        }
        void UpdateDebugConsole()
        {
            if(Input.GetKeyDown(KeyCode.F2)){SetDebugConsole(!debugOpen);debugConsumedFrame=Time.frameCount;}
            else if(debugOpen&&Input.GetKeyDown(KeyCode.Escape)){SetDebugConsole(false);debugConsumedFrame=Time.frameCount;}
            if(debugOpen)
            {
                Cursor.lockState=CursorLockMode.None;Cursor.visible=true;
                if(Time.realtimeSinceStartup>=debugNextSnapshot){debugNextSnapshot=Time.realtimeSinceStartup+.5f;try{debugSnapshot=DebugStatus();}catch(Exception ex){debugSnapshot="status failed: "+ex.Message;}}
            }
            UpdateDebugConsoleLab();
        }
        string DebugStatus()
        {
            var manager=Singleton<BaseGameManager>.Instance;
            var ec=manager==null?null:manager.Ec;
            var happy=Object.FindObjectOfType<HappyBaldi>();
            var audio=happy==null?null:(AudioManager)HarmonyLib.AccessTools.Field(typeof(HappyBaldi),"audMan").GetValue(happy);
            return "Baldi Together "+ModVersion+" | "+(Hosting?"HOST":Active?"CLIENT":"OFFLINE")+" self="+self+" generation="+epoch+
                "\nscene="+UnityEngine.SceneManagement.SceneManager.GetActiveScene().name+" requested="+sceneName+" seed="+Seed+
                "\nworldReady="+worldReady+" readyAck="+readyAcknowledged+" beginRequested="+beginRequested+" ecActive="+(ec==null?"none":ec.Active.ToString())+
                " timeScale="+Time.timeScale+" audioPause="+AudioListener.pause+
                "\nhiding step="+hidingStep+" sequence="+hidingSequence+" finished="+hidingFinished+" routine="+(hidingRoutine!=null)+
                " lockers="+peers.Values.Count(p=>p.InLocker)+" eliminated="+peers.Values.Count(p=>p.Eliminated)+
                "\nspawn configured="+(ec==null?0:ec.npcsToSpawn.Count)+" queued="+(ec==null?0:ec.npcsLeftToSpawn.Count)+
                "\nNPC registry="+npcs.Length+" environment="+(ec==null?0:ec.Npcs.Count)+" scene="+Object.FindObjectsOfType<NPC>().Length+
                " HappyBaldi="+(happy==null?"none":"activated="+HarmonyLib.AccessTools.Field(typeof(HappyBaldi),"activated").GetValue(happy)+" spawnOnFinish="+HarmonyLib.AccessTools.Field(typeof(HappyBaldi),"spawnNpcsOnFinishCounting").GetValue(happy))+
                " queuedAudio="+(audio==null?"none":audio.QueuedAudioIsPlaying.ToString())+
                "\n"+Status;
        }
        string DebugPeers()
        {
            return string.Join("\n",peers.Values.OrderBy(p=>p.Id).Select(p=>"#"+p.Id+" "+p.Name+" version="+p.ModVersion+" accepted="+p.Accepted+" ready="+p.Ready+" pose="+p.HasPose+" age="+(Time.realtimeSinceStartup-p.PoseAt).ToString("0.0")+"s locker="+p.InLocker+" eliminated="+p.Eliminated+" position="+p.Position).ToArray());
        }
        string DebugNpcs()
        {
            return string.Join("\n",Object.FindObjectsOfType<NPC>().Take(64).Select(n=>n.GetType().Name+" position="+n.transform.position+" state="+(n.behaviorStateMachine==null||n.behaviorStateMachine.CurrentState==null?"none":n.behaviorStateMachine.CurrentState.GetType().Name)+" active="+n.gameObject.activeInHierarchy).ToArray());
        }
        string SaveDebugReport()
        {
            string folder=Path.Combine(Root,Lab.Enabled?"MultiplayerMod/private/debug-reports":"UserData/BaldiTogether/logs");
            Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,"debug-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+".txt");
            File.WriteAllText(path,DebugStatus()+"\n\nPLAYERS\n"+DebugPeers()+"\n\nCONTACTS\n"+DebugContacts()+"\n\nNPCS\n"+DebugNpcs()+"\n\nLOG\n"+string.Join("\n",DebugLines()));
            return path;
        }
        void ExecuteDebugCommand(string command)
        {
            string key=(command??"").Trim().ToLowerInvariant();
            if(key.Length==0)return;
            CaptureDebugMessage("> "+key);
            try
            {
                if(key=="help")CaptureDebugMessage("help | status | peers | npcs | contacts | clear | save\nRead-only diagnostics; save writes a local report. No simulation pause or gameplay commands.");
                else if(key=="status")CaptureDebugMessage(DebugStatus());
                else if(key=="peers")CaptureDebugMessage(DebugPeers());
                else if(key=="npcs")CaptureDebugMessage(DebugNpcs());
                else if(key=="contacts")CaptureDebugMessage(DebugContacts());
                else if(key=="clear"){lock(debugGate)debugLines.Clear();}
                else if(key=="save")CaptureDebugMessage("Saved "+SaveDebugReport());
                else CaptureDebugMessage("Unknown command. Type help.");
            }
            catch(Exception ex){CaptureDebugMessage("Command failed: "+ex.Message);}
            debugScroll.y=float.MaxValue;
        }
        void DrawDebugConsole()
        {
            if(!debugOpen)return;
            int previousDepth=GUI.depth;GUI.depth=-10000;
            float w=Mathf.Max(280,Mathf.Min(Screen.width-16,1100)),h=Mathf.Max(220,Screen.height-24);
            GUILayout.BeginArea(new Rect(8,8,w,h),GUI.skin.box);
            GUILayout.Label("BALDI TOGETHER DEBUG - F2 / Escape closes");
            GUILayout.Label(debugSnapshot);
            debugScroll=GUILayout.BeginScrollView(debugScroll,GUILayout.ExpandHeight(true));
            // Layout and Repaint must see the same line count, even if logs arrive between them.
            if(Event.current.type==EventType.Layout||debugFrameLines==null)debugFrameLines=DebugLines();
            foreach(string line in debugFrameLines)GUILayout.Label(line);
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();GUI.SetNextControlName("bt-debug-command");
            debugCommand=GUILayout.TextField(debugCommand,256,GUILayout.ExpandWidth(true));
            if(debugFocusPending&&Event.current.type==EventType.Repaint){GUI.FocusControl("bt-debug-command");debugFocusPending=false;}
            bool submit=GUILayout.Button("Run",GUILayout.Width(55));
            if(Event.current.type==EventType.KeyDown&&(Event.current.keyCode==KeyCode.Return||Event.current.keyCode==KeyCode.KeypadEnter)){submit=true;Event.current.Use();}
            if(submit){ExecuteDebugCommand(debugCommand);debugCommand="";GUI.FocusControl("bt-debug-command");}
            GUILayout.EndHorizontal();GUILayout.EndArea();GUI.depth=previousDepth;
        }
        void DisposeDebugConsole()
        {
            if(debugOpen)SetDebugConsole(false);
            if(debugInitialized)Application.logMessageReceivedThreaded-=ReceiveDebugLog;
            debugInitialized=false;
        }
        void UpdateDebugConsoleLab()
        {
            if(!Lab.Enabled||debugLabDone||!TitleVisible||!Lab.HasArg("-bt-console-tour"))return;
            debugLabDone=true;var checks=new Dictionary<string,bool>();
            var old=DebugLines();var oldLock=Cursor.lockState;bool oldVisible=Cursor.visible;float oldScale=Time.timeScale;
            for(int i=0;i<DebugCapacity+30;i++)CaptureDebugMessage("bounded-test-"+i);
            checks["bounded-buffer"]=DebugLines().Length==DebugCapacity&&DebugLines().Last().Contains("bounded-test-429");
            checks["status-fields"]=DebugStatus().Contains("generation=")&&DebugStatus().Contains("ecActive=")&&DebugStatus().Contains("queuedAudio=");
            SetDebugConsole(true);checks["opens-input-block"]=DebugConsoleOpen&&Cursor.lockState==CursorLockMode.None&&Cursor.visible;
            checks["does-not-pause"]=Time.timeScale==oldScale;
            string path=SaveDebugReport();checks["saves-report"]=File.Exists(path)&&File.ReadAllText(path).Contains("NPCS");
            SetDebugConsole(false);checks["restores-cursor"]=!DebugConsoleOpen&&Cursor.lockState==oldLock&&Cursor.visible==oldVisible;
            ExecuteDebugCommand("clear");checks["clear"]=DebugLines().Length==0;
            lock(debugGate){foreach(string line in old)debugLines.Enqueue(line);while(debugLines.Count>DebugCapacity)debugLines.Dequeue();}
            string json="{\"passed\":"+checks.Count(p=>p.Value)+",\"failed\":"+checks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",checks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}";
            Directory.CreateDirectory(Path.Combine(Root,"MultiplayerMod/artifacts/tests"));File.WriteAllText(Path.Combine(Root,"MultiplayerMod/artifacts/tests/console-results.json"),json);Log("LAB console "+json);
        }
    }
}

