using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    static class PlayerSpriteSheet
    {
        public static readonly int[][] Frames = { new[] { 0,1,2,3 }, new[] { 4,5,6,7 }, new[] { 8,9,12,13 }, new[] { 10,11,14,15 } };
        public static Sprite[] Sprites;
        public static Material Material;
        // Index 0 keeps the supplied artwork; the others recolour only its teal clothing.
        public static readonly string[] ColourNames = { "GREEN", "RED", "ORANGE", "YELLOW", "BLUE", "PURPLE", "PINK", "WHITE" };
        static readonly float[] ColourHues = { -1, 0f, .07f, .14f, .6f, .77f, .9f, -2 };
        static readonly Sprite[][] colourSprites = new Sprite[8][];
        static readonly Texture2D[] colourTextures = new Texture2D[8];
        static int[] cropTops = { 18,385,768,1136 }, cropHeights = { 340,355,351,339 }, cropFeet = { 8,8,7,0 };
        public static int RenderLayer;
        public static bool TransparentCorners;
        static Texture2D texture;
        static bool attempted;

        public static bool Load()
        {
            if (attempted) return Sprites != null;
            attempted = true;
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BaldiTogether.PlayerSheet.png"))
                {
                    if (stream == null) throw new IOException("Embedded player sheet is missing.");
                    using (MemoryStream bytes = new MemoryStream())
                    {
                        stream.CopyTo(bytes);
                        texture = new Texture2D(2,2,TextureFormat.RGBA32,false);
                        if (!texture.LoadImage(bytes.ToArray())) throw new IOException("Player sheet could not be decoded.");
                    }
                }
                if (texture.width != 720 || texture.height != 1475) throw new IOException("Unexpected player sheet dimensions.");
                texture.name = "User supplied player sheet";
                texture.filterMode = FilterMode.Point;
                texture.wrapMode = TextureWrapMode.Clamp;
                TransparentCorners = true;
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 4; col++)
                    {
                        int x = 44 + col * 172;
                        TransparentCorners &= texture.GetPixel(x,texture.height-cropTops[row]-cropHeights[row]).a < 0.02f;
                    }
                Sprites = CutFrames(texture);
                colourSprites[0] = Sprites;
                TogetherRuntime.Log("Loaded supplied player sheet: 16 frames, four directions, transparent=" + TransparentCorners);
                return true;
            }
            catch (Exception ex)
            {
                TogetherRuntime.Log("Player sprite load failed: " + ex.Message);
                Dispose(); attempted = true;
                return false;
            }
        }

        // Use the same depth-writing, alpha-clipped billboard as native characters.
        // Sprites/Default blends over native sprites without writing matching depth.
        // Retried until NPC assets are loaded; the title-screen colour preview needs only the sprites.
        public static bool EnsureMaterial()
        {
            if (Material != null) return true;
            if (!Load()) return false;
            var native = Resources.FindObjectsOfTypeAll<NPC>()
                .SelectMany(n => n.GetComponentsInChildren<SpriteRenderer>(true))
                .FirstOrDefault(r => r.sharedMaterial != null && r.sharedMaterial.shader != null && r.sharedMaterial.shader.name == "SpriteStandard_NoBatching");
            if (native == null) return false;
            Material = new Material(native.sharedMaterial) { name = "Baldi Together player sprite" };
            RenderLayer = native.gameObject.layer;
            return true;
        }

        // The supplied sheet has irregular row heights. Keep the side labels outside every crop.
        static Sprite[] CutFrames(Texture2D sheet)
        {
            Sprite[] frames = new Sprite[16];
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                {
                    int x = 44 + col * 172, width = Math.Min(164,sheet.width-x);
                    Rect rect = new Rect(x,sheet.height-cropTops[row]-cropHeights[row],width,cropHeights[row]);
                    float pixelsPerUnit = (cropHeights[row]-cropFeet[row]) / 7.8f;
                    frames[row*4+col] = Sprite.Create(sheet,rect,new Vector2(0.5f,cropFeet[row]/(float)cropHeights[row]),pixelsPerUnit,0,SpriteMeshType.FullRect);
                    frames[row*4+col].name = "Player frame " + (row*4+col);
                }
            return frames;
        }

        public static int ClampColour(int colour) { return colour < 0 || colour >= ColourNames.Length ? 0 : colour; }

        // Built once per colour on first use; skin, hair and eyes keep their original pixels.
        public static Sprite[] SpritesFor(int colour)
        {
            colour = ClampColour(colour);
            if (!Load()) return null;
            if (colourSprites[colour] != null) return colourSprites[colour];
            Color32[] pixels = texture.GetPixels32();
            for (int i = 0; i < pixels.Length; i++) pixels[i] = RecolourPixel(pixels[i], colour);
            Texture2D sheet = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            sheet.name = "Player sheet " + ColourNames[colour]; sheet.filterMode = FilterMode.Point; sheet.wrapMode = TextureWrapMode.Clamp;
            sheet.SetPixels32(pixels); sheet.Apply(false, true);
            colourTextures[colour] = sheet;
            return colourSprites[colour] = CutFrames(sheet);
        }

        // Teal clothing is green-dominant with low red; skin, hair, eyes and outlines are left as they are.
        public static Color32 RecolourPixel(Color32 c, int colour)
        {
            float hue = ColourHues[ClampColour(colour)];
            if (hue == -1 || c.a == 0 || c.g <= c.r + 24 || c.b <= c.r) return c;
            float h, s, v; Color.RGBToHSV(c, out h, out s, out v);
            if (h < .38f || h > .55f || s < .2f) return c;
            Color result = hue == -2 ? Color.HSVToRGB(0, s * .08f, Mathf.Min(1, v * 1.08f)) : Color.HSVToRGB(hue, Mathf.Min(1, s * 1.1f), v);
            return new Color32((byte)(result.r * 255), (byte)(result.g * 255), (byte)(result.b * 255), c.a);
        }

        public static float TargetHue(int colour) { return ColourHues[ClampColour(colour)]; }

        public static void Dispose()
        {
            for (int i = 1; i < colourSprites.Length; i++)
            {
                if (colourSprites[i] != null) foreach (Sprite sprite in colourSprites[i]) if (sprite != null) Object.Destroy(sprite);
                if (colourTextures[i] != null) Object.Destroy(colourTextures[i]);
                colourSprites[i] = null; colourTextures[i] = null;
            }
            colourSprites[0] = null;
            if (Sprites != null) foreach (Sprite sprite in Sprites) if (sprite != null) Object.Destroy(sprite);
            if (Material != null) Object.Destroy(Material);
            if (texture != null) Object.Destroy(texture);
            Sprites = null; Material = null; texture = null; attempted = false;
        }
    }

    public sealed class RemotePlayerSprite : MonoBehaviour
    {
        public static int DirectionsSeen, WalkFramesSeen;
        public static bool IdleSeen;
        public int Direction { get; private set; }
        public int Frame { get; private set; }
        public int Colour { get; private set; }
        Sprite[] sheet;
        SpriteRenderer display;
        Vector3 previousPose;
        float yaw, walkDistance, movedAt;
        bool initialized;

        void Awake()
        {
            GameObject visual = new GameObject("Animated player sprite", typeof(SpriteRenderer));
            visual.layer = PlayerSpriteSheet.RenderLayer;
            visual.transform.SetParent(transform,false);
            // Player/entity origins are five units above the floor. Sprite pivots sit at the feet.
            visual.transform.localPosition = Vector3.down * 5;
            display = visual.GetComponent<SpriteRenderer>();
            display.sharedMaterial = PlayerSpriteSheet.Material;
            display.sprite = PlayerSpriteSheet.Sprites[0];
            display.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            display.receiveShadows = false;
        }

        public void SetColour(int colour)
        {
            colour = PlayerSpriteSheet.ClampColour(colour);
            if (sheet != null && Colour == colour) return;
            Colour = colour; sheet = PlayerSpriteSheet.SpritesFor(colour);
        }

        public void SetPose(Vector3 position, float facing)
        {
            yaw = facing;
            if (initialized)
            {
                Vector3 delta = position-previousPose; delta.y = 0;
                float distance = delta.magnitude;
                if (distance > 0.01f && distance < 20) { walkDistance += distance; movedAt = Time.realtimeSinceStartup; }
                else if (distance >= 20) { walkDistance = 0; movedAt = 0; }
            }
            else { initialized = true; walkDistance = movedAt = 0; }
            previousPose = position;
        }

        void OnDisable() { initialized = false; }

        void LateUpdate()
        {
            Camera camera = TogetherRuntime.PlayerCamera;
            if (camera == null || PlayerSpriteSheet.Sprites == null) return;
            Vector3 toViewer = camera.transform.position-transform.position; toViewer.y = 0;
            Vector3 forward = Quaternion.Euler(0,yaw,0) * Vector3.forward;
            float angle = Vector3.SignedAngle(forward,toViewer,Vector3.up);
            Direction = Mathf.Abs(angle) > 135 ? 1 : angle > 45 ? 3 : angle < -45 ? 2 : 0;
            bool moving = movedAt > 0 && Time.realtimeSinceStartup-movedAt < 0.18f;
            Frame = moving ? (int)(walkDistance / 1.4f) % 4 : 0;
            if (!moving) { walkDistance = 0; IdleSeen = true; }
            DirectionsSeen |= 1 << Direction;
            if (moving) WalkFramesSeen |= 1 << Frame;
            display.sprite = (sheet ?? PlayerSpriteSheet.Sprites)[PlayerSpriteSheet.Frames[Direction][Frame]];
            Vector3 facing = camera.transform.forward; facing.y = 0;
            if (facing.sqrMagnitude > 0.001f) display.transform.rotation = Quaternion.LookRotation(facing,Vector3.up);
        }
    }

    public sealed partial class TogetherRuntime
    {
        public static Camera PlayerCamera
        {
            get
            {
                var core = Singleton<CoreGameManager>.Instance;
                return core != null && core.GetCamera(0) != null ? core.GetCamera(0).camCom : Camera.main;
            }
        }

        void QueueScreenshot(string path) { StartCoroutine(CaptureFrame(path)); }
        IEnumerator CaptureFrame(string path)
        {
            yield return new WaitForEndOfFrame();
            RenderTexture previous = RenderTexture.active;
            Camera camera = worldReady ? PlayerCamera : null;
            RenderTexture previousTarget = camera != null ? camera.targetTexture : null;
            RenderTexture temporary = null;
            Texture2D screen = null;
            try
            {
                // The menu keeps a stale UI texture during play. Render the live player camera for evidence.
                var global = Singleton<GlobalCam>.Instance;
                RenderTexture output = global != null && global.Cam != null ? global.Cam.targetTexture : null;
                if (camera != null)
                {
                    temporary = new RenderTexture(Screen.width,Screen.height,24);
                    camera.targetTexture = temporary;
                    camera.Render();
                    output = temporary;
                }
                if (output != null)
                {
                    RenderTexture.active = output;
                    screen = new Texture2D(output.width,output.height,TextureFormat.RGB24,false);
                    screen.ReadPixels(new Rect(0,0,output.width,output.height),0,0); screen.Apply();
                }
                else
                {
                    RenderTexture.active = null;
                    screen = ScreenCapture.CaptureScreenshotAsTexture();
                }
                if (SpriteTour)
                    File.WriteAllLines(Path.ChangeExtension(path,"cameras.txt"),Camera.allCameras.Select(c => c.name + " / enabled=" + c.enabled + " / mask=" + c.cullingMask + " / target=" + (c.targetTexture == null ? "screen" : c.targetTexture.name + ":" + c.targetTexture.width + "x" + c.targetTexture.height)).ToArray());
                if (screen != null) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path,screen.EncodeToPNG()); }
            }
            catch (Exception ex) { Log("Screenshot: " + ex.Message); }
            finally
            {
                if (camera != null) camera.targetTexture = previousTarget;
                RenderTexture.active = previous;
                if (temporary != null) { temporary.Release(); Object.Destroy(temporary); }
                if (screen != null) Object.Destroy(screen);
            }
        }

        float spriteTourStart;
        float spriteTourYaw;
        bool spriteTourPose;
        int spriteCaptureMask;
        bool spriteTour;
        bool SpriteTour { get { return spriteTour; } }

        void UpdateSpriteTour()
        {
            if (!SpriteTour || !worldReady) return;
            if (!Hosting && testStep >= 11 && epoch == 1)
            {
                float now = Time.realtimeSinceStartup;
                if (spriteTourStart == 0) { spriteTourStart = now; Log("LAB: beginning supplied-sprite facing and walk tour."); }
                float elapsed = now-spriteTourStart;
                spriteTourPose = elapsed < 14;
                if (elapsed < 14)
                {
                    int direction = (int)(elapsed / 3.5f);
                    var manager = Singleton<BaseGameManager>.Instance;
                    var player = Singleton<CoreGameManager>.Instance.GetPlayer(0);
                    player.Teleport(manager.Ec.spawnPoint + manager.Ec.spawnRotation * new Vector3(3 + Mathf.Sin(elapsed * 5) * 0.85f,0,12));
                    player.transform.rotation = manager.Ec.spawnRotation * Quaternion.Euler(0,new[] {180f,0f,-90f,90f}[direction],0);
                    spriteTourYaw = player.transform.eulerAngles.y;
                }
            }
            if (!Hosting || epoch != 1) return;
            Camera camera = PlayerCamera;
            foreach (Peer peer in peers.Values.Where(p => p.Id != self && p.Visual != null && p.Avatar.activeSelf))
            {
                int bit = 1 << peer.Visual.Direction;
                Vector3 screen = camera.WorldToViewportPoint(peer.Avatar.transform.position);
                float distance = Vector3.Distance(camera.transform.position,peer.Avatar.transform.position);
                if ((spriteCaptureMask & bit) != 0 || distance < 6 || distance > 18 || screen.z < 0 || screen.x < 0.2f || screen.x > 0.8f || screen.y < 0.2f || screen.y > 0.8f) continue;
                spriteCaptureMask |= bit;
                QueueScreenshot(Path.Combine(Root,"MultiplayerMod/private/captures/player-direction-" + peer.Visual.Direction + ".png"));
            }
        }

        string SpriteDiagnostic()
        {
            return ",\"spriteAvatars\":" + peers.Values.Count(p => p.Id != self && p.Visual != null && p.Avatar.activeSelf)
                + ",\"spriteFrames\":" + (PlayerSpriteSheet.Sprites == null ? 0 : PlayerSpriteSheet.Sprites.Length)
                + ",\"spriteTransparent\":" + PlayerSpriteSheet.TransparentCorners.ToString().ToLowerInvariant()
                + ",\"spriteDirections\":" + RemotePlayerSprite.DirectionsSeen + ",\"spriteWalkingFrames\":" + RemotePlayerSprite.WalkFramesSeen
                + ",\"spriteIdle\":" + RemotePlayerSprite.IdleSeen.ToString().ToLowerInvariant();
        }
    }
}
