using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    // Spectator screen presentation: a native-style overlay canvas scaled to the 480x360 frame.
    // Gameplay (target choice, camera lock, switch sound) stays in Spectator.cs; this file only shows it.
    public sealed partial class TogetherRuntime
    {
        sealed class SpectatorRosterRow
        {
            public int Id, StatusCode = -1;
            public string PeerName;
            public byte Colour;
            public Image Highlight, Swatch;
            public TextMeshProUGUI Name, Status;
        }

        const float SpectatorDissolveSeconds = .3f;
        const int SpectatorDissolveStart = 12;
        static readonly int[] SpectatorBayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

        GameObject spectatorUiRoot, spectatorUiCard, spectatorUiConceal, spectatorUiRoster;
        Canvas spectatorUiCanvas;
        TMP_FontAsset spectatorUiFont;
        TextMeshProUGUI spectatorUiCaption, spectatorUiName, spectatorUiHint, spectatorUiConcealTitle, spectatorUiConcealNote;
        Image spectatorUiSwatch, spectatorUiPortrait, spectatorUiConcealShade;
        GameObject spectatorUiVents;
        RawImage spectatorUiDither;
        Texture2D spectatorUiDitherTexture;
        int spectatorUiDitherStep = -1;
        readonly List<SpectatorRosterRow> spectatorUiRows = new List<SpectatorRosterRow>();
        int spectatorUiRosterSelf = -2; // -2 marks the roster rows as needing a rebuild
        int spectatorUiShownTarget = -1;
        float spectatorUiDissolveAt = -1;
        int spectatorAliveCount;
        string spectatorUiState = "hidden";
        bool spectatorUiLabStarted;

        bool SpectatorUiWanted
        {
            get
            {
                if (!Active || !worldReady || deathSent || !LocalEliminated || spectatorPlayer == null) return false;
                if (show || DebugConsoleOpen || MigrationInProgress || TitleVisible || ResultsTransitionActive) return false;
                var core = Singleton<CoreGameManager>.Instance;
                return core != null && !core.Paused;
            }
        }

        static Color SpectatorSwatch(int colour)
        {
            float hue = PlayerSpriteSheet.TargetHue(colour);
            if (hue == -1) return new Color(.31f, .84f, .67f);
            if (hue == -2) return new Color(.95f, .95f, .95f);
            return Color.HSVToRGB(hue, .8f, .9f);
        }

        TMP_FontAsset SpectatorFont()
        {
            if (spectatorUiFont != null) return spectatorUiFont;
            if (nativeMenuFont != null) return spectatorUiFont = nativeMenuFont;
            var core = Singleton<CoreGameManager>.Instance;
            TextMeshProUGUI sample = null;
            if (core != null && core.GetHud(0) != null) sample = core.GetHud(0).GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t => t.font != null);
            if (sample == null) sample = Object.FindObjectsOfType<TextMeshProUGUI>().FirstOrDefault(t => t.font != null);
            if (sample != null) spectatorUiFont = sample.font;
            if (spectatorUiFont == null) spectatorUiFont = TMP_Settings.defaultFontAsset;
            return spectatorUiFont;
        }

        RectTransform SpectatorRect(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
        {
            var item = new GameObject(name, typeof(RectTransform)); item.layer = 5;
            var rect = (RectTransform)item.transform; rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = anchor; rect.pivot = pivot;
            rect.anchoredPosition = position; rect.sizeDelta = size; return rect;
        }

        RectTransform SpectatorStretch(string name, Transform parent, float inset)
        {
            var rect = SpectatorRect(name, parent, Vector2.zero, new Vector2(.5f, .5f), Vector2.zero, Vector2.zero);
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset); rect.offsetMax = new Vector2(-inset, -inset); return rect;
        }

        Image SpectatorImage(RectTransform rect, Color colour)
        { var image = rect.gameObject.AddComponent<Image>(); image.color = colour; image.raycastTarget = false; return image; }

        TextMeshProUGUI SpectatorText(string name, Transform parent, float x, float y, float width, float height, float size, TextAlignmentOptions alignment, Color colour)
        {
            // The game font's line is about 1.4x its size. With Ellipsis overflow a box shorter than one line
            // truncates the whole text to nothing, so every box gets at least one full line (centred, so it only grows).
            height = Mathf.Max(height, Mathf.Ceil(size * 1.7f));
            var rect = SpectatorRect(name, parent, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(x, y), new Vector2(width, height));
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>(); text.font = SpectatorFont();
            text.fontSize = size; text.color = colour; text.alignment = alignment; text.richText = false; text.raycastTarget = false;
            text.enableWordWrapping = false; text.overflowMode = TextOverflowModes.Ellipsis; text.text = ""; return text;
        }

        // A white panel with the black outline the game uses for its HUD boxes and menu frames.
        RectTransform SpectatorPanel(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
        {
            var rect = SpectatorRect(name, parent, anchor, pivot, position, size);
            SpectatorImage(rect, Color.black);
            SpectatorImage(SpectatorStretch("Fill", rect, 2), Color.white);
            return rect;
        }

        void BuildSpectatorUi()
        {
            if (spectatorUiRoot != null || SpectatorFont() == null) return;
            spectatorUiRoot = new GameObject("Spectator Overlay", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            spectatorUiRoot.layer = 5; spectatorUiRoot.transform.SetParent(transform, false);
            // A screen-space overlay canvas draws after every camera, so it never clears or re-blits the world view
            // (a second URP base camera with Depth clear would clear colour). It is hidden whenever the menu is open.
            spectatorUiCanvas = spectatorUiRoot.GetComponent<Canvas>(); spectatorUiCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            spectatorUiCanvas.sortingOrder = 100; spectatorUiCanvas.pixelPerfect = false;
            var scaler = spectatorUiRoot.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(480, 360); scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            var root = spectatorUiRoot.transform;

            // Concealed and waiting view: a dark wash over the teammate's view, never a flat black screen.
            var conceal = SpectatorStretch("Concealed View", root, 0); spectatorUiConceal = conceal.gameObject;
            spectatorUiConcealShade = SpectatorImage(conceal, new Color(0, 0, 0, .86f));
            var vents = SpectatorRect("Locker Vents", conceal, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(0, 46), new Vector2(84, 26));
            spectatorUiVents = vents.gameObject;
            for (int i = 0; i < 4; i++) SpectatorImage(SpectatorRect("Vent " + i, vents, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(0, 9 - i * 6), new Vector2(84, 3)), new Color(.42f, .42f, .4f, .55f));
            spectatorUiConcealTitle = SpectatorText("Concealed Title", conceal, 0, 12, 420, 30, 24, TextAlignmentOptions.Center, Color.white);
            spectatorUiConcealNote = SpectatorText("Concealed Note", conceal, 0, -14, 420, 20, 13, TextAlignmentOptions.Center, new Color(.78f, .78f, .78f));

            // Short dither dissolve on switching, at one dither cell per 480x360 pixel like GlobalCam's transition.
            spectatorUiDitherTexture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            spectatorUiDitherTexture.name = "Spectator dither"; spectatorUiDitherTexture.filterMode = FilterMode.Point; spectatorUiDitherTexture.wrapMode = TextureWrapMode.Repeat;
            spectatorUiDither = SpectatorStretch("Switch Dissolve", root, 0).gameObject.AddComponent<RawImage>();
            spectatorUiDither.texture = spectatorUiDitherTexture; spectatorUiDither.raycastTarget = false; spectatorUiDither.gameObject.SetActive(false);
            spectatorUiDitherStep = -1;

            // Team roster under the notebook counter (top left); the item bar keeps the top right.
            var roster = SpectatorPanel("Team Roster", root, new Vector2(0, 1), new Vector2(0, 1), new Vector2(6, -34), new Vector2(140, 24));
            spectatorUiRoster = roster.gameObject;
            var rosterTitle = SpectatorText("Roster Title", roster, 0, 0, 128, 14, 11, TextAlignmentOptions.Left, new Color(.35f, .35f, .35f));
            rosterTitle.text = "TEAM"; rosterTitle.rectTransform.anchorMin = rosterTitle.rectTransform.anchorMax = new Vector2(.5f, 1); rosterTitle.rectTransform.anchoredPosition = new Vector2(0, -9);

            // Header card, bottom centre between the stamina meter and the right edge.
            var card = SpectatorPanel("Spectating Card", root, new Vector2(.5f, 0), new Vector2(.5f, 0), new Vector2(0, 8), new Vector2(236, 56));
            spectatorUiCard = card.gameObject;
            var portraitFrame = SpectatorRect("Portrait Frame", card, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(-98, 0), new Vector2(28, 46));
            SpectatorImage(portraitFrame, new Color(.86f, .86f, .86f));
            spectatorUiPortrait = SpectatorImage(SpectatorStretch("Portrait", portraitFrame, 1), Color.white); spectatorUiPortrait.preserveAspect = true;
            spectatorUiCaption = SpectatorText("Caption", card, 12, 16, 188, 14, 11, TextAlignmentOptions.Left, new Color(.35f, .35f, .35f));
            var swatchFrame = SpectatorRect("Swatch Frame", card, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(-75, 0), new Vector2(14, 14));
            SpectatorImage(swatchFrame, Color.black);
            spectatorUiSwatch = SpectatorImage(SpectatorStretch("Swatch", swatchFrame, 2), Color.white);
            spectatorUiName = SpectatorText("Name", card, 20, 0, 172, 22, 20, TextAlignmentOptions.Left, Color.black);
            spectatorUiHint = SpectatorText("Controls", card, 12, -17, 188, 14, 10, TextAlignmentOptions.Left, new Color(.25f, .25f, .25f));

            spectatorUiRows.Clear(); spectatorUiRosterSelf = -2; spectatorUiShownTarget = -1; spectatorUiDissolveAt = -1;
            spectatorUiRoot.SetActive(false);
        }

        void DestroySpectatorUi()
        {
            if (spectatorUiRoot != null) { spectatorUiRoot.SetActive(false); Object.Destroy(spectatorUiRoot); }
            if (spectatorUiDitherTexture != null) Object.Destroy(spectatorUiDitherTexture);
            spectatorUiRoot = spectatorUiCard = spectatorUiConceal = spectatorUiRoster = spectatorUiVents = null;
            spectatorUiCanvas = null; spectatorUiDither = null; spectatorUiDitherTexture = null; spectatorUiDitherStep = -1;
            spectatorUiCaption = spectatorUiName = spectatorUiHint = spectatorUiConcealTitle = spectatorUiConcealNote = null;
            spectatorUiSwatch = spectatorUiPortrait = spectatorUiConcealShade = null;
            spectatorUiRows.Clear(); spectatorUiRosterSelf = -2;
            spectatorUiShownTarget = -1; spectatorUiDissolveAt = -1; spectatorUiState = "hidden";
        }

        static readonly string[] SpectatorStatusNames = { "ALIVE", "CAUGHT", "HIDING" };
        static readonly Color[] SpectatorStatusColours = { new Color(0, .4f, 0), new Color(.65f, 0, 0), new Color(.55f, .35f, 0) };
        static int SpectatorStatusCode(Peer peer) { return peer.Eliminated ? 1 : peer.InLocker ? 2 : 0; }

        // Allocation-free per frame: compares the built rows with the accepted peers field by field.
        bool SpectatorRosterChanged()
        {
            if (spectatorUiRosterSelf != self) return true;
            int accepted = 0;
            foreach (var peer in peers.Values) if (peer.Accepted) accepted++;
            if (accepted != spectatorUiRows.Count) return true;
            for (int i = 0; i < spectatorUiRows.Count; i++)
            {
                var row = spectatorUiRows[i]; Peer peer;
                if (!peers.TryGetValue(row.Id, out peer) || !peer.Accepted || peer.Name != row.PeerName || peer.Colour != row.Colour) return true;
            }
            return false;
        }

        void RefreshSpectatorRoster()
        {
            if (SpectatorRosterChanged())
            {
                var team = peers.Values.Where(p => p.Accepted).OrderBy(p => p.Id).ToArray();
                spectatorUiRosterSelf = self;
                var rosterRect = (RectTransform)spectatorUiRoster.transform;
                foreach (var row in spectatorUiRows) if (row.Highlight != null) Object.Destroy(row.Highlight.gameObject);
                spectatorUiRows.Clear();
                rosterRect.sizeDelta = new Vector2(140, 22 + team.Length * 15);
                for (int i = 0; i < team.Length; i++)
                {
                    var rowRect = SpectatorRect("Roster " + team[i].Id, rosterRect, new Vector2(.5f, 1), new Vector2(.5f, 1), new Vector2(0, -19 - i * 15), new Vector2(132, 14));
                    var row = new SpectatorRosterRow { Id = team[i].Id, PeerName = team[i].Name, Colour = team[i].Colour };
                    row.Highlight = SpectatorImage(rowRect, new Color(1, .93f, .55f));
                    var swatchFrame = SpectatorRect("Swatch Frame", rowRect, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(-59, 0), new Vector2(10, 10));
                    SpectatorImage(swatchFrame, Color.black);
                    row.Swatch = SpectatorImage(SpectatorStretch("Swatch", swatchFrame, 1), SpectatorSwatch(team[i].Colour));
                    row.Name = SpectatorText("Name", rowRect, -16, 0, 74, 14, 11, TextAlignmentOptions.Left, Color.black);
                    row.Name.text = team[i].Name + (team[i].Id == self ? " (you)" : "");
                    row.Status = SpectatorText("Status", rowRect, 43, 0, 44, 14, 10, TextAlignmentOptions.Right, Color.black);
                    spectatorUiRows.Add(row);
                }
            }
            foreach (var row in spectatorUiRows)
            {
                Peer peer; if (!peers.TryGetValue(row.Id, out peer)) continue;
                int status = SpectatorStatusCode(peer);
                if (status != row.StatusCode) { row.StatusCode = status; row.Status.text = SpectatorStatusNames[status]; row.Status.color = SpectatorStatusColours[status]; }
                bool watched = row.Id == spectatorTarget;
                if (row.Highlight.enabled != watched) row.Highlight.enabled = watched;
            }
        }

        void SetSpectatorDither(int step)
        {
            if (spectatorUiDither == null || step == spectatorUiDitherStep) return;
            spectatorUiDitherStep = step;
            bool on = step > 0;
            if (spectatorUiDither.gameObject.activeSelf != on) spectatorUiDither.gameObject.SetActive(on);
            if (!on) return;
            var pixels = new Color32[16];
            for (int i = 0; i < 16; i++) pixels[i] = new Color32(0, 0, 0, (byte)(SpectatorBayer[i] < step ? 255 : 0));
            spectatorUiDitherTexture.SetPixels32(pixels); spectatorUiDitherTexture.Apply(false);
            var size = ((RectTransform)spectatorUiRoot.transform).rect.size;
            spectatorUiDither.uvRect = new Rect(0, 0, Mathf.Max(1, size.x) / 4f, Mathf.Max(1, size.y) / 4f);
        }

        // Runs every frame from UpdateSpectator, after the target has been chosen.
        void UpdateSpectatorUi()
        {
            if (!SpectatorUiWanted)
            {
                if (spectatorUiRoot != null && spectatorUiRoot.activeSelf) spectatorUiRoot.SetActive(false);
                if (spectatorPlayer == null && spectatorUiRoot != null) DestroySpectatorUi();
                spectatorUiState = "hidden";
                return;
            }
            BuildSpectatorUi();
            if (spectatorUiRoot == null) { spectatorUiState = "fallback"; return; }
            if (!spectatorUiRoot.activeSelf) spectatorUiRoot.SetActive(true);

            Peer target; if (!peers.TryGetValue(spectatorTarget, out target)) target = null;
            if (target != null && target.Id != spectatorUiShownTarget) { spectatorUiDissolveAt = Time.unscaledTime; SetSpectatorDither(-1); }
            spectatorUiShownTarget = target == null ? -1 : target.Id;

            spectatorUiCaption.text = "SPECTATING";
            if (target == null)
            {
                spectatorUiState = "waiting";
                spectatorUiName.text = "Nobody yet";
                spectatorUiSwatch.transform.parent.gameObject.SetActive(false);
                spectatorUiPortrait.enabled = false;
                spectatorUiHint.text = "No respawn until the next floor";
                spectatorUiConcealTitle.text = "WAITING FOR A TEAMMATE";
                spectatorUiConcealNote.text = "Teammates appear here once they are in play.";
            }
            else
            {
                spectatorUiState = spectatorBlank ? "hiding" : "watching";
                spectatorUiName.text = target.Name;
                spectatorUiSwatch.transform.parent.gameObject.SetActive(true);
                spectatorUiSwatch.color = SpectatorSwatch(target.Colour);
                var sprites = PlayerSpriteSheet.SpritesFor(target.Colour);
                spectatorUiPortrait.sprite = sprites == null ? null : sprites[0];
                spectatorUiPortrait.enabled = spectatorUiPortrait.sprite != null;
                spectatorUiHint.text = spectatorAliveCount > 1 ? "LEFT / RIGHT or TAB: switch teammate" : "LEFT / RIGHT or TAB: only teammate left";
                spectatorUiConcealTitle.text = target.Name + " is hiding";
                spectatorUiConcealNote.text = "Their view stays dark while they are in a locker.";
            }
            bool concealed = target == null || spectatorBlank;
            if (spectatorUiConceal.activeSelf != concealed) spectatorUiConceal.SetActive(concealed);
            spectatorUiVents.SetActive(target != null);
            RefreshSpectatorRoster();

            int step = 0;
            if (spectatorUiDissolveAt >= 0)
            {
                float t = (Time.unscaledTime - spectatorUiDissolveAt) / SpectatorDissolveSeconds;
                if (t >= 1) spectatorUiDissolveAt = -1;
                else step = Mathf.CeilToInt(SpectatorDissolveStart * (1 - t));
            }
            SetSpectatorDither(step);
        }

        string SpectatorRowStatus(int id)
        {
            var row = spectatorUiRows.FirstOrDefault(r => r.Id == id);
            return row == null || row.Status == null ? null : row.Status.text;
        }

        // Rendered, not just assigned: a non-empty rect and every non-space character laid out visibly.
        static bool SpectatorTextRendered(TextMeshProUGUI text)
        {
            if (text == null || string.IsNullOrEmpty(text.text) || !text.gameObject.activeInHierarchy) return false;
            var size = text.rectTransform.rect.size; if (size.x <= 0 || size.y <= 0) return false;
            text.ForceMeshUpdate();
            var info = text.textInfo; if (info == null || info.characterCount <= 0) return false;
            int visible = 0;
            for (int i = 0; i < info.characterCount && i < info.characterInfo.Length; i++) if (info.characterInfo[i].isVisible) visible++;
            return visible > 0 && visible == text.text.Count(c => !char.IsWhiteSpace(c));
        }

        bool SpectatorUiObjectsExist()
        {
            return Resources.FindObjectsOfTypeAll<GameObject>().Any(g => g != null && g.name == "Spectator Overlay");
        }

        // ---- Lab tour: -bt-lab -bt-spectator-ui-tour ----
        void UpdateSpectatorUiLab()
        {
            if (!Lab.Enabled || !TitleVisible || spectatorUiLabStarted || !Lab.HasArg("-bt-spectator-ui-tour")) return;
            spectatorUiLabStarted = true; StartCoroutine(TourSpectatorUi());
        }

        readonly Dictionary<string, int> spectatorUiCaptureBytes = new Dictionary<string, int>();
        readonly Dictionary<string, float> spectatorUiCaptureBrightness = new Dictionary<string, float>();

        // Headless-safe capture: the lab window is hidden, so ScreenCapture returns nothing. For this one render the
        // overlay becomes a Screen Space Camera canvas on the player camera, which draws the world and the overlay
        // into a temporary texture. Canvas mode, camera target, culling mask and RenderTexture.active are restored.
        IEnumerator CaptureSpectatorUi(string state)
        {
            yield return null;
            spectatorUiCaptureBytes[state] = 0; spectatorUiCaptureBrightness[state] = 0;
            Camera camera = PlayerCamera;
            if (camera == null || spectatorUiCanvas == null) { Log("Spectator UI capture " + state + ": no camera or overlay"); yield break; }
            RenderTexture previousActive = RenderTexture.active, previousTarget = camera.targetTexture, temporary = null;
            int previousMask = camera.cullingMask;
            var previousMode = spectatorUiCanvas.renderMode; var previousCamera = spectatorUiCanvas.worldCamera; float previousPlane = spectatorUiCanvas.planeDistance;
            Texture2D screen = null;
            try
            {
                int width = Math.Max(1, Screen.width), height = Math.Max(1, Screen.height);
                temporary = new RenderTexture(width, height, 24);
                camera.targetTexture = temporary;
                camera.cullingMask = previousMask | (1 << spectatorUiRoot.layer);
                spectatorUiCanvas.renderMode = RenderMode.ScreenSpaceCamera; spectatorUiCanvas.worldCamera = camera;
                spectatorUiCanvas.planeDistance = camera.nearClipPlane + .01f;
                Canvas.ForceUpdateCanvases();
                camera.Render();
                RenderTexture.active = temporary;
                screen = new Texture2D(width, height, TextureFormat.RGB24, false);
                screen.ReadPixels(new Rect(0, 0, width, height), 0, 0); screen.Apply();
                var pixels = screen.GetPixels32(); double sum = 0;
                for (int i = 0; i < pixels.Length; i++) sum += (pixels[i].r * .299 + pixels[i].g * .587 + pixels[i].b * .114) / 255.0;
                float brightness = pixels.Length == 0 ? 0 : (float)(sum / pixels.Length);
                byte[] png = screen.EncodeToPNG();
                string folder = Path.Combine(Root, "MultiplayerMod/artifacts/tests"); Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, "spectator-ui-" + state + ".png"), png);
                spectatorUiCaptureBytes[state] = png.Length; spectatorUiCaptureBrightness[state] = brightness;
                Log("LAB spectator UI capture " + state + " " + width + "x" + height + " bytes=" + png.Length + " brightness=" + brightness.ToString("0.000"));
            }
            catch (Exception ex) { Log("Spectator UI capture " + state + ": " + ex.Message); }
            finally
            {
                if (spectatorUiCanvas != null)
                { spectatorUiCanvas.renderMode = previousMode; spectatorUiCanvas.worldCamera = previousCamera; spectatorUiCanvas.planeDistance = previousPlane; }
                camera.targetTexture = previousTarget; camera.cullingMask = previousMask;
                RenderTexture.active = previousActive;
                if (temporary != null) { temporary.Release(); Object.Destroy(temporary); }
                if (screen != null) Object.Destroy(screen);
            }
        }

        bool SpectatorUiCaptureOk(string state)
        { int bytes; return spectatorUiCaptureBytes.TryGetValue(state, out bytes) && bytes > 1024; }

        IEnumerator TourSpectatorUi()
        {
            CheckNativeUi("spectator-ui-absent-in-single-player", spectatorUiRoot == null && !SpectatorUiObjectsExist());
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            portText = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(); listener.Stop();
            Connect(true); if (!Active) { WriteLabResults("spectator-ui-results.json", "spectator UI"); yield break; }
            AutoStartRun(); while (!worldReady) yield return null;
            var core = Singleton<CoreGameManager>.Instance; var player = core.GetPlayer(0); var manager = Singleton<BaseGameManager>.Instance;
            if (manager.Ec.Npcs.Count == 0) manager.Ec.SpawnNPCs(); npcs = manager.Ec.Npcs.ToArray();
            var baldi = npcs.OfType<Baldi>().FirstOrDefault();
            if (baldi == null) { CheckNativeUi("spectator-ui-baldi-present", false); WriteLabResults("spectator-ui-results.json", "spectator UI"); yield break; }
            baldi.Navigator.enabled = false;
            Vector3 spawn = manager.Ec.spawnPoint; float yaw = manager.Ec.spawnRotation.eulerAngles.y;
            var alpha = new Peer { Id = 2, Name = "Alpha", Accepted = true, Ready = true, HasPose = true, Colour = 3, Position = spawn, Yaw = yaw, PoseAt = Time.realtimeSinceStartup };
            var beta = new Peer { Id = 3, Name = "Beta", Accepted = true, Ready = true, HasPose = true, Colour = 5, Position = spawn, Yaw = yaw + 180, PoseAt = Time.realtimeSinceStartup };
            var gamma = new Peer { Id = 1, Name = "Gamma", Accepted = true, Ready = true, HasPose = true, Colour = 1, Eliminated = true, Position = spawn, PoseAt = Time.realtimeSinceStartup };
            peers[2] = alpha; peers[3] = beta; peers[1] = gamma;
            Action fresh = delegate { alpha.PoseAt = beta.PoseAt = gamma.PoseAt = Time.realtimeSinceStartup; };
            UpdateSpectator();
            CheckNativeUi("spectator-ui-hidden-while-alive", !LocalEliminated && (spectatorUiRoot == null || !spectatorUiRoot.activeSelf));

            core.GetCamera(0).SetControllable(false);
            baldi.CaughtPlayer(player);
            fresh(); UpdateSpectator();
            CheckNativeUi("spectator-ui-shown-when-caught", spectatorUiRoot != null && spectatorUiRoot.activeSelf && spectatorUiCanvas.enabled && spectatorUiState == "watching" && spectatorUiCard.activeInHierarchy && !spectatorUiConceal.activeSelf);
            var sprites = PlayerSpriteSheet.SpritesFor(3);
            CheckNativeUi("spectator-ui-header-names-teammate", spectatorTarget == 2 && spectatorUiCaption.text == "SPECTATING" && spectatorUiName.text == "Alpha"
                && spectatorUiSwatch.color == SpectatorSwatch(3) && (sprites == null || spectatorUiPortrait.sprite == sprites[0]) && spectatorUiName.font == SpectatorFont());
            CheckNativeUi("spectator-ui-roster-every-player", spectatorUiRows.Count == 4 && SpectatorRowStatus(self) == "CAUGHT" && SpectatorRowStatus(1) == "CAUGHT"
                && SpectatorRowStatus(2) == "ALIVE" && SpectatorRowStatus(3) == "ALIVE" && spectatorUiRows.Single(r => r.Id == 2).Highlight.enabled && !spectatorUiRows.Single(r => r.Id == 3).Highlight.enabled);
            var rosterTitle = spectatorUiRoster.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t => t.name == "Roster Title");
            CheckNativeUi("spectator-ui-header-and-roster-text-rendered", SpectatorTextRendered(spectatorUiCaption) && SpectatorTextRendered(spectatorUiName)
                && SpectatorTextRendered(rosterTitle) && spectatorUiRows.Count == 4 && spectatorUiRows.All(row => SpectatorTextRendered(row.Name) && SpectatorTextRendered(row.Status))
                && SpectatorTextRendered(spectatorUiHint));
            CheckNativeUi("spectator-ui-control-hints", spectatorUiHint.text.Contains("LEFT") && spectatorUiHint.text.Contains("RIGHT") && spectatorUiHint.text.Contains("TAB"));
            float until = Time.realtimeSinceStartup + .6f; while (Time.realtimeSinceStartup < until) { fresh(); yield return null; }
            fresh(); yield return StartCoroutine(CaptureSpectatorUi("watching"));

            int switches = SoundCount("switch");
            fresh(); labSpectatorStep = 1; UpdateSpectator();
            CheckNativeUi("spectator-ui-switch-sound-and-dissolve", spectatorTarget == 3 && SoundCount("switch") == switches + 1 && spectatorUiDither.gameObject.activeSelf
                && spectatorUiName.text == "Beta" && spectatorUiRows.Single(r => r.Id == 3).Highlight.enabled);
            fresh(); yield return StartCoroutine(CaptureSpectatorUi("switch"));
            until = Time.realtimeSinceStartup + .5f; while (Time.realtimeSinceStartup < until) { fresh(); yield return null; }
            fresh(); UpdateSpectator();
            CheckNativeUi("spectator-ui-dissolve-is-brief", !spectatorUiDither.gameObject.activeSelf && SoundCount("switch") == switches + 1);

            beta.InLocker = true; fresh(); UpdateSpectator();
            CheckNativeUi("spectator-ui-hiding-concealed", spectatorBlank && spectatorTarget == 3 && spectatorUiState == "hiding" && spectatorUiConceal.activeSelf
                && spectatorUiConcealTitle.text == "Beta is hiding" && spectatorUiConcealShade.color.a < 1 && SpectatorRowStatus(3) == "HIDING");
            CheckNativeUi("spectator-ui-hiding-title-rendered", SpectatorTextRendered(spectatorUiConcealTitle) && SpectatorTextRendered(spectatorUiConcealNote));
            until = Time.realtimeSinceStartup + .4f; while (Time.realtimeSinceStartup < until) { fresh(); yield return null; }
            fresh(); yield return StartCoroutine(CaptureSpectatorUi("hiding"));

            show = true; UpdateSpectator(); bool menuHidden = !spectatorUiRoot.activeSelf; show = false;
            SetDebugConsole(true); UpdateSpectator(); bool consoleHidden = !spectatorUiRoot.activeSelf; SetDebugConsole(false);
            fresh(); UpdateSpectator();
            CheckNativeUi("spectator-ui-hidden-with-menu-or-console", menuHidden && consoleHidden && spectatorUiRoot.activeSelf);

            beta.InLocker = false; alpha.HasPose = false; beta.HasPose = false; UpdateSpectator();
            CheckNativeUi("spectator-ui-waiting-state", spectatorTarget == -1 && spectatorUiState == "waiting" && spectatorUiConceal.activeSelf
                && spectatorUiConcealTitle.text == "WAITING FOR A TEAMMATE" && !spectatorUiPortrait.enabled
                && SpectatorTextRendered(spectatorUiConcealTitle) && SpectatorTextRendered(spectatorUiName));
            until = Time.realtimeSinceStartup + .2f; while (Time.realtimeSinceStartup < until) yield return null;
            UpdateSpectator(); yield return StartCoroutine(CaptureSpectatorUi("waiting"));

            deathSent = true; UpdateSpectator(); bool gameOverHidden = !spectatorUiRoot.activeSelf; deathSent = false;
            CheckNativeUi("spectator-ui-hidden-at-game-over", gameOverHidden);

            alpha.HasPose = beta.HasPose = true; fresh();
            GameObject oldRoot = spectatorUiRoot; Texture2D oldDither = spectatorUiDitherTexture;
            ReleaseSpectator(); bool released = spectatorUiRoot == null && spectatorUiCanvas == null;
            yield return null; released &= oldRoot == null && oldDither == null;
            fresh(); UpdateSpectator();
            CheckNativeUi("spectator-ui-release-destroys-and-rebuilds", released && spectatorUiRoot != null && spectatorUiRoot.activeSelf && spectatorTarget == 2);

            CheckNativeUi("spectator-ui-captures-written", new[] { "watching", "switch", "hiding", "waiting" }.All(SpectatorUiCaptureOk));
            float watchingBrightness;
            CheckNativeUi("spectator-ui-watching-capture-not-empty", SpectatorUiCaptureOk("watching") && spectatorUiCaptureBrightness.TryGetValue("watching", out watchingBrightness) && watchingBrightness > .03f);

            ResetWorld(); yield return null;
            CheckNativeUi("spectator-ui-reset-cleans-up", spectatorUiRoot == null && spectatorUiCanvas == null && spectatorUiDitherTexture == null && !SpectatorUiObjectsExist() && spectatorUiState == "hidden");
            foreach (var peer in new[] { alpha, beta, gamma }) { DestroyAvatar(peer); peers.Remove(peer.Id); }
            core.GetCamera(0).SetControllable(true);
            WriteLabResults("spectator-ui-results.json", "spectator UI");
        }
    }
}
