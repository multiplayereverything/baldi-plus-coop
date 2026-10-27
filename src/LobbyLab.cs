using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    // Lab-only tours for the 0.6 lobby features: team sounds, player colours, spectator HUD and LAN discovery.
    //   -bt-lab -bt-lobby-tour : one process, synthetic peers, actual handlers and native components.
    //   -bt-lab -bt-lan-tour   : client half of a two-process test; finds the host on FIND LAN GAMES and joins it.
    public sealed partial class TogetherRuntime
    {
        bool lobbyLabStarted, lanLabStarted;

        void UpdateLobbyLab()
        {
            if (!Lab.Enabled || !TitleVisible) return;
            string[] args = Environment.GetCommandLineArgs();
            if (!lobbyLabStarted && args.Contains("-bt-lobby-tour")) { lobbyLabStarted = true; StartCoroutine(TourLobby()); }
            if (!lanLabStarted && args.Contains("-bt-lan-tour")) { lanLabStarted = true; StartCoroutine(TourLanJoin()); }
        }

        void WriteLabResults(string file, string label)
        {
            string json = "{\"passed\":" + nativeUiChecks.Count(p => p.Value) + ",\"failed\":" + nativeUiChecks.Count(p => !p.Value) + ",\"checks\":{"
                + string.Join(",", nativeUiChecks.Select(p => "\"" + p.Key + "\":" + p.Value.ToString().ToLowerInvariant()).ToArray()) + "}}";
            string folder = Path.Combine(Lab.Workspace, "artifacts/tests");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, file), json);
            Log("LAB " + label + " " + json);
        }

        int SoundCount(string name) { return teamSoundLog.Count(n => n == name); }

        static bool Near(float a, float b, float tolerance) { return Math.Abs(a - b) <= tolerance; }

        static byte[] LanBeacon(string room, string version, string name, int port, int players, bool code, bool running)
        {
            return Encoding.UTF8.GetBytes(string.Join("\n", new[] { DiscoveryMagic, Wire.Version.ToString(), version, room, name, port.ToString(),
                players.ToString(), code ? "1" : "0", running ? "1" : "0" }));
        }

        IEnumerator TourLobby()
        {
            float now;
            // ---- Team sounds: loaded, game-ready format and levelled to the game's short SFX ----
            bool loaded = true, format = true, levels = true;
            foreach (string name in TeamSoundNames)
            {
                var sound = TeamSound(name);
                if (sound == null || sound.soundClip == null) { loaded = false; continue; }
                var clip = sound.soundClip;
                format &= clip.channels == 1 && clip.frequency == 44100 && clip.length > .1f && clip.length <= 1.25f && sound.soundType == SoundType.Effect && !sound.subtitle;
                var data = new float[clip.samples]; clip.GetData(data, 0);
                float peak = data.Max(v => Math.Abs(v)), rms = (float)Math.Sqrt(data.Average(v => v * v));
                float peakDb = 20 * Mathf.Log10(peak), rmsDb = 20 * Mathf.Log10(rms);
                // Levelled to -18 dB RMS unless the -6 dBFS peak cap bound first (sharp transients such as the bell).
                bool levelled = peakDb <= -5.5f && (Near(rmsDb, -18, 1.5f) || Near(peakDb, -6, .6f));
                levels &= levelled;
                Log("LAB sound " + name + ": " + clip.length.ToString("0.000") + "s peak " + peakDb.ToString("0.0") + " dBFS rms " + rmsDb.ToString("0.0") + " dB");
            }
            CheckNativeUi("sounds-all-six-loaded", loaded);
            CheckNativeUi("sounds-mono-44k-short-effect", format);
            CheckNativeUi("sounds-levelled-to-game", levels);

            var music = Singleton<MusicManager>.Instance;
            var musicSource = music == null ? null : AccessTools.Field(typeof(MusicManager), "soundSource").GetValue(music) as AudioManager;
            PlayTeamSound("join");
            CheckNativeUi("sounds-play-through-native-sfx", musicSource != null
                && (AudioClip)AccessTools.Field(typeof(AudioManager), "_oneShotClip").GetValue(musicSource) == TeamSound("join").soundClip);
            CheckNativeUi("sounds-native-sfx-mixer-group", EffectsMixerGroup() != null && EffectsMixerGroup() == AudioSourceManager.sfxMixerGroup);

            // ---- Player colours: recolour rule and per-colour sheets ----
            var teal = new Color32(80, 214, 170, 255); var skin = new Color32(232, 205, 150, 255); var hair = new Color32(110, 70, 30, 255); var white = new Color32(250, 250, 250, 255);
            bool hues = true, untouched = true, sheets = true;
            for (int colour = 0; colour < PlayerSpriteSheet.ColourNames.Length; colour++)
            {
                var result = PlayerSpriteSheet.RecolourPixel(teal, colour);
                float target = PlayerSpriteSheet.TargetHue(colour), h, s, v; Color.RGBToHSV(result, out h, out s, out v);
                if (target == -1) hues &= result.Equals(teal);
                else if (target == -2) hues &= s < .1f && v > .8f;
                else hues &= Math.Min(Math.Abs(h - target), 1 - Math.Abs(h - target)) < .03f && s > .4f;
                untouched &= PlayerSpriteSheet.RecolourPixel(skin, colour).Equals(skin) && PlayerSpriteSheet.RecolourPixel(hair, colour).Equals(hair) && PlayerSpriteSheet.RecolourPixel(white, colour).Equals(white);
                var frames = PlayerSpriteSheet.SpritesFor(colour);
                sheets &= frames != null && frames.Length == 16 && frames.All(f => f != null) && (colour == 0 || frames[0].texture != PlayerSpriteSheet.Sprites[0].texture);
            }
            CheckNativeUi("colour-clothing-takes-target-hue", hues);
            CheckNativeUi("colour-skin-hair-eyes-unchanged", untouched);
            CheckNativeUi("colour-eight-sheets-sixteen-frames", sheets);

            // ---- Player Options colour picker ----
            byte startColour = PlayerColour;
            SetPanel(true); OpenNativeOptions(2); yield return null;
            TMPro.TextMeshProUGUI pickerLabel;
            bool picker = nativeLabels.TryGetValue("player-colour", out pickerLabel) && pickerLabel.text == NativeColourLabel();
            CyclePlayerColour();
            picker &= PlayerColour == (startColour + 1) % 8 && pickerLabel.text == "COLOR: " + PlayerSpriteSheet.ColourNames[PlayerColour];
            picker &= nativeColourPreview != null && nativeColourPreview.sprite == PlayerSpriteSheet.SpritesFor(PlayerColour)[0];
            for (int i = 0; i < 7; i++) CyclePlayerColour();
            picker &= PlayerColour == startColour;
            CheckNativeUi("colour-picker-cycles-labels-and-previews", picker);

            // ---- LAN page: beacon parsing, de-duplication and the found cue ----
            nativePage = LanPage; lanRooms.Clear(); BuildNativeMenu();
            now = Time.realtimeSinceStartup;
            int foundBefore = SoundCount("found");
            string roomA = Guid.NewGuid().ToString("N"), roomB = Guid.NewGuid().ToString("N");
            var lan = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("10.0.0.5"), DiscoveryPort);
            var loop = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, DiscoveryPort);
            ReadLanBeacon(Encoding.UTF8.GetBytes("not a beacon"), lan, now);
            ReadLanBeacon(LanBeacon("short-id", ModVersion, "Bad", 27015, 1, false, false), lan, now);
            ReadLanBeacon(LanBeacon(roomA, ModVersion, "Bad port", 80, 1, false, false), lan, now);
            CheckNativeUi("lan-rejects-malformed-beacons", lanRooms.Count == 0);
            ReadLanBeacon(LanBeacon(roomA, ModVersion, "Alex", 27040, 2, true, false), lan, now);
            ReadLanBeacon(LanBeacon(roomA, ModVersion, "Alex", 27040, 2, true, false), lan, now);
            ReadLanBeacon(LanBeacon(roomA, ModVersion, "Alex", 27040, 2, true, false), loop, now);
            LanRoom listed;
            CheckNativeUi("lan-lists-room", lanRooms.TryGetValue(roomA, out listed) && listed.Name == "Alex" && listed.Port == 27040 && listed.Players == 2 && listed.NeedsCode && listed.Compatible);
            CheckNativeUi("lan-prefers-lan-address-over-loopback", listed != null && listed.Address == "10.0.0.5");
            CheckNativeUi("lan-found-sound-once-per-room", SoundCount("found") == foundBefore + 1);
            ReadLanBeacon(LanBeacon(roomB, "0.0.1", "Old", 27041, 1, false, false), lan, now);
            CheckNativeUi("lan-marks-other-versions", lanRooms.ContainsKey(roomB) && !lanRooms[roomB].Compatible);
            BuildNativeMenu();
            CheckNativeUi("lan-page-shows-rooms", nativeLabels.ContainsKey("lan-0") && nativeLabels.ContainsKey("lan-1"));
            JoinLanRoom(listed);
            yield return null; while (Singleton<GlobalCam>.Instance != null && Singleton<GlobalCam>.Instance.TransitionActive) yield return null;
            CheckNativeUi("lan-code-room-opens-join-form", !Active && nativePage == 1 && joining && nativeEditing == "code" && Address == "10.0.0.5" && portText == "27040");
            lanRooms.Clear(); Address = "127.0.0.1"; SetPanel(false);

            // ---- Host a real room: loopback beacon round trip, join and leave cues ----
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            portText = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(); listener.Stop();
            Code = ""; Connect(true); if (!Active) { CheckNativeUi("host-opened", false); WriteLabResults("lobby-results.json", "lobby"); yield break; }
            bool roundTrip = false; float until = Time.realtimeSinceStartup + 6; labLanListen = true;
            while (!roundTrip && Time.realtimeSinceStartup < until)
            {
                PollLanRooms(Time.realtimeSinceStartup);
                LanRoom own; roundTrip = lanRooms.TryGetValue(migrationRoom, out own) && own.Port == Port && own.Players == 1 && !own.NeedsCode && own.Compatible;
                yield return null;
            }
            CheckNativeUi("lan-real-beacon-loopback-roundtrip", roundTrip);
            labLanListen = false; CloseLanListener();

            int joins = SoundCount("join"), leaves = SoundCount("leave");
            var stranger = new Peer { Id = 2, Joined = Time.realtimeSinceStartup }; peers[2] = stranger;
            var hello = Wire.Packet(Op.Hello, w =>
            {
                w.Write(Wire.Version); Wire.Text(w, ModVersion); Wire.Text(w, gameHash); Wire.Text(w, "Lab friend"); Wire.Text(w, Code);
                Wire.Text(w, Guid.NewGuid().ToString("N")); w.Write((byte)1); Wire.Text(w, "127.0.0.1"); w.Write((byte)6);
            });
            using (var reader = new BinaryReader(new MemoryStream(hello))) { reader.ReadByte(); HandleClient(2, stranger, Op.Hello, reader); }
            CheckNativeUi("join-accepts-colour-from-identity", stranger.Accepted && stranger.Colour == 6);
            CheckNativeUi("join-sound-on-host", SoundCount("join") == joins + 1);
            HandleEvent(new NetworkEvent { Peer = 2, Kind = "disconnected" });
            CheckNativeUi("leave-sound-on-host", SoundCount("leave") == leaves + 1 && !peers.ContainsKey(2));

            // ---- Run: ping, capture, spectator HUD, switch, restore ----
            AutoStartRun(); while (!worldReady) yield return null;
            yield return new WaitForSeconds(1.5f);
            CheckNativeUi("lan-beacon-stops-during-run", lanBeacon == null);
            var core = Singleton<CoreGameManager>.Instance; var player = core.GetPlayer(0); var manager = Singleton<BaseGameManager>.Instance;
            if (manager.Ec.Npcs.Count == 0) manager.Ec.SpawnNPCs(); npcs = manager.Ec.Npcs.ToArray();
            var baldi = npcs.OfType<Baldi>().FirstOrDefault();
            if (baldi == null) { CheckNativeUi("baldi-present", false); WriteLabResults("lobby-results.json", "lobby"); yield break; }
            baldi.Navigator.enabled = false;
            var items = Resources.FindObjectsOfTypeAll<ItemObject>().Where(i => i != null && i.itemSpriteSmall != null && i.itemType != Items.None)
                .GroupBy(i => i.name).Select(g => g.First()).OrderBy(i => i.name, StringComparer.Ordinal).Take(4).ToArray();
            var alpha = new Peer { Id = 2, Name = "Alpha", Accepted = true, Ready = true, HasPose = true, Colour = 3, Position = player.transform.position + Vector3.forward * 30, PoseAt = Time.realtimeSinceStartup };
            var beta = new Peer { Id = 3, Name = "Beta", Accepted = true, Ready = true, HasPose = true, Colour = 5, Position = player.transform.position + Vector3.right * 30, PoseAt = Time.realtimeSinceStartup };
            peers[2] = alpha; peers[3] = beta;
            // Alpha's HUD arrives through the real client handler; Beta's is stored directly.
            var hudPacket = Wire.Packet(Op.PlayerHud, w => { w.Write(epoch); WriteHud(w, .5f, 1, new[] { items[0].name, items[1].name, "" }); });
            using (var reader = new BinaryReader(new MemoryStream(hudPacket))) { reader.ReadByte(); HandleClient(2, alpha, Op.PlayerHud, reader); }
            CheckNativeUi("hud-packet-stored-from-client", alpha.HudItems != null && alpha.HudItems.Length == 3 && alpha.HudSelected == 1 && Near(alpha.HudStamina, .5f, .001f));
            StoreHud(beta, 1.2f, 0, new[] { items[2].name, items[3].name, items[0].name, "", "" });
            UpdateAvatars();
            CheckNativeUi("avatar-renders-synced-colour", alpha.Visual != null && alpha.Visual.Colour == 3 && beta.Visual != null && beta.Visual.Colour == 5);

            ShowPing(2, player.transform.position + Vector3.forward * 12);
            Marker marker; AudioSource pingSource = markers.TryGetValue(2, out marker) && marker.Object != null ? marker.Object.GetComponent<AudioSource>() : null;
            CheckNativeUi("ping-plays-3d-at-marker", pingSource != null && pingSource.spatialBlend == 1 && pingSource.isPlaying && pingSource.outputAudioMixerGroup != null && pingSource.outputAudioMixerGroup == AudioSourceManager.sfxMixerGroup && SoundCount("ping") >= 1);

            int caught = SoundCount("caught");
            core.GetCamera(0).SetControllable(false);
            baldi.CaughtPlayer(player);
            CheckNativeUi("caught-sound-on-capture", LocalEliminated && SoundCount("caught") == caught + 1);
            alpha.PoseAt = beta.PoseAt = Time.realtimeSinceStartup; alpha.HudAt = beta.HudAt = Time.realtimeSinceStartup;
            UpdateSpectator();
            var hud = core.GetHud(0);
            var icons = (Image[])AccessTools.Field(typeof(HudManager), "itemSprites").GetValue(hud);
            Func<float> needle = () => (float)AccessTools.Field(typeof(HudManager), "needleTargetValue").GetValue(hud);
            Func<int> slots = () => 9 - Mathf.RoundToInt(hud.inventory.rectTransform.anchoredPosition.x / 40f);
            var empty = player.itm.nothing;
            CheckNativeUi("spectator-hud-shows-first-teammate", spectatorTarget == 2 && hud.gameObject.activeSelf && slots() == 3
                && icons[0].sprite == items[0].itemSpriteSmall && icons[1].sprite == items[1].itemSpriteSmall && icons[2].sprite == empty.itemSpriteSmall && Near(needle(), .5f, .001f));

            int switches = SoundCount("switch");
            labSpectatorStep = 1; UpdateSpectator();
            CheckNativeUi("switch-sound-on-teammate-change", spectatorTarget == 3 && SoundCount("switch") == switches + 1);
            CheckNativeUi("spectator-hud-follows-switch", slots() == 5 && icons[0].sprite == items[2].itemSpriteSmall && icons[2].sprite == items[0].itemSpriteSmall && Near(needle(), 1.2f, .001f));
            UpdateSpectator();
            CheckNativeUi("no-switch-sound-without-change", SoundCount("switch") == switches + 1);

            beta.HudAt = Time.realtimeSinceStartup - 10; UpdateSpectator();
            CheckNativeUi("spectator-hud-hides-stale-data", !hud.gameObject.activeSelf);
            beta.HudAt = Time.realtimeSinceStartup; UpdateSpectator();

            int remoteCaught = SoundCount("caught");
            var round = Wire.Packet(Op.RoundState, w => { w.Write(epoch); w.Write((byte)1); w.Write(2); w.Write(true); });
            for (int i = 0; i < 2; i++) using (var reader = new BinaryReader(new MemoryStream(round))) { reader.ReadByte(); HandleRoundHost(Op.RoundState, reader); }
            CheckNativeUi("caught-sound-once-per-remote-capture", alpha.Eliminated && SoundCount("caught") == remoteCaught + 1);

            ResetWorld(); yield return null;
            var local = player.itm; float localStamina = player.plm.StaminaMax > 0 ? player.plm.stamina / player.plm.StaminaMax : 1;
            bool restored = hud.gameObject.activeSelf && !LocalEliminated && Near(needle(), localStamina, .01f);
            for (int i = 0; i <= local.maxItem && i < icons.Length; i++) restored &= icons[i].sprite == local.items[i].itemSpriteSmall;
            CheckNativeUi("own-hud-restored-after-spectating", restored);
            core.GetCamera(0).SetControllable(true);
            WriteLabResults("lobby-results.json", "lobby");
        }

        // Client half of the two-process LAN test. The host is a normal lab host on port 27015.
        IEnumerator TourLanJoin()
        {
            SetPanel(true); OpenNativeOptions(LanPage);
            LanRoom room = null; float until = Time.realtimeSinceStartup + 40;
            while (room == null && Time.realtimeSinceStartup < until)
            {
                room = lanRooms.Values.FirstOrDefault(r => r.Port == 27015 && r.Compatible && !r.Running);
                yield return null;
            }
            CheckNativeUi("lan-host-listed-on-page", room != null);
            CheckNativeUi("lan-found-sound-on-client", SoundCount("found") >= 1);
            if (room == null) { WriteLabResults("lan-tour-results.json", "LAN tour"); yield break; }
            // The page rebuilds its rows on the frame after the room list changes.
            float rowUntil = Time.realtimeSinceStartup + 2;
            while (!nativeLabels.ContainsKey("lan-0") && Time.realtimeSinceStartup < rowUntil) yield return null;
            CheckNativeUi("lan-page-row-built", nativeLabels.ContainsKey("lan-0"));
            JoinLanRoom(room);
            until = Time.realtimeSinceStartup + 20;
            while (!(self >= 1 && peers.ContainsKey(0)) && Time.realtimeSinceStartup < until) yield return null;
            CheckNativeUi("lan-joined-without-typing-address", self >= 1 && Address == room.Address && Port == 27015);
            // Only the host starts a run: the client's real Hide & Seek New and Continue buttons do nothing but explain.
            var buttons = Resources.FindObjectsOfTypeAll<StandardMenuButton>().Where(btn => btn != null && btn.gameObject.scene.IsValid()).ToArray();
            var newRun = buttons.FirstOrDefault(btn => btn.name == "MainNew" && btn.transform.parent != null && btn.transform.parent.name == "HideSeekMenu");
            var continueRun = buttons.FirstOrDefault(btn => btn.name == "MainContinue");
            var elevator = Resources.FindObjectsOfTypeAll<ElevatorScreen>().FirstOrDefault(e => e != null && e.gameObject.scene.IsValid());
            var hideSeekMenu = newRun == null ? null : newRun.transform.parent.gameObject;
            bool menuWasActive = hideSeekMenu != null && hideSeekMenu.activeSelf, coreBefore = Singleton<CoreGameManager>.Instance != null;
            toast = null;
            if (newRun != null) newRun.Press();
            if (continueRun != null) continueRun.Press();
            CheckNativeUi("client-cannot-start-run", newRun != null && continueRun != null && clientStartBlocks == 2 && (elevator == null || !elevator.gameObject.activeSelf)
                && (hideSeekMenu.activeSelf == menuWasActive) && (Singleton<CoreGameManager>.Instance != null) == coreBefore && toast != null && toast.Contains("Only the host"));
            var other = buttons.FirstOrDefault(btn => !StartsRun(btn));
            CheckNativeUi("client-other-menu-buttons-unaffected", other != null && !BlockClientRunStart(other) && clientStartBlocks == 2);
            Peer host; peers.TryGetValue(0, out host);
            CheckNativeUi("roster-carries-host-colour", host != null && host.Colour == 1);
            until = Time.realtimeSinceStartup + 120;
            while (!(worldReady && readyAcknowledged && host != null && host.HudItems != null && host.Visual != null) && Time.realtimeSinceStartup < until)
            { peers.TryGetValue(0, out host); yield return null; }
            CheckNativeUi("hud-data-from-host", host != null && host.HudItems != null && Time.realtimeSinceStartup - host.HudAt < 3);
            CheckNativeUi("host-avatar-in-host-colour", host != null && host.Visual != null && host.Visual.Colour == 1);
            WriteLabResults("lan-tour-results.json", "LAN tour");
        }
    }
}
