using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        MainMenu titleMenu;
        StandardMenuButton multiplayerButton;
        Texture2D multiplayerArtworkTexture;
        Sprite multiplayerArtworkSprite;
        CursorController panelCursor;
        bool cursorWasHidden, cursorClickDisabled, joining, menuDumped;
        float nextMenuScan;
        int uiLabStep;
        float nextUiLab;
        readonly System.Collections.Generic.Dictionary<string,bool> nativeUiChecks = new System.Collections.Generic.Dictionary<string,bool>();
        bool nativeSmallFits;
        int aspectLabStep;
        float nextAspectLab;
        bool transitionLabStarted;
        bool optionsLabStarted;
        bool buttonArtLabStarted;
        bool titleArtLabStarted, titleArtworkPreparedInactive;
        bool backTransitionLabStarted;
        bool pageTransitionLabStarted;
        bool lockerLabStarted;
        // Evaluated many times per frame; the scene search below runs at most once per frame.
        int titleVisibleFrame = -1; bool titleVisibleValue;
        bool TitleVisible
        {
            get
            {
                if(titleVisibleFrame==Time.frameCount)return titleVisibleValue;
                titleVisibleFrame=Time.frameCount;titleVisibleValue=ComputeTitleVisible();return titleVisibleValue;
            }
        }
        bool ComputeTitleVisible()
        {
            {
                if(titleMenu==null||!titleMenu.isActiveAndEnabled)return false;
                var names=Object.FindObjectOfType<NameManager>();
                if(names!=null&&names.canvas!=null&&names.canvas.activeInHierarchy)return false;
                var canvas=multiplayerButton==null?null:multiplayerButton.GetComponentInParent<Canvas>(true);
                return (canvas==null||canvas.isActiveAndEnabled)&&(multiplayerButton==null||multiplayerButton.transform.parent.gameObject.activeInHierarchy);
            }
        }

        void UpdateMenuUi()
        {
            StartCompatibilityLab();UpdateNpcExpansionLab();UpdateSpectatorLab();UpdateRemoteElevatorLab();
            UpdateVendingLab();UpdateResultsLab();UpdateCountdownLab();UpdateContactLab();UpdateLobbyLab();UpdateNpcFeatureLabs();UpdateEncounterLabGuarded();UpdateSoakLab(); UpdateMenuDumpLab(); UpdateFocusLab(); UpdateCompatFailLab(); UpdateSteamLab();
            float now = Time.realtimeSinceStartup;
            if (now > nextMenuScan)
            {
                nextMenuScan = now + 0.5f;
                titleMenu = Object.FindObjectOfType<MainMenu>();
                if (TitleVisible && multiplayerButton == null) AddMenuButton();
            }
            // Its native menu parent controls title visibility, including captured transitions.
            if (multiplayerButton != null) multiplayerButton.gameObject.SetActive(!show);
            if ((show||DebugConsoleOpen) && CursorController.Instance != null && CursorController.Instance != panelCursor) SetMenuInput(true);
            if (show&&!DebugConsoleOpen&&!DebugConsoleConsumedKeyFrame) { Cursor.lockState = CursorLockMode.None; UpdateNativeMenu(); }
            if(Lab.Enabled&&TitleVisible&&!audioLabStarted&&Lab.HasArg("-bt-hideseek-audio-tour"))
            {audioLabStarted=true;StartCoroutine(TourHideSeekAudio());}
            if(Lab.Enabled&&TitleVisible&&!lockerLabStarted&&Lab.HasArg("-bt-locker-tour"))
            {lockerLabStarted=true;StartCoroutine(TourLocker());}
            if(Lab.Enabled&&TitleVisible&&!pageTransitionLabStarted&&Lab.HasArg("-bt-page-transition-tour"))
            {pageTransitionLabStarted=true;StartCoroutine(TourPageTransitions());}
            if(Lab.Enabled&&TitleVisible&&!backTransitionLabStarted&&Lab.HasArg("-bt-back-transition-tour"))
            {backTransitionLabStarted=true;StartCoroutine(TourBackTransition());}
            if(Lab.Enabled&&TitleVisible&&!titleArtLabStarted&&Lab.HasArg("-bt-title-art-transition"))
            {titleArtLabStarted=true;StartCoroutine(TourTitleArtworkTransition());}
            if(Lab.Enabled&&TitleVisible&&!buttonArtLabStarted&&Lab.HasArg("-bt-button-art-tour"))
            {buttonArtLabStarted=true;StartCoroutine(TourButtonArtwork());}
            if(Lab.Enabled && TitleVisible && !optionsLabStarted && Lab.HasArg("-bt-options-tour"))
            {optionsLabStarted=true;StartCoroutine(TourOptions());}
            if(Lab.Enabled && TitleVisible && !transitionLabStarted && Lab.HasArg("-bt-transition-tour") && !Singleton<GlobalCam>.Instance.TransitionActive)
            {transitionLabStarted=true;StartCoroutine(TourMenuTransition());}
            if (Lab.Enabled && TitleVisible && aspectLabStep<=4 && Lab.HasArg("-bt-aspect-tour") && now>=nextAspectLab) TourAspect(now);
            if (Lab.Enabled && uiLabStep <= 12 && Lab.HasArg("-bt-ui-tour") && TitleVisible && now > nextUiLab)
                TourUi(now);
        }

        System.Collections.IEnumerator TourLocker()
        {
            portText="27031";Connect(true);AutoStartRun();
            while(!worldReady)yield return null;
            yield return new WaitForSecondsRealtime(2);
            var locker=Object.FindObjectOfType<HideableLocker>();
            CheckNativeUi("native-locker-found",locker!=null);
            if(locker!=null)
            {
                var replica=new Peer{Id=3,Name="Locker probe",Accepted=true,Ready=true,HasPose=true,Position=Singleton<CoreGameManager>.Instance.GetPlayer(0).transform.position};
                peers.Add(3,replica);UpdateAvatars();
                CheckNativeUi("outside-visible",replica.Avatar!=null&&replica.Avatar.activeSelf);
                locker.Clicked(0);yield return null;SendPose();
                CheckNativeUi("native-entry-detected",locker.playerInside&&peers[self].InLocker);
                replica.InLocker=peers[self].InLocker;
                byte[] packet=PosePacket(replica);replica.InLocker=false;
                using(var reader=new BinaryReader(new MemoryStream(packet))){reader.ReadByte();HandleClient(3,replica,Op.Pose,reader);}
                UpdateAvatars();CheckNativeUi("host-hides-remote",replica.InLocker&&!replica.Avatar.activeSelf);
                replica.InLocker=false;
                using(var reader=new BinaryReader(new MemoryStream(packet))){reader.ReadByte();HandleHost(Op.Pose,reader);}
                UpdateAvatars();CheckNativeUi("client-hides-remote",replica.InLocker&&!replica.Avatar.activeSelf);
                locker.ForceOpen();yield return new WaitForSecondsRealtime(.25f);SendPose();
                CheckNativeUi("native-exit-detected",!locker.playerInside&&!peers[self].InLocker);
                replica.InLocker=false;packet=PosePacket(replica);replica.InLocker=true;
                using(var reader=new BinaryReader(new MemoryStream(packet))){reader.ReadByte();HandleHost(Op.Pose,reader);}
                UpdateAvatars();CheckNativeUi("exit-restores-sprite",!replica.InLocker&&replica.Avatar.activeSelf);
                replica.InLocker=true;ResetWorld();CheckNativeUi("reset-clears-locker-state",!replica.InLocker&&localLocker==null&&!replica.Avatar.activeSelf);
                DestroyAvatar(replica);peers.Remove(3);
            }
            string json="{\"passed\":"+nativeUiChecks.Count(p=>p.Value)+",\"failed\":"+nativeUiChecks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",nativeUiChecks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}";
            File.WriteAllText(Path.Combine(Root,"MultiplayerMod/artifacts/tests/locker-results.json"),json);Log("LAB locker "+json);
        }

        System.Collections.IEnumerator TourPageTransitions()
        {
            var global=Singleton<GlobalCam>.Instance;
            while(global.TransitionActive)yield return null;
            multiplayerButton.Press();yield return new WaitForSecondsRealtime(1);
            foreach(string key in new[]{"host","back","join","back","player-options","back","host","connect","lobby-options","back","leave"})
            {
                portText="27030";
                PressNativeUi(key);
                CheckNativeUi("page-"+nativeUiChecks.Count+"-"+key,global.TransitionActive&&nativeOpeningTransition==global&&nativeMenuCamera.targetTexture==global.Cam.targetTexture);
                var outgoing=((Texture2D)HarmonyLib.AccessTools.Field(typeof(GlobalCam),"transTexture").GetValue(global)).GetPixels32();
                for(int i=0;i<3;i++)yield return new WaitForEndOfFrame();
                yield return new WaitForSecondsRealtime(.12f);
                var destination=(Texture2D)HarmonyLib.AccessTools.Field(typeof(GlobalCam),"toCopyTexture").GetValue(global);
                var blend=(Texture2D)HarmonyLib.AccessTools.Field(typeof(GlobalCam),"transTexture").GetValue(global);
                CheckNativeUi("destination-"+nativeUiChecks.Count,destination.GetPixels32().Count(p=>p.r>245&&p.g>245&&p.b>245)>destination.width*destination.height*.65f);
                CheckNativeUi("blend-"+nativeUiChecks.Count,destination.GetPixels32().Where((p,i)=>!p.Equals(outgoing[i])).Count()>10&&blend.GetPixels32().Where((p,i)=>!p.Equals(outgoing[i])).Count()>10);
                if(key=="join")File.WriteAllBytes(Path.Combine(Root,"MultiplayerMod/private/captures/page-dissolve.png"),blend.EncodeToPNG());
                while(global.TransitionActive)yield return null;
                yield return null;
                CheckNativeUi("restored-"+nativeUiChecks.Count,nativeMenuCamera.targetTexture==null&&nativeOpeningTransition==null);
            }
            CheckNativeUi("leave-disconnects",!Active&&show&&nativeChoices.Any(b=>b.name=="host"));
            PressNativeUi("host");yield return new WaitForSecondsRealtime(1);portText="0";PressNativeUi("connect");yield return new WaitForSecondsRealtime(1);
            CheckNativeUi("validation-preserved",!Active&&Status.Contains("Port must")&&nativeLabels.ContainsKey("name"));
            PressNativeUi("name");CheckNativeUi("field-edits-without-dissolve",nativeEditing=="name"&&!global.TransitionActive);
            nativeEditing=null;SetPanel(false);
            string json="{\"passed\":"+nativeUiChecks.Count(p=>p.Value)+",\"failed\":"+nativeUiChecks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",nativeUiChecks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}";
            File.WriteAllText(Path.Combine(Root,"MultiplayerMod/artifacts/tests/page-transition-results.json"),json);Log("LAB page transitions "+json);
        }

        System.Collections.IEnumerator TourBackTransition()
        {
            while(Singleton<GlobalCam>.Instance.TransitionActive)yield return null;
            multiplayerButton.Press();yield return new WaitForSecondsRealtime(1);
            var global=Singleton<GlobalCam>.Instance;PressNativeUi("back");
            CheckNativeUi("back-starts-dissolve",!show&&global.TransitionActive);
            var snapshot=(Texture2D)HarmonyLib.AccessTools.Field(typeof(GlobalCam),"transTexture").GetValue(global);
            var before=snapshot.GetPixels32();int white=before.Count(p=>p.r>245&&p.g>245&&p.b>245);
            CheckNativeUi("back-captures-multiplayer",white>before.Length*.65f&&TitleArtworkPixels(snapshot)==0);
            for(int i=0;i<3;i++)yield return new WaitForEndOfFrame();
            var destination=(Texture2D)HarmonyLib.AccessTools.Field(typeof(GlobalCam),"toCopyTexture").GetValue(global);
            CheckNativeUi("back-destination-art",TitleArtworkPixels(destination)>30);
            int changed=snapshot.GetPixels32().Where((p,i)=>!p.Equals(before[i])).Count();
            CheckNativeUi("back-progressive-blend",changed>100&&changed<before.Length*.9f&&TitleArtworkPixels(snapshot)>5);
            File.WriteAllBytes(Path.Combine(Root,"MultiplayerMod/private/captures/back-dissolve.png"),snapshot.EncodeToPNG());
            while(global.TransitionActive)yield return null;
            CheckNativeUi("back-restores-cameras",!nativeMenuRoot.activeSelf&&!nativeMenuCamera.enabled&&nativeMenuCamera.targetTexture==null);
            multiplayerButton.Press();yield return new WaitForSecondsRealtime(1);PressNativeUi("host");portText="27029";PressNativeUi("connect");BuildNativeMenu();PressNativeUi("back");
            CheckNativeUi("room-back-preserves-connection",Hosting&&!show&&global.TransitionActive);
            while(global.TransitionActive)yield return null;Disconnect("Back transition tour complete.");
            string json="{\"passed\":"+nativeUiChecks.Count(p=>p.Value)+",\"failed\":"+nativeUiChecks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",nativeUiChecks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}";
            Directory.CreateDirectory(Path.Combine(Root,"MultiplayerMod/artifacts/tests"));File.WriteAllText(Path.Combine(Root,"MultiplayerMod/artifacts/tests/back-transition-results.json"),json);Log("LAB back transition "+json);
        }

        int TitleArtworkPixels(Texture2D texture)
        {
            var rect=(RectTransform)multiplayerButton.transform;var corners=new Vector3[4];rect.GetWorldCorners(corners);
            var cam=Singleton<GlobalCam>.Instance.Cam;var lo=cam.WorldToScreenPoint(corners[0]);var hi=cam.WorldToScreenPoint(corners[2]);int count=0;
            for(int y=Mathf.Max(0,(int)lo.y);y<Mathf.Min(texture.height,(int)hi.y);y++)for(int x=Mathf.Max(0,(int)lo.x);x<Mathf.Min(texture.width,(int)hi.x);x++)
            {var p=texture.GetPixel(x,y);if(p.r>.1f&&p.g<p.r*.5f&&p.b>.04f&&p.b>p.g*1.5f)count++;}
            return count;
        }

        System.Collections.IEnumerator TourTitleArtworkTransition()
        {
            var global=Singleton<GlobalCam>.Instance;
            CheckNativeUi("prepared-before-activation",titleArtworkPreparedInactive);
            yield return new WaitForEndOfFrame();
            var destination=(Texture2D)HarmonyLib.AccessTools.Field(typeof(GlobalCam),"toCopyTexture").GetValue(global);
            Log("TITLE ART initial pixels="+TitleArtworkPixels(destination)+" transition="+global.TransitionActive+" buttonActive="+multiplayerButton.gameObject.activeInHierarchy);
            File.WriteAllBytes(Path.Combine(Root,"MultiplayerMod/private/captures/title-art-destination.png"),destination.EncodeToPNG());
            CheckNativeUi("initial-destination-art",TitleArtworkPixels(destination)>30);
            for(int i=0;i<3;i++)yield return new WaitForEndOfFrame();
            var transition=(Texture2D)HarmonyLib.AccessTools.Field(typeof(GlobalCam),"transTexture").GetValue(global);
            CheckNativeUi("initial-dissolve-art",TitleArtworkPixels(transition)>10);
            File.WriteAllBytes(Path.Combine(Root,"MultiplayerMod/private/captures/title-art-dissolve.png"),transition.EncodeToPNG());
            while(global.TransitionActive)yield return null;
            var menu=titleMenu;menu.gameObject.SetActive(false);yield return new WaitForSecondsRealtime(.7f);
            global.Transition(UiTransition.Dither,.01666667f);menu.gameObject.SetActive(true);yield return new WaitForEndOfFrame();
            CheckNativeUi("return-destination-art",TitleArtworkPixels(destination)>30);
            CheckNativeUi("one-title-button",menu.GetComponentsInChildren<StandardMenuButton>(true).Count(b=>b.name=="BaldiTogether Multiplayer")==1);
            string json="{\"passed\":"+nativeUiChecks.Count(p=>p.Value)+",\"failed\":"+nativeUiChecks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",nativeUiChecks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}";
            Directory.CreateDirectory(Path.Combine(Root,"MultiplayerMod/artifacts/tests"));File.WriteAllText(Path.Combine(Root,"MultiplayerMod/artifacts/tests/title-art-transition-results.json"),json);Log("LAB title artwork transition "+json);
        }

        System.Collections.IEnumerator TourButtonArtwork()
        {
            yield return new WaitForSecondsRealtime(1);
            while(Singleton<GlobalCam>.Instance.TransitionActive)yield return null;
            yield return new WaitForEndOfFrame();
            CheckNativeUi("artwork",multiplayerArtworkSprite!=null&&multiplayerArtworkSprite.rect.width==473&&multiplayerArtworkTexture.GetPixel(0,0).a==0&&multiplayerButton.GetComponent<Image>().sprite==multiplayerArtworkSprite);
            var canvas=multiplayerButton.GetComponentInParent<Canvas>();var hits=new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
            var buttonRect=(RectTransform)multiplayerButton.transform;
            canvas.GetComponent<GraphicRaycaster>().Raycast(new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current){position=RectTransformUtility.WorldToScreenPoint(Singleton<GlobalCam>.Instance.Cam,buttonRect.TransformPoint(buttonRect.rect.center))},hits);
            Log("ART raycast: "+string.Join(",",hits.Select(h=>h.gameObject.name).ToArray()));
            CheckNativeUi("artwork-hit",hits.Count>0&&hits[0].gameObject==multiplayerButton.gameObject);
            Singleton<GlobalCam>.Instance.Cam.Render();yield return CaptureMenuFrame(Path.Combine(Root,"MultiplayerMod/private/captures/button-art.png"));
            multiplayerButton.Press();CheckNativeUi("artwork-opens-menu",show&&nativeOpeningTransition!=null);
            yield return new WaitForSecondsRealtime(1);CheckNativeUi("artwork-dissolve-completes",show&&nativeOpeningTransition==null&&nativeMenuCamera.targetTexture==null);
            string json="{\"passed\":"+nativeUiChecks.Count(p=>p.Value)+",\"failed\":"+nativeUiChecks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",nativeUiChecks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}";
            Directory.CreateDirectory(Path.Combine(Root,"MultiplayerMod/artifacts/tests"));File.WriteAllText(Path.Combine(Root,"MultiplayerMod/artifacts/tests/button-art-results.json"),json);SetPanel(false);Log("LAB button artwork "+json);
        }

        System.Collections.IEnumerator TourOptions()
        {
            multiplayerButton.Press();yield return new WaitForSecondsRealtime(1);
            string oldName=Name,oldCode=Code;int oldSeed=Seed;
            PressNativeUi("player-options");
            CheckNativeUi("player-options",nativeLabels["heading"].text=="PLAYER OPTIONS"&&nativeLabels.Keys.Count(k=>k.StartsWith("planned-"))==5&&nativeChoices.Count==1);
            string path=Path.Combine(Root,"MultiplayerMod/private/captures/");
            yield return CaptureMenuFrame(path+"options-player.png");yield return CaptureMenuFrame(path+"options-player-small.png");CheckNativeUi("player-small",nativeSmallFits);
            PressNativeUi("back");CheckNativeUi("player-back",nativeChoices.Any(b=>b.name=="host")&&Name==oldName&&Code==oldCode&&Seed==oldSeed);
            PressNativeUi("host");portText="27029";PressNativeUi("connect");BuildNativeMenu();PressNativeUi("lobby-options");
            CheckNativeUi("lobby-options",Hosting&&nativeLabels["heading"].text=="LOBBY OPTIONS"&&nativeLabels.Keys.Count(k=>k.StartsWith("planned-"))==8&&nativeChoices.Count==1);
            yield return CaptureMenuFrame(path+"options-lobby.png");yield return CaptureMenuFrame(path+"options-lobby-small.png");CheckNativeUi("lobby-small",nativeSmallFits);
            NativeBack();CheckNativeUi("lobby-back",Hosting&&nativeChoices.Any(b=>b.name=="lobby-options")&&nativeLabels.ContainsKey("player-0"));
            string json="{\"passed\":"+nativeUiChecks.Count(p=>p.Value)+",\"failed\":"+nativeUiChecks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",nativeUiChecks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}";
            Directory.CreateDirectory(Path.Combine(Root,"MultiplayerMod/artifacts/tests"));File.WriteAllText(Path.Combine(Root,"MultiplayerMod/artifacts/tests/options-results.json"),json);
            Disconnect("Options UI tour complete.");SetPanel(false);Log("LAB options "+json);
        }

        System.Collections.IEnumerator TourMenuTransition()
        {
            yield return new WaitForEndOfFrame();
            var global=Singleton<GlobalCam>.Instance;
            SetPanel(true);
            var texture=(Texture2D)HarmonyLib.AccessTools.Field(typeof(GlobalCam),"transTexture").GetValue(global);
            var before=texture.GetPixels32();bool started=global.TransitionActive&&nativeOpeningTransition==global;
            bool intermediate=false,destination=false;int frames=0;
            while(global.TransitionActive && frames++<180)
            {
                yield return new WaitForEndOfFrame();
                var current=texture.GetPixels32();int changed=0;
                for(int i=0;i<current.Length;i++)if(!current[i].Equals(before[i]))changed++;
                if(changed>100 && changed<current.Length*.9f)intermediate=true;
                if(frames==4)File.WriteAllBytes(Path.Combine(Root,"MultiplayerMod/private/transition-mid.png"),texture.EncodeToPNG());
            }
            yield return null;
            var copy=(Texture2D)HarmonyLib.AccessTools.Field(typeof(GlobalCam),"toCopyTexture").GetValue(global);
            // Menu's white background must replace the colorful title in the captured destination.
            var pixels=copy.GetPixels32();int white=0;foreach(var p in pixels)if(p.r>245&&p.g>245&&p.b>245)white++;
            destination=white>pixels.Length*.65f;
            File.WriteAllBytes(Path.Combine(Root,"MultiplayerMod/private/transition-destination.png"),copy.EncodeToPNG());
            bool restored=!global.TransitionActive&&nativeOpeningTransition==null&&nativeMenuCamera.targetTexture==null&&nativePointer.enabled;
            int passed=(started?1:0)+(intermediate?1:0)+(destination?1:0)+(restored?1:0);
            string json="{\"passed\":"+passed+",\"failed\":"+(4-passed)+",\"started\":"+started.ToString().ToLowerInvariant()+",\"intermediate\":"+intermediate.ToString().ToLowerInvariant()+",\"destination\":"+destination.ToString().ToLowerInvariant()+",\"restored\":"+restored.ToString().ToLowerInvariant()+",\"frames\":"+frames+"}";
            Directory.CreateDirectory(Path.Combine(Root,"MultiplayerMod/artifacts/tests"));File.WriteAllText(Path.Combine(Root,"MultiplayerMod/artifacts/tests/transition-results.json"),json);Log("LAB transition "+json);
        }

        void AddMenuButton()
        {
            var labels = titleMenu.GetComponentsInChildren<TextMeshProUGUI>(true);
            var sample = labels.FirstOrDefault(t => t.GetComponentInParent<Canvas>(true) != null);
            var copyright = labels.FirstOrDefault(t => t.text.IndexOf("Basically", StringComparison.OrdinalIgnoreCase) >= 0);
            Canvas canvas = copyright != null ? copyright.GetComponentInParent<Canvas>(true) : (sample != null ? sample.GetComponentInParent<Canvas>(true) : null);
            if (canvas == null) return;
            if (Lab.Enabled && diagnostics && !menuDumped)
            {
                menuDumped = true;
                var lines = canvas.GetComponentsInChildren<RectTransform>(true).Select(r => r.name + " | " + r.rect + " | anchor " + r.anchorMin + " | pos " + r.anchoredPosition);
                Directory.CreateDirectory(Path.Combine(Root, "MultiplayerMod/private"));
                File.WriteAllLines(Path.Combine(Root, "MultiplayerMod/private/menu-hierarchy.txt"), lines.ToArray());
            }
            GameObject item = new GameObject("BaldiTogether Multiplayer", typeof(RectTransform));
            RectTransform rect = (RectTransform)item.transform;
            rect.SetParent(copyright != null ? copyright.rectTransform : canvas.transform, false);
            // The wide copyright includes the year at left; align above the publisher name at right.
            rect.anchorMin = rect.anchorMax = copyright != null ? new Vector2(0.73f, 1) : new Vector2(0.71f, 0.16f);
            rect.pivot = new Vector2(0.5f, 0);
            rect.anchoredPosition = copyright != null ? new Vector2(0, 12) : Vector2.zero;
            rect.sizeDelta = new Vector2(174, 26);
            var label=new GameObject("Multiplayer label",typeof(RectTransform));label.layer=5;var labelRect=(RectTransform)label.transform;
            labelRect.SetParent(rect,false);labelRect.anchorMin=Vector2.zero;labelRect.anchorMax=Vector2.one;labelRect.offsetMin=labelRect.offsetMax=Vector2.zero;
            TextMeshProUGUI text = label.AddComponent<TextMeshProUGUI>();
            text.font = (copyright ?? sample).font;
            text.fontSize = 19;
            text.alignment = TextAlignmentOptions.Center;
            text.color = new Color(0.08f, 0.42f, 0.08f);
            text.text = "MULTIPLAYER";
            text.enableWordWrapping = false;
            text.raycastTarget = false;
            var hitImage=item.AddComponent<Image>();hitImage.color=Color.clear;hitImage.raycastTarget=true;hitImage.canvasRenderer.cullTransparentMesh=false;
            item.tag = "Button";
            multiplayerButton = item.AddComponent<StandardMenuButton>();
            multiplayerButton.text = text;
            multiplayerButton.underlineOnHigh = true;
            multiplayerButton.OnPress = new UnityEvent();
            multiplayerButton.OnPress.AddListener(delegate { SetPanel(true); });
            multiplayerButton.OnHighlight = new UnityEvent();
            multiplayerButton.OffHighlight = new UnityEvent();
            multiplayerButton.OnRelease = new UnityEvent();
            AddMultiplayerArtwork(rect);
            Log("Main menu Multiplayer button added above the copyright.");
        }

        public void PrepareTitleArtwork(MainMenu menu)
        {
            if(menu==null)return;
            if(multiplayerButton!=null&&multiplayerButton.GetComponentInParent<MainMenu>(true)!=menu)
            {multiplayerButton.gameObject.SetActive(false);Destroy(multiplayerButton.gameObject);multiplayerButton=null;}
            titleMenu=menu;
            if(multiplayerButton==null)AddMenuButton();
            if(multiplayerButton!=null&&!menu.gameObject.activeInHierarchy)titleArtworkPreparedInactive=true;
        }

        public void PrepareTitleTransitionCapture(GlobalCam global)
        {
            if(show||multiplayerButton==null||!multiplayerButton.transform.parent.gameObject.activeInHierarchy)return;
            titleMenu=multiplayerButton.GetComponentInParent<MainMenu>(true);
            if(!TitleVisible||global.Cam==null||global.Cam.targetTexture==null)return;
            // New UI graphics must be built before the native dither reads its destination texture.
            Canvas.ForceUpdateCanvases();global.Cam.Render();
        }

        void AddMultiplayerArtwork(RectTransform rect)
        {
            if(multiplayerArtworkSprite==null)
            {
                using(var stream=typeof(TogetherRuntime).Assembly.GetManifestResourceStream("BaldiTogether.MultiplayerButton.png"))
                using(var bytes=new MemoryStream())
                {
                    if(stream==null)return;
                    stream.CopyTo(bytes);multiplayerArtworkTexture=new Texture2D(2,2,TextureFormat.RGBA32,false);
                    if(!multiplayerArtworkTexture.LoadImage(bytes.ToArray()))return;
                    multiplayerArtworkTexture.filterMode=FilterMode.Point;multiplayerArtworkTexture.wrapMode=TextureWrapMode.Clamp;
                    // Display the lettering without the supplied PNG's large transparent margins.
                    multiplayerArtworkSprite=Sprite.Create(multiplayerArtworkTexture,new Rect(9,183,473,127),new Vector2(.5f,.5f),100,0,SpriteMeshType.FullRect);
                }
            }
            rect.sizeDelta=new Vector2(180,48);
            var visual=rect.gameObject;var image=visual.GetComponent<Image>();
            image.sprite=multiplayerArtworkSprite;image.color=Color.white;image.preserveAspect=true;
            multiplayerButton.text.color=Color.clear;multiplayerButton.underlineOnHigh=false;multiplayerButton.eventOnHigh=true;
            multiplayerButton.OnHighlight.AddListener(delegate {if(visual!=null)visual.transform.localScale=Vector3.one*1.03f;});
            multiplayerButton.OffHighlight.AddListener(delegate {if(visual!=null)visual.transform.localScale=Vector3.one;});
        }

        void SetMenuInput(bool blocked)
        {
            if (panelCursor != null)
            {
                panelCursor.DisableClick(cursorClickDisabled);
                panelCursor.Hide(cursorWasHidden);
                panelCursor = null;
            }
            if (!blocked || CursorController.Instance == null) return;
            panelCursor = CursorController.Instance;
            cursorWasHidden = (bool)HarmonyLib.AccessTools.Field(typeof(CursorController), "hidden").GetValue(panelCursor);
            cursorClickDisabled = (bool)HarmonyLib.AccessTools.Field(typeof(CursorController), "clickDisabled").GetValue(panelCursor);
            panelCursor.DisableClick(true);
            panelCursor.Hide(true);
        }

        void OnGUI()
        {
            Styles();
            if(MigrationInProgress){GUI.Box(new Rect(Screen.width/2-240,Screen.height/2-45,480,90),Status);DrawDebugConsole();return;}
            if (!show && Active && Running && !TitleVisible) { DrawNames(); DrawFeatureHud();DrawSpectator(); }
            DrawDebugConsole();
        }

        void SelectTab(bool join) { Online = false; joining = join; nativePage = 1; Status = join ? "Enter your friend's LAN address." : "Choose a name and host a game."; BuildNativeMenu(); }

        // Explicit lab tour exercises the same handlers, not physical mouse/keyboard input.
        void QueueMenuCapture(string path) { StartCoroutine(CaptureMenuFrame(path)); }
        System.Collections.IEnumerator CaptureMenuFrame(string path)
        {
            yield return new WaitForEndOfFrame();
            RenderTexture previous=RenderTexture.active; Texture2D image=null; RenderTexture target=null;
            RenderTexture oldTarget=nativeMenuCamera==null?null:nativeMenuCamera.targetTexture;
            var scaler=nativeMenuRoot==null?null:nativeMenuRoot.GetComponent<CanvasScaler>();
            var oldMode=scaler==null?CanvasScaler.ScaleMode.ScaleWithScreenSize:scaler.uiScaleMode; float oldScale=scaler==null?1:scaler.scaleFactor;
            try
            {
                if(show&&nativeMenuCamera!=null)
                {
                    bool small=path.EndsWith("small.png",StringComparison.Ordinal),aspect=Path.GetFileName(path).StartsWith("aspect-",StringComparison.Ordinal);
                    int width=small?800:Screen.width,height=small?600:Screen.height;
                    if(aspect){width=path.Contains("classic")?800:path.Contains("ultrawide")?2560:path.Contains("portrait")?600:1920;height=path.Contains("classic")?600:path.Contains("portrait")?800:1080;}
                    target=new RenderTexture(width,height,24); nativeMenuCamera.targetTexture=target;
                    if((small||aspect)&&scaler!=null){scaler.uiScaleMode=CanvasScaler.ScaleMode.ConstantPixelSize;scaler.scaleFactor=Mathf.Min(width/480f,height/360f);nativeMenuCanvas.scaleFactor=scaler.scaleFactor;}
                    Canvas.ForceUpdateCanvases(); nativeMenuCamera.Render(); RenderTexture.active=target;
                    if(small)
                    {
                        nativeSmallFits=true;var corners=new Vector3[4];
                        foreach(var label in nativeLabels.Values){label.rectTransform.GetWorldCorners(corners);foreach(var corner in corners){var pixel=nativeMenuCamera.WorldToScreenPoint(corner);if(pixel.x<0||pixel.x>width||pixel.y<0||pixel.y>height)nativeSmallFits=false;}}
                    }
                    image=new Texture2D(width,height,TextureFormat.RGB24,false); image.ReadPixels(new Rect(0,0,width,height),0,0);image.Apply();
                    if(aspect)
                    {
                        var frame=(RectTransform)nativeMenuRoot.transform.Find("White Background");var corners=new Vector3[4];frame.GetWorldCorners(corners);
                        var bottom=nativeMenuCamera.WorldToScreenPoint(corners[0]);var top=nativeMenuCamera.WorldToScreenPoint(corners[2]);float scale=Mathf.Min(width/480f,height/360f);
                        float left=(width-480*scale)/2,bottomEdge=(height-360*scale)/2;string key=Path.GetFileNameWithoutExtension(path);
                        bool geometry=Mathf.Abs(bottom.x-left)<1&&Mathf.Abs(bottom.y-bottomEdge)<1&&Mathf.Abs(top.x-(width-left))<1&&Mathf.Abs(top.y-(height-bottomEdge))<1;
                        bool pixels=true;
                        if(left>1)pixels=image.GetPixel(1,height/2).maxColorComponent<.02f&&image.GetPixel(width-2,height/2).maxColorComponent<.02f;
                        if(bottomEdge>1)pixels=pixels&&image.GetPixel(width/2,1).maxColorComponent<.02f&&image.GetPixel(width/2,height-2).maxColorComponent<.02f;
                        pixels=pixels&&image.GetPixel((int)left+4,(int)bottomEdge+4).grayscale>.98f;
                        CheckNativeUi(key+"-frame",geometry&&pixels);
                        var hits=new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                        nativeRaycaster.Raycast(new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current){position=nativeMenuCamera.WorldToScreenPoint(nativeChoices[0].transform.position)},hits);
                        bool clickable=hits.Count>0&&hits[0].gameObject==nativeChoices[0].gameObject;hits.Clear();
                        nativeRaycaster.Raycast(new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current){position=new Vector2(1,1)},hits);
                        CheckNativeUi(key+"-input",clickable&&!hits.Any(h=>h.gameObject.GetComponent<StandardMenuButton>()!=null));
                    }
                }
                else
                {
                    var global=Singleton<GlobalCam>.Instance; var source=global==null?null:global.Cam.targetTexture;
                    if(source!=null) { RenderTexture.active=source;image=new Texture2D(source.width,source.height,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,source.width,source.height),0,0);image.Apply(); }
                }
                if(image!=null)File.WriteAllBytes(path,image.EncodeToPNG());
            }
            finally {if(nativeMenuCamera!=null)nativeMenuCamera.targetTexture=oldTarget;if(scaler!=null){scaler.uiScaleMode=oldMode;scaler.scaleFactor=oldScale;}Canvas.ForceUpdateCanvases();RenderTexture.active=previous;if(target!=null){target.Release();Destroy(target);}if(image!=null)Destroy(image);}
        }
        void CheckNativeUi(string name, bool value) { nativeUiChecks[name]=value; Log("UI CHECK "+name+": "+(value?"PASS":"FAIL")); }
        void TourAspect(float now)
        {
            nextAspectLab=now+3;
            if(aspectLabStep==0)multiplayerButton.Press();
            if(aspectLabStep<4)QueueMenuCapture(Path.Combine(Root,"MultiplayerMod/private/captures/aspect-"+new[]{"wide","classic","ultrawide","portrait"}[aspectLabStep]+".png"));
            else
            {
                string json="{\"passed\":"+nativeUiChecks.Count(p=>p.Value)+",\"failed\":"+nativeUiChecks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",nativeUiChecks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}";
                File.WriteAllText(Path.Combine(Root,"MultiplayerMod/artifacts/tests/aspect-results.json"),json);SetPanel(false);Log("LAB aspect tour complete.");
            }
            aspectLabStep++;
        }
        void PressNativeUi(string name) { NativePress(nativeChoices.First(b=>b.name==name)); }
        void TourUi(float now)
        {
            if (nextUiLab == 0) { nextUiLab = now + 6; return; }
            nextUiLab = now + 6;
            string path = Path.Combine(Root, "MultiplayerMod/private/captures/ui-native-");
            switch (uiLabStep++)
            {
                case 0: QueueMenuCapture(path + "menu.png"); break;
                case 1: multiplayerButton.Press(); CheckNativeUi("menu",nativeChoices.Count==4 && nativeLabels["heading"].font==multiplayerButton.text.font); QueueMenuCapture(path + "choices.png"); break;
                case 2:
                    PressNativeUi("host"); CheckNativeUi("host",nativeLabels.ContainsKey("name")&&!nativeLabels.ContainsKey("address"));
                    PressNativeUi("name"); NativeSetValue("name",""); NativeType("Playex\br"); CheckNativeUi("typing",Name=="Player"); NativeBack();
                    PressNativeUi("port"); NativeSetValue("port",""); NativeType("abc27015"); CheckNativeUi("numeric-port",portText=="27015"); NativeBack();
                    PressNativeUi("code"); NativeSetValue("code","test"); RefreshNativeLabels(); CheckNativeUi("password",nativeLabels["code"].text.StartsWith("****")); NativeBack(); Code="";
                    break;
                case 3: QueueMenuCapture(path + "host.png"); break;
                case 4:
                    NativeBack(); PressNativeUi("join");
                    PressNativeUi("address"); NativeSetValue("address","127.0.0.1"); NativeBack();
                    CheckNativeUi("join",nativeLabels.ContainsKey("address") && joining); QueueMenuCapture(path + "join.png"); break;
                case 5: NativeBack(); PressNativeUi("host"); portText="0"; PressNativeUi("connect"); CheckNativeUi("validation",!Active&&Status.Contains("Port must")); break;
                case 6: QueueMenuCapture(path + "error.png"); break;
                case 7: portText = "27019"; PressNativeUi("connect"); BuildNativeMenu(); QueueMenuCapture(path + "room.png"); break;
                case 8:
                    CheckNativeUi("room",Hosting&&nativeLabels.ContainsKey("player-0"));
                    PressNativeUi("back"); CheckNativeUi("back-keeps-room",!show&&Hosting); multiplayerButton.Press(); PressNativeUi("leave"); CheckNativeUi("leave",!Active); break;
                case 9: SetPanel(false); break;
                case 10: multiplayerButton.Press(); PressNativeUi("host"); break;
                case 11: QueueMenuCapture(path + "small.png"); break;
                case 12:
                    CheckNativeUi("small-layout",nativeSmallFits);
                    string json="{\"passed\":"+nativeUiChecks.Count(p=>p.Value)+",\"failed\":"+nativeUiChecks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",nativeUiChecks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}";
                    Directory.CreateDirectory(Path.Combine(Root,"MultiplayerMod/artifacts/tests")); File.WriteAllText(Path.Combine(Root,"MultiplayerMod/artifacts/tests/native-ui-results.json"),json);
                    SetPanel(false); Log("LAB UI tour complete."); break;
            }
        }
    }
}

