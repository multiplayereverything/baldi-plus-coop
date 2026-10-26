using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        GameObject nativeMenuRoot, nativeMenuContent;
        Canvas nativeMenuCanvas;
        Camera nativeMenuCamera;
        GraphicRaycaster nativeRaycaster;
        TMP_FontAsset nativeMenuFont;
        Image nativePointer;
        readonly List<StandardMenuButton> nativeChoices = new List<StandardMenuButton>();
        readonly Dictionary<string,TextMeshProUGUI> nativeLabels = new Dictionary<string,TextMeshProUGUI>();
        int nativePage, nativeFocus;
        string nativeEditing, nativeSignature;
        Vector3 nativeMouse;
        Vector2 nativePointerOffset;
        SoundObject nativeHighlightSound, nativeConfirmSound;
        GlobalCam nativeOpeningTransition;
        RenderTexture nativePreviousTarget;

        void FinishNativeOpening()
        {
            if (nativeOpeningTransition == null) return;
            if (nativeMenuCamera != null) nativeMenuCamera.targetTexture = nativePreviousTarget;
            nativeOpeningTransition = null; nativePreviousTarget = null;
            if (nativePointer != null) nativePointer.enabled = nativePointer.sprite != null;
            if (show && panelCursor != null) panelCursor.Hide(true);
        }

        void NativeMenuVisibility(bool visible, bool dissolveBack = false)
        {
            if (!visible)
            {
                FinishNativeOpening();
                if(dissolveBack&&TitleVisible&&nativeMenuRoot!=null&&nativeMenuRoot.activeSelf)CaptureNativeClosing();
                nativeEditing = null;if(nativeMenuRoot!=null)nativeMenuRoot.SetActive(false);if(nativeMenuCamera!=null)nativeMenuCamera.enabled=false;return;
            }
            var global = Singleton<GlobalCam>.Instance;
            // Capture the old menu before enabling the new one, just as MainMenu does.
            if (TitleVisible && global != null && global.Cam != null && global.Cam.targetTexture != null)
                global.Transition(UiTransition.Dither, 0.01666667f);
            nativePage = 0; nativeSignature = null;
            BuildNativeMenu();
            if (TitleVisible && global != null && global.TransitionActive && nativeMenuCamera != null)
            {
                nativeOpeningTransition = global; nativePreviousTarget = nativeMenuCamera.targetTexture;
                // The native dither captures its destination on the following frame.
                // Draw this menu into that target while the native output camera displays the dissolve.
                nativeMenuCamera.targetTexture = global.Cam.targetTexture;
                nativePointer.enabled = false;
                global.Cam.enabled = false;
                Canvas.ForceUpdateCanvases();
                nativeMenuCamera.Render();
            }
        }

        void CaptureNativeClosing(bool revealTitle = true)
        {
            var global=Singleton<GlobalCam>.Instance;
            if(global==null||global.Cam==null||global.Cam.targetTexture==null||nativeMenuCamera==null)return;
            global.Transition(UiTransition.Dither,.01666667f);
            var target=global.Cam.targetTexture;var previousTarget=nativeMenuCamera.targetTexture;var previousActive=RenderTexture.active;
            try
            {
                // GlobalCam normally captures its own camera; the outgoing page uses our menu camera.
                nativeMenuCamera.targetTexture=target;Canvas.ForceUpdateCanvases();nativeMenuCamera.Render();RenderTexture.active=target;
                var snapshot=(Texture2D)HarmonyLib.AccessTools.Field(typeof(GlobalCam),"transTexture").GetValue(global);
                snapshot.ReadPixels(new Rect(0,0,target.width,target.height),0,0);snapshot.Apply();
            }
            finally {nativeMenuCamera.targetTexture=previousTarget;RenderTexture.active=previousActive;}
            if(revealTitle&&multiplayerButton!=null)multiplayerButton.gameObject.SetActive(true);
        }

        void NativePageTransition(Action action)
        {
            var global=Singleton<GlobalCam>.Instance;
            if(global!=null&&global.TransitionActive)return;
            FinishNativeOpening();
            CaptureNativeClosing(false);
            action();
            BuildNativeMenu();
            if(show&&global!=null&&global.TransitionActive&&nativeMenuCamera!=null)
            {
                nativeOpeningTransition=global;nativePreviousTarget=nativeMenuCamera.targetTexture;
                nativeMenuCamera.targetTexture=global.Cam.targetTexture;nativePointer.enabled=false;
                global.Cam.enabled=false;Canvas.ForceUpdateCanvases();nativeMenuCamera.Render();
            }
        }

        RectTransform NativeRect(string name, Transform parent, float x, float y, float width, float height)
        {
            var item = new GameObject(name,typeof(RectTransform)); var rect = (RectTransform)item.transform;
            item.layer = 5;
            rect.SetParent(parent,false); rect.anchorMin = rect.anchorMax = new Vector2(.5f,.5f);
            rect.anchoredPosition = new Vector2(x,y); rect.sizeDelta = new Vector2(width,height); return rect;
        }

        TextMeshProUGUI NativeText(string key, string value, float x, float y, float width, float height, float size, TextAlignmentOptions alignment)
        {
            var rect = NativeRect(key,nativeMenuContent.transform,x,y,width,height);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>(); text.font = nativeMenuFont;
            text.fontSize = size; text.text = value; text.color = Color.black; text.alignment = alignment;
            text.richText = false; text.raycastTarget = false; text.enableWordWrapping = true;
            nativeLabels[key] = text; return text;
        }

        StandardMenuButton NativeChoice(string key, string value, float x, float y, float width, float size, Action action)
        {
            var text = NativeText(key,value,x,y,width,34,size,TextAlignmentOptions.Center);
            text.raycastTarget = true; text.gameObject.tag = "Button";
            var button = text.gameObject.AddComponent<StandardMenuButton>(); button.text = text;
            button.underlineOnHigh = true; button.OnPress = new UnityEvent();
            button.OnPress.AddListener(delegate {
                if(key=="host"||key=="join"||key=="steam"||key=="steam-find"||key=="find-lan"||key=="player-options"||key=="lobby-options"||key=="compatibility"||key=="connect"||key=="leave")NativePageTransition(action);
                else action();
            }); button.OnHighlight = new UnityEvent();
            button.OffHighlight = new UnityEvent(); button.OnRelease = new UnityEvent();
            nativeChoices.Add(button); return button;
        }

        void NativeField(string key, string label, float y)
        {
            NativeText(key+"-label",label,-140,y,130,28,18,TextAlignmentOptions.Left);
            var button = NativeChoice(key,"",65,y,270,19,delegate { nativeEditing = key; });
            button.text.alignment = TextAlignmentOptions.Left; button.text.enableWordWrapping = false;
            button.text.overflowMode = TextOverflowModes.Ellipsis;
        }

        void BuildNativeMenu()
        {
            if (!show) return;
            if (nativeMenuFont == null)
            {
                var sample = multiplayerButton != null ? multiplayerButton.text : Object.FindObjectsOfType<TextMeshProUGUI>().FirstOrDefault(t=>t.font!=null);
                if (sample != null) nativeMenuFont = sample.font;
                if (nativeMenuFont == null) return;
            }
            if (nativeMenuRoot == null)
            {
                nativeMenuRoot = new GameObject("Multiplayer Menu",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster));
                nativeMenuRoot.transform.SetParent(transform,false);
                nativeMenuRoot.layer=5;
                var cameraObject=new GameObject("Multiplayer Menu Camera"); cameraObject.transform.SetParent(transform,false); cameraObject.transform.localPosition=new Vector3(0,0,-100);
                nativeMenuCamera=cameraObject.AddComponent<Camera>(); nativeMenuCamera.orthographic=true; nativeMenuCamera.nearClipPlane=.1f; nativeMenuCamera.farClipPlane=5;
                nativeMenuCamera.depth=1000; nativeMenuCamera.cullingMask=1<<5; nativeMenuCamera.clearFlags=CameraClearFlags.Depth;
                nativeMenuCanvas = nativeMenuRoot.GetComponent<Canvas>(); nativeMenuCanvas.renderMode = RenderMode.ScreenSpaceCamera; nativeMenuCanvas.worldCamera=nativeMenuCamera; nativeMenuCanvas.planeDistance=1; nativeMenuCanvas.sortingOrder = 30000;
                var scaler = nativeMenuRoot.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                // GlobalCam and the native menu frame use a 480x360 safe area.
                scaler.referenceResolution = new Vector2(480,360); scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
                nativeRaycaster = nativeMenuRoot.GetComponent<GraphicRaycaster>();
                var background = NativeRect("Black Frame",nativeMenuRoot.transform,0,0,0,0);
                background.anchorMin = Vector2.zero; background.anchorMax = Vector2.one; background.offsetMin = background.offsetMax = Vector2.zero;
                background.gameObject.AddComponent<Image>().color = Color.black;
                NativeRect("White Background",nativeMenuRoot.transform,0,0,480,360).gameObject.AddComponent<Image>().color = Color.white;
                var pointerRect = NativeRect("Menu Cursor",nativeMenuRoot.transform,0,0,32,32);
                nativePointer = pointerRect.gameObject.AddComponent<Image>(); nativePointer.raycastTarget = false;
                var source = panelCursor != null ? panelCursor : CursorController.Instance;
                if (source == null) source = Resources.FindObjectsOfTypeAll<CursorController>().FirstOrDefault(c=>!c.gameObject.scene.IsValid());
                if (source != null)
                {
                    var image = source.GetComponentInChildren<Image>(true);
                    if (image != null) { nativePointer.sprite = image.sprite; nativePointer.color = image.color; pointerRect.sizeDelta = image.rectTransform.rect.size; pointerRect.pivot = image.rectTransform.pivot; nativePointerOffset=image.rectTransform.anchoredPosition; }
                    nativeHighlightSound = (SoundObject)HarmonyLib.AccessTools.Field(typeof(CursorController),"audHighlight").GetValue(source);
                    nativeConfirmSound = (SoundObject)HarmonyLib.AccessTools.Field(typeof(CursorController),"audConfirm").GetValue(source);
                }
                nativePointer.enabled = nativePointer.sprite != null;
            }
            nativeMenuRoot.SetActive(true);
            nativeMenuCamera.enabled=true;
            if (nativeMenuContent != null) { nativeMenuContent.SetActive(false); Destroy(nativeMenuContent); }
            nativeChoices.Clear(); nativeLabels.Clear(); nativeQualityImages.Clear(); nativeEditing = null; nativeFocus = 0;
            nativeMenuContent = NativeRect("Menu Choices",nativeMenuRoot.transform,0,0,480,360).gameObject;
            nativePointer.transform.SetAsLastSibling(); nativeMouse = Input.mousePosition;
            NativeText("heading","MULTIPLAYER",0,139,450,52,36,TextAlignmentOptions.Center);
            if (nativePage == LanPage) BuildNativeLanGames();
            else if (nativePage == 4) BuildNativeCompatibility();
            else if (nativePage == SteamPage && !Active) BuildNativeSteam();
            else if (nativePage == SteamListPage && !Active) BuildNativeSteamList();
            else if (nativePage >= 2) BuildNativeOptions();
            else if (Active) BuildNativeRoom();
            else if (nativePage == 0)
            {
                NativeChoice("host","HOST GAME",0,86,360,28,delegate { SelectTab(false); });
                NativeChoice("join","JOIN GAME",0,52,360,28,delegate { SelectTab(true); });
                NativeChoice("steam","STEAM GAME",0,19,420,23,delegate { Online = true; Status = "Host a room, or find one your friends opened."; OpenNativeOptions(SteamPage); });
                NativeChoice("find-lan","FIND LAN GAMES",0,-12,420,21,delegate { OpenNativeOptions(LanPage); });
                NativeChoice("player-options","PLAYER OPTIONS",0,-42,420,21,delegate { OpenNativeOptions(2); });
                NativeChoice("compatibility","COMPATIBILITY",0,-72,420,21,delegate { OpenNativeOptions(4); });
                NativeChoice("back","BACK",0,-119,240,26,delegate { SetPanel(false,true); });
            }
            else
            {
                NativeText("subheading",joining?"JOIN GAME":"HOST GAME",0,101,420,30,21,TextAlignmentOptions.Center);
                NativeField("name","Name",65);
                if (joining) NativeField("address","Address",28);
                NativeField("port","Port",joining?-9:20);
                NativeField("code","Room code",joining?-46:-25);
                if (!joining) NativeText("seed-note","A new run uses a random seed.",0,-61,430,26,13,TextAlignmentOptions.Center);
                NativeText("status",Status,0,-87,440,32,12,TextAlignmentOptions.Center);
                NativeChoice("connect",joining?"JOIN":"HOST",0,-119,260,25,delegate { if (TitleVisible || Singleton<CoreGameManager>.Instance==null) Connect(!joining); else Tell("Return to the main menu to host or join."); });
                NativeChoice("back","BACK",0,-157,220,21,NativeBack);
            }
            nativeSignature = NativeSignature(); RefreshNativeLabels();
        }

        void BuildNativeRoom()
        {
            bool steamHost=Hosting&&Network.Steam;
            NativeText("room-title",Hosting?(Network.Steam?"STEAM ROOM":"HOST GAME"):self<0?"CONNECTING...":Network.Steam?"JOINED STEAM ROOM":"JOINED GAME",0,101,450,28,21,TextAlignmentOptions.Center);
            int row=0;
            foreach (Peer peer in peers.Values.Where(p=>p.Accepted).OrderBy(p=>p.Id))
            {
                float y=64-row++*(steamHost?26:28);
                var label=NativeText("player-"+peer.Id,NativePeerName(peer),-80,y,260,27,17,TextAlignmentOptions.Left);
                label.enableWordWrapping=false; label.overflowMode=TextOverflowModes.Ellipsis;
                NativeQualityIcon(peer,95,y);
                NativeText("quality-"+peer.Id,ConnectionQuality(peer),170,y,88,27,11,TextAlignmentOptions.Right);
            }
            NativeText("status",Status,0,steamHost?-38:-48,440,steamHost?20:27,11,TextAlignmentOptions.Center);
            if(steamHost)((RectTransform)NativeChoice("steam-invite","INVITE FRIENDS",0,-59,430,18,InviteSteamFriends).transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,22);
            var compatibility=NativeChoice("compatibility","COMPATIBILITY",0,-83,430,19,delegate { OpenNativeOptions(4); });
            var options=NativeChoice("lobby-options","LOBBY OPTIONS",0,-110,430,19,delegate { OpenNativeOptions(3); });
            var back=NativeChoice("back",Running?"BACK TO GAME":"BACK",0,-137,430,20,delegate { SetPanel(false,true); });
            var leave=NativeChoice("leave",self<0?"CANCEL":"LEAVE GAME",0,-164,300,19,delegate { if(!HandoverHost())Disconnect("Left the room."); });
            foreach(var choice in new[]{compatibility,options,back,leave})((RectTransform)choice.transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,25);
        }

        void OpenNativeOptions(int page) {nativePage=page;BuildNativeMenu();}

        void BuildNativeOptions()
        {
            bool player=nativePage==2;
            nativeLabels["heading"].text=player?"PLAYER OPTIONS":"LOBBY OPTIONS";
            nativeLabels["heading"].fontSize=30;
            if(player)
            {
                NativeChoice("player-colour",NativeColourLabel(),-30,92,300,20,CyclePlayerColour);
                var preview=NativeRect("colour-preview",nativeMenuContent.transform,150,78,34,70);
                nativeColourPreview=preview.gameObject.AddComponent<Image>();nativeColourPreview.raycastTarget=false;nativeColourPreview.preserveAspect=true;
                RefreshColourPreview();
            }
            NativeText("options-note","COMING SOON",0,player?36:102,430,24,14,TextAlignmentOptions.Center);
            string[] names=player
                ?new[]{"Name tag visibility","Name tag size","Player icon","Connection display"}
                :new[]{"Shared YTP","Shared lives","Friendly item effects","Shared map discovery","Mid-run joining"};
            float top=player?10:66,step=player?22:24;
            for(int i=0;i<names.Length;i++)NativeText("planned-"+i,names[i],0,top-i*step,440,25,player?16:18,TextAlignmentOptions.Center);
            NativeChoice("back","BACK",0,-147,260,24,NativeBack);
        }

        Image nativeColourPreview;
        string NativeColourLabel(){return "COLOR: "+PlayerSpriteSheet.ColourNames[PlayerSpriteSheet.ClampColour(PlayerColour)];}

        void CyclePlayerColour()
        {
            PlayerColour=(byte)((PlayerSpriteSheet.ClampColour(PlayerColour)+1)%PlayerSpriteSheet.ColourNames.Length);
            SaveSettings();
            TextMeshProUGUI label;if(nativeLabels.TryGetValue("player-colour",out label))label.text=NativeColourLabel();
            RefreshColourPreview();
        }

        void RefreshColourPreview()
        {
            if(nativeColourPreview==null)return;
            var sprites=PlayerSpriteSheet.SpritesFor(PlayerColour);
            nativeColourPreview.sprite=sprites==null?null:sprites[0];nativeColourPreview.enabled=sprites!=null;
        }

        string NativeSignature()
        { return Active+":"+Hosting+":"+self+":"+Running+":"+nativePage+":"+string.Join("|",peers.Values.Where(p=>p.Accepted).OrderBy(p=>p.Id).Select(p=>p.Id+"="+p.Name).ToArray())+":"+LanSignature()+":"+SteamSignature(); }

        string NativeValue(string key)
        { return key=="name"?Name:key=="address"?Address:key=="port"?portText:Code; }

        void NativeSetValue(string key, string value)
        {
            int limit=key=="name"?24:key=="address"?128:key=="port"?5:64;
            value = new string(value.Where(c=>!char.IsControl(c) && (key!="port" || c>='0'&&c<='9')).Take(limit).ToArray());
            if(key=="name")Name=value;else if(key=="address")Address=value;else if(key=="port")portText=value;else Code=value;
        }

        void RefreshNativeLabels()
        {
            foreach (string key in new[]{"name","address","port","code"})
            {
                TextMeshProUGUI label; if(!nativeLabels.TryGetValue(key,out label))continue;
                string value=NativeValue(key); if(key=="code")value=new string('*',value.Length);
                label.text=(value.Length==0?(key=="code"?"(optional)":"________"):value)+(nativeEditing==key && Time.unscaledTime%1<.5f?"_":"");
                label.color=nativeEditing==key?new Color(0,.4f,0):Color.black;
            }
            TextMeshProUGUI status; if(nativeLabels.TryGetValue("status",out status))
                status.text=Active&&!Running&&self>=0?(Hosting?"Players: "+peers.Values.Count(p=>p.Accepted)+"/4. Start a new run from PLAY.":"Waiting for the host to start the game."):Status;
            RefreshCompatibilityLabels();
        }

        void NativeBack()
        {
            if(nativeOpeningTransition!=null&&nativeOpeningTransition.TransitionActive)return;
            if(nativeEditing!=null) { nativeEditing=null;return; }
            if(nativePage>=2||(!Active&&nativePage!=0)){NativePageTransition(delegate {nativePage=0;});return;}
            SetPanel(false,true);
        }

        void NativePress(StandardMenuButton button)
        { if(button==null)return; if(nativeConfirmSound!=null)Singleton<MusicManager>.Instance.PlaySoundEffect(nativeConfirmSound); button.Press(); if(button!=null)button.UnHold(); }

        void NativeType(string input)
        {
            if(nativeEditing==null)return;
            string value=NativeValue(nativeEditing);
            foreach(char c in input) {if(c=='\b'){if(value.Length>0)value=value.Substring(0,value.Length-1);}else if(c!='\n'&&c!='\r')value+=c;}
            NativeSetValue(nativeEditing,value);
        }

        void UpdateNativeMenu()
        {
            if(!show)return;
            if(nativeOpeningTransition != null)
            {
                if(nativeOpeningTransition.TransitionActive) { Cursor.visible=false; return; }
                FinishNativeOpening();
            }
            if(nativeMenuRoot==null || nativeSignature!=NativeSignature())BuildNativeMenu();
            if(nativeMenuRoot==null)return;
            Cursor.visible = nativePointer==null || !nativePointer.enabled;
            Vector2 position; RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)nativeMenuRoot.transform,Input.mousePosition,nativeMenuCamera,out position);
            nativePointer.rectTransform.anchoredPosition=new Vector2(Mathf.Clamp(position.x,-240,240),Mathf.Clamp(position.y,-180,180))+nativePointerOffset;
            var hits=new List<RaycastResult>(); nativeRaycaster.Raycast(new PointerEventData(EventSystem.current){position=Input.mousePosition},hits);
            var hovered=hits.Count==0?null:hits[0].gameObject.GetComponent<StandardMenuButton>();
            if((Input.mousePosition-nativeMouse).sqrMagnitude>.01f && hovered!=null)nativeFocus=nativeChoices.IndexOf(hovered);
            nativeMouse=Input.mousePosition;
            if(Input.GetMouseButtonDown(0) && hovered!=null)NativePress(hovered);
            if(!show)return;
            if(nativeOpeningTransition!=null)return;
            if(nativeEditing!=null)
            {
                if((Input.GetKey(KeyCode.LeftControl)||Input.GetKey(KeyCode.RightControl))&&Input.GetKeyDown(KeyCode.V))NativeType(GUIUtility.systemCopyBuffer);
                else NativeType(Input.inputString);
                if(Input.GetKeyDown(KeyCode.Return)||Input.GetKeyDown(KeyCode.KeypadEnter))nativeEditing=null;
                if(Input.GetKeyDown(KeyCode.Tab))
                { var fields=joining?new[]{"name","address","port","code"}:new[]{"name","port","code"};int index=Array.IndexOf(fields,nativeEditing);nativeEditing=fields[(index+(Input.GetKey(KeyCode.LeftShift)||Input.GetKey(KeyCode.RightShift)?fields.Length-1:1))%fields.Length]; }
            }
            else if(nativeChoices.Count>0)
            {
                if(Input.GetKeyDown(KeyCode.DownArrow))nativeFocus=(nativeFocus+1)%nativeChoices.Count;
                if(Input.GetKeyDown(KeyCode.Tab))nativeFocus=(nativeFocus+(Input.GetKey(KeyCode.LeftShift)||Input.GetKey(KeyCode.RightShift)?nativeChoices.Count-1:1))%nativeChoices.Count;
                if(Input.GetKeyDown(KeyCode.UpArrow))nativeFocus=(nativeFocus+nativeChoices.Count-1)%nativeChoices.Count;
                if(Input.GetKeyDown(KeyCode.Return)||Input.GetKeyDown(KeyCode.KeypadEnter))NativePress(nativeChoices[Mathf.Clamp(nativeFocus,0,nativeChoices.Count-1)]);
            }
            if(show&&nativeChoices.Count>0)
            {var focus=nativeChoices[Mathf.Clamp(nativeFocus,0,nativeChoices.Count-1)]; if(!focus.WasHighlighted&&nativeHighlightSound!=null)Singleton<MusicManager>.Instance.PlaySoundEffect(nativeHighlightSound);focus.Highlight();}
            RefreshNativeLabels();
        }
    }
}
