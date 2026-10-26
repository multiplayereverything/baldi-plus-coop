using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        string compatibilityRejection;
        readonly Dictionary<int,float> latencySampleAt=new Dictionary<int,float>();
        readonly Dictionary<int,Image> nativeQualityImages=new Dictionary<int,Image>();
        Texture2D connectionIndicatorTexture;
        Sprite[] connectionIndicatorSprites;
        bool connectionIndicatorAttempted;

        void LoadConnectionIndicators()
        {
            if(connectionIndicatorAttempted)return;
            connectionIndicatorAttempted=true;
            using(var stream=typeof(TogetherRuntime).Assembly.GetManifestResourceStream("BaldiTogether.ConnectionIndicator.png"))
            {
                if(stream==null)return;
                using(var bytes=new MemoryStream())
                {
                    stream.CopyTo(bytes);connectionIndicatorTexture=new Texture2D(2,2,TextureFormat.RGBA32,false);
                    if(!connectionIndicatorTexture.LoadImage(bytes.ToArray()))return;
                }
            }
            // The supplied sheet contains black silhouettes. Keep its alpha while allowing status tinting.
            var pixels=connectionIndicatorTexture.GetPixels32();
            for(int i=0;i<pixels.Length;i++){pixels[i].r=255;pixels[i].g=255;pixels[i].b=255;}
            connectionIndicatorTexture.SetPixels32(pixels);connectionIndicatorTexture.Apply();
            connectionIndicatorTexture.filterMode=FilterMode.Point;connectionIndicatorTexture.wrapMode=TextureWrapMode.Clamp;
            int[] left={717,549,379,210,44};connectionIndicatorSprites=new Sprite[5];
            for(int i=0;i<left.Length;i++)connectionIndicatorSprites[i]=Sprite.Create(connectionIndicatorTexture,new Rect(left[i],23,112,90),new Vector2(.5f,.5f),100,0,SpriteMeshType.FullRect);
        }

        void DisposeConnectionIndicators()
        {
            nativeQualityImages.Clear();
            if(connectionIndicatorSprites!=null)foreach(var sprite in connectionIndicatorSprites)if(sprite!=null)Destroy(sprite);
            if(connectionIndicatorTexture!=null)Destroy(connectionIndicatorTexture);
            connectionIndicatorSprites=null;connectionIndicatorTexture=null;connectionIndicatorAttempted=false;
        }

        int ConnectionBars(Peer peer)
        {
            int milliseconds;
            if(peer.Id==self)return 5;
            if((!Hosting&&peer.Id!=0)||!FreshLatency(peer.Id)||!latency.TryGetValue(peer.Id,out milliseconds))return 0;
            return milliseconds<=70?5:milliseconds<=100?4:milliseconds<=150?3:milliseconds<=200?2:1;
        }

        Color ConnectionTint(Peer peer)
        {
            int bars=ConnectionBars(peer);
            return bars==0?new Color(.45f,.45f,.45f):bars>=4?new Color(0,.4f,0):bars>=2?new Color(.55f,.35f,0):new Color(.65f,0,0);
        }

        void NativeQualityIcon(Peer peer,float x,float y)
        {
            LoadConnectionIndicators();if(connectionIndicatorSprites==null)return;
            var image=NativeRect("quality-icon-"+peer.Id,nativeMenuContent.transform,x,y,23,18).gameObject.AddComponent<Image>();
            image.raycastTarget=false;image.sprite=connectionIndicatorSprites[Math.Max(1,ConnectionBars(peer))-1];image.color=ConnectionTint(peer);
            nativeQualityImages[peer.Id]=image;
        }

        void DrawConnectionIndicator(Peer peer,Rect rect,float opacity=1)
        {
            LoadConnectionIndicators();if(connectionIndicatorSprites==null)return;
            var sprite=connectionIndicatorSprites[Math.Max(1,ConnectionBars(peer))-1];var crop=sprite.rect;
            var previous=GUI.color;var tint=ConnectionTint(peer);tint.a*=Mathf.Clamp01(opacity);GUI.color=tint;
            GUI.DrawTextureWithTexCoords(rect,connectionIndicatorTexture,new Rect(crop.x/connectionIndicatorTexture.width,crop.y/connectionIndicatorTexture.height,crop.width/connectionIndicatorTexture.width,crop.height/connectionIndicatorTexture.height));
            GUI.color=previous;
        }

        void ClearCompatibilityStatus() { compatibilityRejection=null; }
        void RecordCompatibilityRejection(string message) { compatibilityRejection=message; }

        string NativePeerName(Peer peer)
        {
            return peer.Name+(peer.Id==self?" (you)":"")+(peer.Id==0?" - Host":"")+(peer.Eliminated?" - Spectating":"");
        }

        string ConnectionQuality(Peer peer)
        {
            if(peer.Id==self)return "Local";
            // A client measures its host link only; never present that sample as a peer-to-peer RTT.
            if(!Hosting&&peer.Id!=0)return "Via host";
            int milliseconds;
            if(!latency.TryGetValue(peer.Id,out milliseconds))return "Measuring...";
            if(!FreshLatency(peer.Id))return "No response";
            return (milliseconds<=100?"Good":milliseconds<=200?"Fair":"Poor")+" "+milliseconds+" ms";
        }

        bool FreshLatency(int id)
        {float at;return latencySampleAt.TryGetValue(id,out at)&&Time.realtimeSinceStartup-at<=8;}

        string CompatibilityState(Peer peer)
        {
            if(string.IsNullOrEmpty(peer.ModVersion)||string.IsNullOrEmpty(peer.GameHash))return "Checking...";
            return peer.ModVersion==ModVersion&&string.Equals(peer.GameHash,gameHash,StringComparison.OrdinalIgnoreCase)?"MATCH":"MISMATCH";
        }

        void BuildNativeCompatibility()
        {
            nativeLabels["heading"].text="COMPATIBILITY";
            nativeLabels["heading"].fontSize=30;
            NativeText("compatibility-version","Baldi Together "+ModVersion+" / Protocol "+Wire.Version,0,99,440,27,16,TextAlignmentOptions.Center);
            NativeText("compatibility-rule","Matching mod and game builds required",0,75,440,25,13,TextAlignmentOptions.Center);
            if(Active)
            {
                int row=0;
                foreach(var peer in peers.Values.Where(p=>p.Accepted).OrderBy(p=>p.Id))
                {
                    float y=40-row++*32;
                    var name=NativeText("compatibility-name-"+peer.Id,peer.Name+(peer.Id==self?" (you)":""),-116,y,190,28,16,TextAlignmentOptions.Left);
                    name.enableWordWrapping=false;name.overflowMode=TextOverflowModes.Ellipsis;
                    NativeText("compatibility-build-"+peer.Id,peer.ModVersion??"Unknown",30,y,105,28,14,TextAlignmentOptions.Center);
                    NativeText("compatibility-state-"+peer.Id,CompatibilityState(peer),152,y,120,28,14,TextAlignmentOptions.Right);
                }
            }
            else
            {
                NativeText("compatibility-local","Your mod: "+ModVersion,0,34,440,28,19,TextAlignmentOptions.Center);
                NativeText("compatibility-instruction","Every player must install the same release.\nThe room checks mod version and game assembly before joining.",0,-17,425,61,15,TextAlignmentOptions.Center);
            }
            NativeText("compatibility-detail",CompatibilityDetail(),0,-99,435,45,12,TextAlignmentOptions.Center);
            NativeChoice("back","BACK",0,-150,260,24,NativeBack);
        }

        string CompatibilityDetail()
        {
            if(!string.IsNullOrEmpty(compatibilityRejection))return compatibilityRejection;
            return Active?"MATCH checks the exact mod version and game assembly.\nConnection quality appears beside players in the lobby.":"Other installed mods are not checked automatically.";
        }

        void RefreshCompatibilityLabels()
        {
            foreach(var peer in peers.Values.Where(p=>p.Accepted))
            {
                Image image;if(nativeQualityImages.TryGetValue(peer.Id,out image)&&image!=null&&connectionIndicatorSprites!=null)
                {image.sprite=connectionIndicatorSprites[Math.Max(1,ConnectionBars(peer))-1];image.color=ConnectionTint(peer);}
                TextMeshProUGUI label;
                if(nativeLabels.TryGetValue("player-"+peer.Id,out label))label.text=NativePeerName(peer);
                if(nativeLabels.TryGetValue("quality-"+peer.Id,out label))
                {
                    label.text=ConnectionQuality(peer);
                    int milliseconds;
                    label.color=peer.Id!=self&&(Hosting||peer.Id==0)&&FreshLatency(peer.Id)&&latency.TryGetValue(peer.Id,out milliseconds)
                        ?milliseconds<=100?new Color(0,.4f,0):milliseconds<=200?new Color(.55f,.35f,0):new Color(.65f,0,0):Color.black;
                }
                if(nativeLabels.TryGetValue("compatibility-build-"+peer.Id,out label))label.text=peer.ModVersion??"Unknown";
                if(nativeLabels.TryGetValue("compatibility-state-"+peer.Id,out label))
                {
                    string state=CompatibilityState(peer);label.text=state;
                    label.color=state=="MATCH"?new Color(0,.4f,0):state=="MISMATCH"?new Color(.65f,0,0):Color.black;
                }
            }
            TextMeshProUGUI detail;
            if(nativeLabels.TryGetValue("compatibility-detail",out detail))detail.text=CompatibilityDetail();
        }

        bool compatibilityLabStarted;
        void StartCompatibilityLab()
        {
            if(compatibilityLabStarted||!Lab.Enabled||!TitleVisible||!Lab.HasArg("-bt-compatibility-tour"))return;
            compatibilityLabStarted=true;StartCoroutine(TourCompatibility());
        }

        System.Collections.IEnumerator TourCompatibility()
        {
            var checks=new Dictionary<string,bool>();
            var global=Singleton<GlobalCam>.Instance;
            while(global.TransitionActive)yield return null;
            multiplayerButton.Press();yield return new WaitForSecondsRealtime(1);
            PressNativeUi("compatibility");
            checks["compatibility-dissolves"]=global.TransitionActive&&nativePage==4&&nativeOpeningTransition==global;
            while(global.TransitionActive)yield return null;
            yield return null;
            checks["local-version-shown"]=nativeLabels["compatibility-local"].text.Contains(ModVersion);
            PressNativeUi("back");while(global.TransitionActive)yield return null;yield return null;
            portText="27037";Connect(true);BuildNativeMenu();
            var peer=new Peer{Id=3,Name="Connection probe",Accepted=true,ModVersion=ModVersion,GameHash=gameHash,Eliminated=true};peers.Add(3,peer);
            checks["exact-build-matches"]=CompatibilityState(peer)=="MATCH";
            peer.ModVersion="0.0.0";checks["mod-mismatch-detected"]=CompatibilityState(peer)=="MISMATCH";peer.ModVersion=ModVersion;
            peer.GameHash="wrong";checks["game-mismatch-detected"]=CompatibilityState(peer)=="MISMATCH";peer.GameHash=gameHash;
            int[] sample={70,100,150,200,201};
            for(int i=0;i<sample.Length;i++)
            {latency[3]=sample[i];latencySampleAt[3]=Time.realtimeSinceStartup;checks["rtt-bars-"+sample[i]]=ConnectionBars(peer)==5-i;}
            latencySampleAt[3]=Time.realtimeSinceStartup-9;
            checks["stale-sample-gray"]=ConnectionBars(peer)==0&&ConnectionQuality(peer)=="No response";
            latency[3]=42;latencySampleAt[3]=Time.realtimeSinceStartup;BuildNativeMenu();RefreshNativeLabels();
            checks["supplied-indicator-loaded"]=connectionIndicatorSprites!=null&&connectionIndicatorSprites.Length==5&&nativeQualityImages.ContainsKey(3);
            checks["live-room-status"]=nativeLabels["quality-3"].text=="Good 42 ms"&&nativeLabels["player-3"].text.Contains("Spectating");
            var captureDir=Path.Combine(Root,"MultiplayerMod/private/captures/");Directory.CreateDirectory(captureDir);
            yield return CaptureMenuFrame(captureDir+"connection-quality.png");
            PressNativeUi("compatibility");
            checks["room-compatibility-dissolves"]=global.TransitionActive&&nativePage==4;
            while(global.TransitionActive)yield return null;yield return null;
            checks["peer-build-row"]=nativeLabels["compatibility-build-3"].text==ModVersion&&nativeLabels["compatibility-state-3"].text=="MATCH";
            yield return CaptureMenuFrame(captureDir+"compatibility.png");
            PressNativeUi("back");while(global.TransitionActive)yield return null;yield return null;
            checks["back-restores-room"]=nativeChoices.Any(b=>b.name=="lobby-options")&&nativeMenuCamera.targetTexture==null;
            peers.Remove(3);latency.Remove(3);latencySampleAt.Remove(3);Disconnect("Compatibility tour complete.");SetPanel(false);
            string json="{\"passed\":"+checks.Count(p=>p.Value)+",\"failed\":"+checks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",checks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}";
            Directory.CreateDirectory(Path.Combine(Root,"MultiplayerMod/artifacts/tests"));File.WriteAllText(Path.Combine(Root,"MultiplayerMod/artifacts/tests/compatibility-results.json"),json);Log("LAB compatibility "+json);
        }
    }
}
