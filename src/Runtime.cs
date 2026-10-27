using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    public sealed class Peer
    {
        public int Id;
        public string Name;
        public bool Accepted, Ready, HasPose, Tagged, Invisible, InLocker, Eliminated;
        public byte Colour;
        public string[] HudItems;
        public int HudSelected;
        public float HudStamina, HudAt;
        public string ModVersion,GameHash;
        public float Joined, LastAction, PoseAt;
        public Vector3 Position;
        public float Yaw;
        public GameObject Avatar;
        public RemotePlayerSprite Visual;
    }

    public sealed partial class TogetherRuntime : MonoBehaviour
    {
        public const string ModVersion = "0.9.2";
        public static TogetherRuntime Instance;
        public static bool Applying;
        public static Action<string> Log = delegate { };
        public Transport Network;
        public bool Hosting { get { return Network != null && Network.Hosting; } }
        public bool Active { get { return Network != null; } }
        public bool Running { get { return epoch > 0; } }
        public bool WorldReady { get { return worldReady; } }
        public int Seed = 12345;
        public string Name = "Player", Address = "127.0.0.1", Code = "";
        public byte PlayerColour;
        public int Port = 27015;
        public string Status = "Host a LAN room or join a friend's address.";
        readonly Dictionary<int, Peer> peers = new Dictionary<int, Peer>();
        readonly Dictionary<string, Notebook> notebooks = new Dictionary<string, Notebook>();
        readonly Dictionary<string, StandardDoor> doors = new Dictionary<string, StandardDoor>();
        readonly Dictionary<string, Activity> activities = new Dictionary<string, Activity>();
        readonly List<Navigator> disabledNavigators = new List<Navigator>();
        readonly List<string> messages = new List<string>();
        NPC[] npcs = new NPC[0];
        string gameHash, worldHash = "", sceneName = "", pendingScene;
        int self = -1, epoch;
        bool worldReady, readyAcknowledged, show, deathSent;
        float nextSend, nextWorld, nextPing, nextDiagnostic, autoStartAt;
        float nextRegistry,nextClientLoad;
        bool autoStart, diagnostics;
        float nextCapture;
        float nextLabStep;
        bool warningAdvanced, nameSubmitted;
        int labWarningLogs;
        bool testActions;
        int testStep;
        float nextTest;
        string testNotebook, testDoor;
        bool beginRequested;
        int acceptedInteractions, observedDoorOpens, observedNpcFrames;
        string pendingNext;
        int pendingLives, pendingAttempts, pendingLastLevel, pendingMode, pendingLifeMode;
        bool pendingTimeLimit;
        string autoRole;
        // Lab auto-join retries while the host is still starting: two lab copies start at once and the client often
        // reached the title first, got "Connection refused" and sat idle until the test timed out.
        float autoRoleAt; int labJoinRetries; bool labAutoJoin;
        CursorLockMode previousLock;
        bool previousVisible;
        GUIStyle textStyle, fieldStyle, buttonStyle, titleStyle;
        Texture2D paper, ink, green;
        string portText = "27015", seedText = "12345";
        public static string Root { get { return Directory.GetParent(Application.dataPath).FullName; } }

        public static void Boot()
        {
            if (Instance != null) return;
            GameObject root = new GameObject("BaldiTogether");
            Object.DontDestroyOnLoad(root);
            Instance = root.AddComponent<TogetherRuntime>();
        }

        void Awake()
        {
            Instance = this;
            InitializeDebugConsole();
            using (SHA256 sha = SHA256.Create())
                gameHash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Path.Combine(Application.dataPath, "Managed/Assembly-CSharp.dll")))).Replace("-", "").ToLowerInvariant();
            LoadSettings();
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "-bt-host") autoRole = "host";
                if (arg == "-bt-join" && i + 1 < args.Length) { autoRole = "join"; Address = args[++i]; }
                if (arg == "-bt-name" && i + 1 < args.Length) Name = args[++i];
                if (arg == "-bt-colour" && Lab.Enabled && i + 1 < args.Length) { int colour; if (int.TryParse(args[++i], out colour)) PlayerColour = (byte)PlayerSpriteSheet.ClampColour(colour); }
                if (arg == "-bt-port" && i + 1 < args.Length) portText = args[++i];
                if (arg == "-bt-seed" && i + 1 < args.Length) seedText = args[++i];
                if (arg == "-bt-auto-start") autoStart = true;
                if (arg == "+connect_lobby" && i + 1 < args.Length) ulong.TryParse(args[++i], out steamLaunchLobby); // Steam invite while the game was closed
                if (arg == "-bt-diagnostics") diagnostics = true;
                if (arg == "-bt-test-actions" && Lab.Enabled) testActions = true;
                if (arg == "-bt-delay-ready" && Lab.Enabled && i + 1 < args.Length) { float delay; if (float.TryParse(args[++i], out delay)) testReadyDelay = Mathf.Clamp(delay, 0, 10); }
                if (arg == "-bt-test-transition" && Lab.Enabled) testTransition = true;
                if (arg == "-bt-sprite-tour" && Lab.Enabled) spriteTour = true;
                if (arg == "-bt-focused-sync" && Lab.Enabled) FocusedSync = true;
                if (arg == "-bt-flow-sync" && Lab.Enabled) flowSync = true;
                if (arg == "-bt-npc-sync" && Lab.Enabled) npcSyncLab = true;
            }
            autoStartAt = Time.realtimeSinceStartup + 20;
            Application.runInBackground = true;
            if (Lab.Enabled && Lab.HasArg("-bt-lab-light") && !Lab.HasArg("-bt-lab-uncapped")) // the soak measures with the game's own settings
            {
                // Lab copies share the PC with the user: lowest quality and 60 FPS.
                QualitySettings.SetQualityLevel(0, true); QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
            }
            Log("BaldiTogether " + ModVersion + " ready. F5 opens the LAN lobby. Game SHA256 " + gameHash);
        }

        void Tell(string message)
        {
            CaptureDebugMessage(message);
            Status = message;
            messages.Add(message);
            if (messages.Count > 8) messages.RemoveAt(0);
            Log(message);
        }

        void LoadSettings()
        {
            string path = Path.Combine(Root, "UserData/BaldiTogether.ini");
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                int split = line.IndexOf('='); if (split < 0) continue;
                string key = line.Substring(0, split), value = line.Substring(split + 1);
                if (key == "Name") Name = CleanName(value);
                if (key == "Address") Address = value.Substring(0, Math.Min(128, value.Length));
                if (key == "Port") portText = value;
                if (key == "Seed") seedText = value;
                if (key == "OnlinePublic") OnlinePublic = value.Trim() != "0";
                int colour; if (key == "Colour" && int.TryParse(value, out colour)) PlayerColour = (byte)PlayerSpriteSheet.ClampColour(colour);
            }
        }

        void SaveSettings()
        {
            if (Lab.Enabled) return;
            try
            {
                Directory.CreateDirectory(Path.Combine(Root, "UserData"));
                File.WriteAllLines(Path.Combine(Root, "UserData/BaldiTogether.ini"), new[] { "Name=" + Name, "Address=" + Address.Replace("\n", "").Replace("\r", ""), "Port=" + Port, "Seed=" + Seed, "Colour=" + PlayerColour, "OnlinePublic=" + (OnlinePublic ? 1 : 0) });
            }
            catch (Exception ex) { Log("Could not save lobby settings: " + ex.Message); }
        }

        static string CleanName(string name)
        {
            string clean = new string((name ?? "Player").Where(c => !char.IsControl(c) && c != '<' && c != '>').Take(24).ToArray()).Trim();
            return clean.Length == 0 ? "Player" : clean;
        }

        public void Connect(bool host)
        {
            if (Active) return;
            if (Singleton<CoreGameManager>.Instance != null && !TitleVisible) { Tell("Return to the main menu before hosting or joining."); return; }
            if (!Online && (!int.TryParse(portText, out Port) || Port < 1024 || Port > 65535)) { Tell("Port must be between 1024 and 65535."); return; }
            if (!Online && !host && string.IsNullOrEmpty(Address.Trim())) { Tell("Enter the host computer's LAN address."); return; }
            Name = CleanName(Name);
            ResetResultsSession();
            ClearCompatibilityStatus();latency.Clear();probes.Clear();latencySampleAt.Clear();
            try
            {
                if (Online)
                {
                    // Steam: lobby plus P2P through Valve's relay (SteamOnline.cs); no migration
                    if (ConnectOnline(host)) SaveSettings();
                    return;
                }
                Network = new Transport();
                if (host)
                {
                    Network.Host(Port, false); self = 0;
                    peers[0] = new Peer { Id = 0, Name = Name, Accepted = true,ModVersion=ModVersion,GameHash=gameHash,Colour=PlayerColour };
                    Tell("Room open on TCP " + Port + ". Friends join your LAN address. Start a new Hide & Seek run after they connect.");
                }
                else { Network.Join(Address.Trim(), Port); Tell("Connecting to " + Address + ":" + Port + "..."); }
                InitializeMigration(host);SaveSettings();
            }
            catch (Exception ex) { Disconnect("Connection failed: " + ex.Message); }
        }

        public void Disconnect(string reason, bool returnToMenu = true)
        {
            bool wasRunning = Running;
            ResetResultsSession();
            ResetMigration();latency.Clear();probes.Clear();latencySampleAt.Clear();
            ClearClientElevator();
            if (Network != null) Network.Dispose();
            Network = null;
            foreach (Peer peer in peers.Values) DestroyAvatar(peer);
            peers.Clear();
            ResetWorld(); epoch = 0; self = -1; pendingScene = null;
            Tell(reason);
            if (returnToMenu && wasRunning && Singleton<CoreGameManager>.Instance != null)
            {
                // A run can end while its elevator screen is still up (a failed level check during loading). The native
                // menu return keeps that screen, which then reads the destroyed CoreGameManager every frame.
                if (!(Lab.Enabled && Lab.HasArg("-bt-keep-elevator"))) // lab baseline switch
                    foreach (ElevatorScreen screen in Object.FindObjectsOfType<ElevatorScreen>()) Object.Destroy(screen.gameObject);
                Applying = true;
                try { Time.timeScale = 1; AudioListener.pause = false; Singleton<CoreGameManager>.Instance.ReturnToMenu(); }
                catch (Exception ex) { Log("Return to menu: " + ex.Message); }
                finally { Applying = false; }
            }
        }

        void ResetWorld()
        {
            bool retainDeaths=PreserveMigrationEliminations;
            ResetSpectator(!retainDeaths);
            if(retainDeaths){RestoreMigrationEliminations();CompleteMigrationFloorReset();}else ClearMigrationEliminations();
            ResetNpcInteractions(); ResetPrincipalSync(); ResetNpcPresentation(); ResetNpcFeatures();
            ResetHiding(); ResetShop(); ResetVending(); ResetReplication();
            ResetFeatures();
            foreach (Navigator navigator in disabledNavigators) if (navigator != null) navigator.enabled = true;
            disabledNavigators.Clear();
            notebooks.Clear(); doors.Clear(); activities.Clear(); npcs = new NPC[0];
            worldReady = readyAcknowledged = deathSent = false;
            beginRequested = false;
            worldHash = "";
            foreach (Peer peer in peers.Values) { peer.Ready = peer.HasPose = peer.InLocker = false; if (peer.Avatar != null) peer.Avatar.SetActive(false); }
            nextRegistry = Time.realtimeSinceStartup + 1;
        }

        float nextWorldErrorLog;
        void TrySendWorld()
        {
            try { SendWorld(); }
            catch (IOException ex)
            {
                if (ex.Message == "Packet too large.") { Disconnect("This floor has too many doors or objects for multiplayer world sync."); return; }
                if (Time.realtimeSinceStartup >= nextWorldErrorLog) { nextWorldErrorLog = Time.realtimeSinceStartup + 5; Log("World snapshot failed: " + ex.Message); }
            }
            catch (Exception ex)
            { if (Time.realtimeSinceStartup >= nextWorldErrorLog) { nextWorldErrorLog = Time.realtimeSinceStartup + 5; Log("World snapshot failed: " + ex); } }
        }

        void Update()
        {
            UpdateDebugConsole();
            UpdateMenuUi();
            UpdateLanDiscovery();
            UpdateSpriteDepthLab();
            if (!DebugConsoleOpen&&!DebugConsoleConsumedKeyFrame&&Input.GetKeyDown(KeyCode.F5)) SetPanel(!show);
            if (!DebugConsoleOpen&&!DebugConsoleConsumedKeyFrame&&show && Input.GetKeyDown(KeyCode.Escape)) NativeBack();
            if (autoRole != null && Time.realtimeSinceStartup > 3 && Time.realtimeSinceStartup > autoRoleAt && (!Lab.Enabled||TitleVisible))
            { string role = autoRole; autoRole = null; labAutoJoin = role == "join"; Connect(role == "host"); }
            UpdateSteam();
            float now = Time.realtimeSinceStartup;
            if (Lab.Enabled && now > nextLabStep) { nextLabStep = now + 3; AdvanceLabMenus(); }
            if (diagnostics && now > nextDiagnostic) { nextDiagnostic = now + 1; WriteDiagnostic(); }
            if (diagnostics && now > nextCapture)
            {
                nextCapture = now + 10;
                string directory = Path.Combine(Lab.Workspace, "private/captures"); Directory.CreateDirectory(directory);
                QueueScreenshot(Path.Combine(directory, "screen-" + Process.GetCurrentProcess().Id + ".png"));
            }
            if(!Active){UpdateMigration();if(!Active)return;}
            NetworkEvent e;
            for (int i = 0; i < 100 && Active && Network.Poll(out e); i++)
            {
                try { HandleEvent(e); }
                catch (Exception ex)
                {
                    Log("Rejected peer " + e.Peer + ": " + ex.Message);
                    if (Hosting) Network.SendAndClose(e.Peer, Wire.Packet(Op.Reject, w => Wire.Text(w, ex.Message.Substring(0, Math.Min(180, ex.Message.Length)))));
                    else Disconnect("Host sent invalid or incompatible data: " + ex.Message);
                }
            }
            if(UpdateMigration())return;
            if (!Active) return;
            UpdateLinkHealth(now);
            if (!Active) return;
            foreach (Peer peer in peers.Values.ToArray())
                if (Hosting && peer.Id != 0 && !peer.Accepted && now - peer.Joined > 8) Network.Drop(peer.Id);
            if (now > nextPing && (Hosting || self >= 0)) { nextPing = now + 2; SendHeartbeat(); }
            // accepted peers only: a client still in its handshake would be refused once the run starts
            if (autoStart && Hosting && !Running && now > autoStartAt && peers.Values.Count(p => p.Accepted) > 1)
            { autoStart = false; AutoStartRun(); }
            if (pendingScene != null) TryLoadClient();
            if (Running && !worldReady && !AwaitingMigrationFloor && now > nextRegistry) TryBuildWorld();
            var currentManager = Singleton<BaseGameManager>.Instance;
            var currentCore = Singleton<CoreGameManager>.Instance;
            if (Running && worldReady && !ResultsTransitionActive && (Hosting || readyAcknowledged) && currentManager != null && currentCore != null && currentCore.readyToStart && !currentManager.Ec.Active && (beginRequested || Hosting))
            {
                if (Object.FindObjectOfType<ElevatorScreen>() == null) currentManager.BeginPlay();
                if (Hosting && Lab.Enabled && currentManager.Ec.Active) { var happy = Object.FindObjectOfType<HappyBaldi>(); if (happy != null) happy.Activate(); }
            }
            UpdateClientElevator();
            UpdateElevatorLab();
            UpdateNpcSyncLab();
            if (worldReady && now > nextSend) { nextSend = now + 0.05f; SendPose(); }
            UpdateHudShare();
            if (Hosting && worldReady && now > nextWorld) { nextWorld = now + 0.1f; TrySendWorld(); if (!Active) return; CheckRemoteCatches();CheckTeamWipe(null); }
            if (testActions && !Hosting && worldReady && readyAcknowledged && now > nextTest) RunTestActions();
            UpdateAvatars();
            UpdateNpcReplication();
            UpdateFeatures();
            UpdateHiding(); UpdateShop();
            UpdateNpcInteractions(); UpdatePrincipalSync(); UpdateNpcFeatures();
            UpdateFocusedSync();
            UpdateSpriteTour();
            UpdateSpectator();
        }

        void HandleEvent(NetworkEvent e)
        {
            NoteLinkEvent(e);
            if(HandleMigrationConnectionEvent(e))return;
            if (e.Kind == "error")
            {
                if (Hosting) { Tell(e.Message); return; }
                Disconnect("Connection failed: " + e.Message);
                if (Lab.Enabled && labAutoJoin && labJoinRetries++ < 30)
                { autoRole = "join"; autoRoleAt = Time.realtimeSinceStartup + 2; Log("LAB: the host isn't listening yet; join retry " + labJoinRetries + " in 2 s."); }
                return;
            }
            if (e.Kind == "connected")
            {
                if (Hosting) peers[e.Peer] = new Peer { Id = e.Peer, Joined = Time.realtimeSinceStartup };
                else Network.Send(0, Wire.Packet(Op.Hello, w => { w.Write(Wire.Version); Wire.Text(w, ModVersion); Wire.Text(w, gameHash); Wire.Text(w, Name); Wire.Text(w, Code);WriteMigrationIdentity(w); }));
                return;
            }
            if (e.Kind == "disconnected")
            {
                if (!Hosting) { if(!Network.Steam&&BeginHostMigration())return;Disconnect("Disconnected from host. Return to the menu to rejoin."); return; }
                Peer leaving;
                if (peers.TryGetValue(e.Peer, out leaving)) { DestroyAvatar(leaving); peers.Remove(e.Peer);latency.Remove(e.Peer);probes.Remove(e.Peer);latencySampleAt.Remove(e.Peer); Tell((leaving.Name ?? "Peer") + " left."); if (leaving.Accepted) PlayTeamSound("leave"); BroadcastRoster();CheckTeamWipe(null); }
                return;
            }
            using (MemoryStream memory = new MemoryStream(e.Data, false))
            using (BinaryReader r = new BinaryReader(memory))
            {
                Op op = (Op)r.ReadByte();
                Peer sender = null;
                if (Hosting && (!peers.TryGetValue(e.Peer, out sender) || (!sender.Accepted && op != Op.Hello&&op!=Op.MigrationHello))) throw new IOException("Handshake required.");
                if (op == Op.Ping) { int nonce = r.ReadInt32(); Network.Send(e.Peer, Wire.Packet(Op.Pong, w => w.Write(nonce))); }
                else if (op == Op.Pong) ReadHeartbeat(e.Peer, r.ReadInt32());
                else if (Hosting) HandleClient(e.Peer, sender, op, r);
                else HandleHost(op, r);
                if (memory.Position != memory.Length) throw new IOException("Trailing packet data.");
            }
        }

        void HandleClient(int id, Peer sender, Op op, BinaryReader r)
        {
            if(HandleVendingClient(id,sender,op,r))return;
            if(HandleRemoteElevatorClient(id,sender,op,r))return;
            if(HandleMigrationClient(id,sender,op,r))return;
            if(HandleHudClient(id,sender,op,r))return;
            if (HandleNpcClient(id,sender,op,r) || HandlePrincipalClient(id,sender,op,r) || HandleNpcFeatureClient(id,sender,op,r) || HandleHidingClient(id,sender,op,r) || HandleShopClient(id,sender,op,r)) return;
            if (HandleFeatureClient(id, sender, op, r)) return;
            if (op == Op.Hello)
            {
                int protocol = r.ReadInt32(); string modVersion = Wire.Text(r, 16), hash = Wire.Text(r, 64), name = Wire.Text(r, 24), code = Wire.Text(r, 64);
                if (sender.Accepted) throw new IOException("This connection already joined.");
                if (protocol != Wire.Version || modVersion != ModVersion) throw new IOException("Mod version mismatch. All players need Baldi Together " + ModVersion + ".");
                if (hash != gameHash) throw new IOException("Game version mismatch. Use identical Baldi game files.");
                if (code != Code) throw new IOException("Incorrect room code.");
                if (Running) throw new IOException("Run already started. Join a new room before the host starts.");
                if (migrationHostWaiting) throw new IOException("Room is migrating to a new host. Try again shortly.");
                sender.Accepted = true; sender.Name = CleanName(name);sender.ModVersion=modVersion;sender.GameHash=hash;
                ReadInitialMigrationIdentity(id,sender,r);
                sender.Colour = migrationMembers[id].Colour;
                Network.Send(id, Wire.Packet(Op.Welcome, w => { w.Write(id); w.Write(Seed); }));
                BroadcastRoster(); Tell(sender.Name + " joined the room."); PlayTeamSound("join");
            }
            else if (op == Op.Ready)
            {
                int generation = r.ReadInt32(); string hash = Wire.Text(r, 64), itemHash = Wire.Text(r, 64);
                if (generation != epoch || !worldReady) { sender.Ready = false; return; }
                if (hash != worldHash) throw new IOException("Generated level mismatch. All players must use identical game files and settings.");
                if (itemHash != SupplyDigest()) throw new IOException("Shared item catalog did not match the host.");
                sender.Ready = true;
                SendRoundState();
                Network.Send(id, Wire.Packet(Op.ReadyAck, w => w.Write(epoch)));
                Tell(sender.Name + " loaded the same level.");
            }
            else if (op == Op.Pose)
            {
                int generation = r.ReadInt32(); int claimedId = r.ReadInt32();
                Vector3 pos = ReadVector(r); float yaw = Wire.Number(r); bool inLocker=r.ReadBoolean();
                if (generation != epoch || claimedId != id || !sender.Ready || sender.Eliminated) return;
                sender.Position = pos; sender.Yaw = yaw; sender.InLocker=inLocker; sender.HasPose = true; sender.PoseAt = Time.realtimeSinceStartup;
                BroadcastPose(sender);
            }
            else if (op == Op.Interact)
            {
                int generation = r.ReadInt32(); byte kind = r.ReadByte(); string key = Wire.Text(r, 256);
                if (generation != epoch || !sender.Ready || !sender.HasPose || sender.Eliminated || Time.realtimeSinceStartup - sender.LastAction < 0.08f) return;
                sender.LastAction = Time.realtimeSinceStartup;
                Applying = true;
                try
                {
                    Notebook notebook; StandardDoor door; Activity activity;
                    if (kind == 0 && notebooks.TryGetValue(key, out notebook) && notebook != null && !notebook.collected && !notebook.hidden && Vector3.Distance(sender.Position, notebook.transform.position) < 20)
                    { notebook.Clicked(0); acceptedInteractions++; Log("Accepted notebook from " + sender.Name + ": " + key); }
                    if (kind == 1 && doors.TryGetValue(key, out door) && door != null && Vector3.Distance(sender.Position, door.CenteredPosition) < 20)
                    { door.Clicked(0); acceptedInteractions++; Log("Accepted door from " + sender.Name + ": " + key); }
                    if ((kind == 2 || kind == 3) && activities.TryGetValue(key, out activity) && activity != null && !activity.IsCompleted && Vector3.Distance(sender.Position, activity.transform.position) < 35)
                    { activity.Completed(0, kind == 2); acceptedInteractions++; Log("Accepted activity from " + sender.Name + ": " + key); }
                    GameButtonBase button;
                    if (kind == 4 && buttons.TryGetValue(key, out button) && button != null && GameButtonPatch.HostOwned(button) && Vector3.Distance(sender.Position, button.transform.position) < 20)
                    { button.Clicked(0); acceptedInteractions++; acceptedButtons++; Log("Accepted button from " + sender.Name + ": " + key); }
                }
                finally { Applying = false; }
            }
            else if (op == Op.Leave) Network.Drop(id);
            else throw new IOException("Client cannot send " + op);
        }

        void HandleHost(Op op, BinaryReader r)
        {
            if(HandleResultsHost(op,r)||HandleVendingHost(op,r))return;
            if(HandleMigrationHost(op,r))return;
            if(HandleHudHost(op,r))return;
            if(HandleRoundHost(op,r))return;
            if (HandleNpcHost(op,r) || HandlePrincipalHost(op,r) || HandleNpcFeatureHost(op,r)) return;
            if (HandleFeatureHost(op, r)) return;
            if (op == Op.Welcome) { self = r.ReadInt32(); Seed = r.ReadInt32(); if (self < 1 || self > 3) throw new IOException("Invalid player ID.");MigrationWelcomed(); Tell("Joined. Waiting for the host to start a new run."); }
            else if (op == Op.Roster)
            {
                int count = r.ReadByte(); if (count < 1 || count > 4) throw new IOException("Invalid roster.");
                HashSet<int> present = new HashSet<int>();
                // Sounds only for changes after this client already knew the room, not its own first roster.
                bool knownRoom = self >= 0 && peers.ContainsKey(self); bool someoneJoined = false;
                for (int i = 0; i < count; i++)
                {
                    int id = r.ReadInt32(); string name = Wire.Text(r, 24);string version=Wire.Text(r,16),hash=Wire.Text(r,64);bool eliminated=r.ReadBoolean();byte colour=r.ReadByte();
                    if (id < 0 || id > 3 || !present.Add(id)) throw new IOException("Invalid player ID.");
                    Peer peer; if (!peers.TryGetValue(id, out peer)) { peers[id] = peer = new Peer { Id = id, Accepted = true }; someoneJoined |= knownRoom && id != self; }
                    peer.Name = CleanName(name);
                    peer.ModVersion=version;peer.GameHash=hash;peer.Eliminated=peer.Eliminated||eliminated;peer.Colour=(byte)PlayerSpriteSheet.ClampColour(colour);
                }
                int[] departed = peers.Keys.Where(id => !present.Contains(id)).ToArray();
                foreach (int id in departed) { DestroyAvatar(peers[id]); peers.Remove(id); }
                if (knownRoom && departed.Any(id => id != self)) PlayTeamSound("leave");
                else if (someoneJoined) PlayTeamSound("join");
                RestoreMigrationEliminations();
            }
            else if (op == Op.Start)
            {
                int generation = r.ReadInt32(); Seed = r.ReadInt32(); string scene = Wire.Text(r, 128);
                pendingNext = Wire.Text(r, 128); pendingLives = r.ReadInt32(); pendingAttempts = r.ReadInt32(); pendingLastLevel = r.ReadInt32(); pendingMode = r.ReadInt32(); pendingLifeMode = r.ReadInt32(); pendingTimeLimit = r.ReadBoolean();
                if (generation <= epoch) throw new IOException("Invalid level generation.");
                epoch = generation; sceneName = scene; ResetWorld(); pendingScene = scene;nextClientLoad=0;
                Tell("Loading the host's level with seed " + Seed + "..."); SetPanel(false);
            }
            else if (op == Op.ReadyAck) { int generation = r.ReadInt32(); if (generation == epoch) { readyAcknowledged = true; Tell("Level matched. Co-op connected."); } }
            else if (op == Op.Begin) { int generation = r.ReadInt32(); if (generation == epoch) beginRequested = true; }
            else if (op == Op.Pose)
            {
                int generation = r.ReadInt32(), id = r.ReadInt32(); Vector3 pos = ReadVector(r); float yaw = Wire.Number(r); bool inLocker=r.ReadBoolean();
                Peer peer; if (generation == epoch && id != self && peers.TryGetValue(id, out peer)) { peer.Position = pos; peer.Yaw = yaw; peer.InLocker=inLocker; peer.HasPose = true;peer.PoseAt=Time.realtimeSinceStartup; }
            }
            else if (op == Op.World) ReadWorld(r);
            else if (op == Op.End)
            {
                int generation = r.ReadInt32(), index = r.ReadInt32();
                if (generation == epoch && worldReady && index >= 0 && index < npcs.Length && npcs[index] is Baldi)
                {
                    Applying = true;
                    try { deathSent=true;ReleaseSpectator();var core = Singleton<CoreGameManager>.Instance; core.EndGame(core.GetPlayer(0).transform, (Baldi)npcs[index]); }
                    finally { Applying = false; }
                }
            }
            else throw new IOException("Unexpected host message " + op);
        }

        void BroadcastRoster()
        {
            SendAll(Wire.Packet(Op.Roster, w =>
            {
                Peer[] accepted = peers.Values.Where(p => p.Accepted).OrderBy(p => p.Id).ToArray();
                w.Write((byte)accepted.Length);
                foreach (Peer peer in accepted) { w.Write(peer.Id); Wire.Text(w, peer.Name);Wire.Text(w,peer.ModVersion??ModVersion);Wire.Text(w,peer.GameHash??gameHash);w.Write(peer.Eliminated); w.Write(peer.Colour); }
            }));
            PublishMigrationPlan();
        }
        void SendAll(byte[] packet)
        {
            if (!Active) return;
            if (Hosting) { foreach (Peer peer in peers.Values.ToArray()) if (peer.Id != 0 && peer.Accepted) Network.Send(peer.Id, packet); }
            else Network.Send(0, packet);
        }

        public void HostLoad(SceneObject scene)
        {
            if (!Hosting || scene == null) return;
            epoch++; sceneName = scene.name; ResetWorld();
            var core = Singleton<CoreGameManager>.Instance;
            core.readyToStart = false;
            SendAll(Wire.Packet(Op.Start, w =>
            {
                w.Write(epoch); w.Write(Seed); Wire.Text(w, sceneName); Wire.Text(w, core.nextLevel != null ? core.nextLevel.name : sceneName);
                w.Write(core.Lives); w.Write(core.Attempts); w.Write(core.lastLevelNumber); w.Write((int)core.currentMode); w.Write((int)core.lifeMode); w.Write(core.timeLimitChallenge);
            }));
            Tell("Loading " + sceneName + ", seed " + Seed + "."); SetPanel(false);
        }

        void TryLoadClient()
        {
            if(Time.realtimeSinceStartup<nextClientLoad)return;nextClientLoad=Time.realtimeSinceStartup+.25f;
            SceneObject scene = Resources.FindObjectsOfTypeAll<SceneObject>().FirstOrDefault(s => s.name == pendingScene);
            if (scene == null) { if(Time.realtimeSinceStartup-levelStartedAt<90)return;Disconnect("Host level is not available in this game copy: " + pendingScene); return; }
            CoreGameManager core = Singleton<CoreGameManager>.Instance;
            Applying = true;
            try
            {
                if (core == null)
                {
                    GameLoader template = Resources.FindObjectsOfTypeAll<GameLoader>().FirstOrDefault(l => l.cgmPre != null);
                    if (template == null) return;
                    GameObject root = new GameObject("BaldiTogether client loader");
                    GameLoader loader = root.AddComponent<GameLoader>(); loader.cgmPre = template.cgmPre; loader.useSeed = true; loader.seed = Seed;
                    loader.Initialize(2); core = Singleton<CoreGameManager>.Instance; ApplyRunState(core);
                    core.sceneObject = scene; EnsureClientElevator(); loader.LoadLevel(scene);
                    Object.Destroy(root, 2);
                }
                else
                {
                    core.StopAllCoroutines();
                    var oldManager = Singleton<BaseGameManager>.Instance;
                    if (oldManager != null) { oldManager.StopAllCoroutines(); if (oldManager.Ec != null) { oldManager.Ec.ResetEvents(); oldManager.Ec.gameObject.SetActive(false); } }
                    core.PrepareForReload(); ApplyRunState(core); core.SetSeed(Seed); core.sceneObject = scene;
                    EnsureClientElevator();
                    Singleton<AdditiveSceneManager>.Instance.LoadScene("Game");
                }
                pendingScene = null;
            }
            catch (Exception ex) { Disconnect("Could not load host level: " + ex.Message); }
            finally { Applying = false; }
        }

        void ApplyRunState(CoreGameManager core)
        {
            core.SaveEnabled = false; core.readyToStart = false;
            core.SetLives(pendingLives, true); core.SetAttempts(pendingAttempts); core.lastLevelNumber = pendingLastLevel;
            core.currentMode = (Mode)pendingMode; core.lifeMode = (LifeMode)pendingLifeMode; core.timeLimitChallenge = pendingTimeLimit; // shapes TimeOut
            core.nextLevel = Resources.FindObjectsOfTypeAll<SceneObject>().FirstOrDefault(s => s.name == pendingNext);
            if (core.nextLevel == null) throw new IOException("Host's next level is unavailable: " + pendingNext);
            Time.timeScale = 0; AudioListener.pause = false;
        }

        void AutoStartRun()
        {
            GameLoader loader = Resources.FindObjectsOfTypeAll<GameLoader>().FirstOrDefault(l => l.cgmPre != null && l.list != null && l.list.scenes.Length > 0);
            if (loader == null) { autoStart = true; autoStartAt = Time.realtimeSinceStartup + 2; return; }
            SceneObject scene = loader.list.scenes.FirstOrDefault(s => s != null && s.levelNo == 0);
            if (scene == null) scene = loader.list.scenes[0];
            loader.useSeed = true; loader.seed = Seed; loader.Initialize(2);
            Singleton<CoreGameManager>.Instance.SaveEnabled = false;
            if(Lab.Enabled && Lab.HasArg("-bt-elevator-tour"))
            {
                var field=HarmonyLib.AccessTools.Field(typeof(BaseGameManager),"elevatorScreenPre");
                var prefab=Resources.FindObjectsOfTypeAll<BaseGameManager>().Select(m=>field.GetValue(m) as ElevatorScreen).First(s=>s!=null);
                var screen=Object.Instantiate(prefab);loader.AssignElevatorScreen(screen);screen.Initialize();
            }
            loader.LoadLevel(scene);
        }

        void AdvanceLabMenus()
        {
            WarningScreen warning = Object.FindObjectOfType<WarningScreen>();
            var global = Singleton<GlobalCam>.Instance;
            bool cameraReady = global != null && global.Cam != null && global.Cam.targetTexture != null;
            // Some two-process launches sat on the warning screen for minutes (black capture): log what is missing,
            // and after 20 s advance anyway so the run is not lost to a startup stall.
            if (warning != null && !warningAdvanced && (cameraReady || Time.realtimeSinceStartup > 20))
            {
                warningAdvanced = true; HarmonyLib.AccessTools.Method(typeof(WarningScreen), "Advance").Invoke(warning, null);
                Log("LAB: advanced warning screen through game method" + (cameraReady ? "." : " (fallback at " + Time.realtimeSinceStartup.ToString("0") + " s; camera " + (global == null ? "no GlobalCam" : global.Cam == null ? "no Cam" : "no target texture") + ")."));
            }
            else if (!warningAdvanced && Time.realtimeSinceStartup > 10 && labWarningLogs++ < 6)
                Log("LAB: waiting at startup: warning screen " + (warning != null ? "found" : "missing") + ", GlobalCam " + (global == null ? "missing" : global.Cam == null ? "no Cam" : global.Cam.targetTexture == null ? "no target texture" : "ready")
                    + ", scene " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + ", title " + TitleVisible + ", frame " + Time.frameCount);
            NameManager names = Object.FindObjectOfType<NameManager>();
            if (names != null && !nameSubmitted)
            {
                nameSubmitted = true;
                int count = (int)HarmonyLib.AccessTools.Field(typeof(NameManager), "nameCount").GetValue(names);
                if (count > 0) names.LoadName(0);
                else { names.AddLetterToName("L"); names.AddLetterToName("A"); names.AddLetterToName("B"); names.AddName(); }
                Log("LAB: selected isolated LAB profile.");
            }
        }

        void RunTestActions()
        {
            nextTest = Time.realtimeSinceStartup + 2;
            PlayerManager player = Singleton<CoreGameManager>.Instance.GetPlayer(0);
            if (testStep == 0)
            {
                ProbeClientEventTimers(Singleton<BaseGameManager>.Instance == null ? null : Singleton<BaseGameManager>.Instance.Ec);
                var entry = notebooks.FirstOrDefault(pair => pair.Value != null && !pair.Value.collected && !pair.Value.hidden);
                if (entry.Value == null) return;
                testNotebook = entry.Key; player.Teleport(entry.Value.transform.position + new Vector3(3,0,0));
                Log("LAB: moved client near notebook " + testNotebook); testStep++;
            }
            else if (testStep == 1)
            {
                notebooks[testNotebook].Clicked(0); Log("LAB: client requested notebook through Clicked."); testStep++;
            }
            else if (testStep == 2)
            {
                if (!notebooks[testNotebook].collected) { Log("LAB: waiting for notebook acknowledgement."); return; }
                var entry = doors.FirstOrDefault(pair => pair.Value != null && !pair.Value.locked);
                if (entry.Value == null) return;
                testDoor = entry.Key; player.Teleport(entry.Value.CenteredPosition + new Vector3(3,0,0));
                Log("LAB: moved client near door " + testDoor); testStep++;
            }
            else if (testStep == 3)
            {
                doors[testDoor].Clicked(0); Log("LAB: client requested door through Clicked."); testStep++;
            }
            else if (testStep == 4)
            {
                var entry = activities.FirstOrDefault(pair => pair.Value != null && !pair.Value.IsCompleted);
                if (entry.Value == null) { testStep = 6; return; }
                player.Teleport(entry.Value.transform.position + new Vector3(3,0,0)); testNotebook = entry.Key; testStep++;
            }
            else if (testStep == 5)
            { activities[testNotebook].Completed(0, true); Log("LAB: client submitted an activity result."); testStep++; }
            else if (testStep == 6)
            {
                var entry = supplies.FirstOrDefault(pair => !pair.Value.Consumed && pair.Value.Pickup != null && pair.Value.Pickup.gameObject.activeInHierarchy);
                if (entry.Value == null) { Log("LAB: no shared item available in this level."); return; }
                testSupply = entry.Key; inventoryBeforeGrant = player.itm.TotalItemsInInventory;
                player.Teleport(entry.Value.Pickup.transform.position + new Vector3(3,0,0)); testStep++;
            }
            else if (testStep == 7)
            { supplies[testSupply].Pickup.Collect(0); PingTeam(); Log("LAB: requested shared item and team ping."); testStep++; }
            else if (testStep == 8)
            {
                if (pickupGrants == 0) return;
                supplies[testSupply].Pickup.Collect(0);
                duplicateItemBlocked = player.itm.TotalItemsInInventory == inventoryBeforeGrant + 1; testStep++;
            }
            else if (testStep == 9)
            {
                ItemObject[] previous = player.itm.items.ToArray();
                for (int i = 0; i < 9 && !player.itm.InventoryFull(); i++) player.itm.AddItem(supplies[testSupply].Item);
                var other = supplies.FirstOrDefault(pair => pair.Key != testSupply && !pair.Value.Consumed);
                if (other.Value != null) { other.Value.Pickup.Collect(0); fullInventoryBlocked = !pickupPending; }
                for (int i = 0; i < previous.Length; i++) player.itm.SetItem(previous[i], i);
                testStep++;
            }
            else if (testStep == 10)
            {
                var core = Singleton<CoreGameManager>.Instance;
                player.Teleport(core.sceneObject == null ? player.transform.position : Singleton<BaseGameManager>.Instance.Ec.spawnPoint + Singleton<BaseGameManager>.Instance.Ec.spawnRotation * new Vector3(3,0,10));
                Log("LAB: moved client in front of the host for visual verification."); testStep++;
            }
        }

        static string Key(Component component)
        {
            Vector3 p = component.transform.position;
            StandardDoor door = component as StandardDoor;
            if (door != null) p = door.CenteredPosition;
            return component.GetType().Name + ":" + Math.Round(p.x, 2).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(p.y, 2).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(p.z, 2).ToString(CultureInfo.InvariantCulture) + ":" + (door == null ? "" : ((int)door.direction).ToString());
        }

        void TryBuildWorld()
        {
            nextRegistry = Time.realtimeSinceStartup + 1;
            BaseGameManager manager = Singleton<BaseGameManager>.Instance;
            CoreGameManager core = Singleton<CoreGameManager>.Instance;
            if (manager == null || core == null || !core.readyToStart || manager.Ec == null || core.GetPlayer(0) == null) return;
            try
            {
                notebooks.Clear(); doors.Clear(); activities.Clear();
                foreach (Notebook notebook in Object.FindObjectsOfType<Notebook>(true)) { string key = Key(notebook); if (notebooks.ContainsKey(key)) throw new IOException("Duplicate notebook ID."); notebooks.Add(key, notebook); }
                foreach (StandardDoor door in Object.FindObjectsOfType<StandardDoor>()) { string key = Key(door); if (doors.ContainsKey(key)) throw new IOException("Duplicate door ID."); doors.Add(key, door); }
                foreach (Activity activity in Object.FindObjectsOfType<Activity>(true).Where(a => a.room != null && a.room.ec == manager.Ec)) { string key = Key(activity); if (activities.ContainsKey(key)) throw new IOException("Duplicate activity ID."); activities.Add(key, activity); }
                BuildShopWorld(); BuildVendingWorld(); BuildFeatureWorld(); BuildExtraDoors(manager.Ec); LabForceCompatibilityFailure();
                if (notebooks.Count > 256 || doors.Count > 512 || activities.Count > 256) throw new IOException("Level exceeds supported world limits.");
                npcs = manager.Ec.Npcs.ToArray();
                string layout = sceneName + ":" + Seed + "\n" + string.Join("\n", notebooks.Keys.Concat(doors.Keys).Concat(activities.Keys).Concat(extraDoors.Keys).OrderBy(k => k, StringComparer.Ordinal).ToArray());
                using (SHA256 sha = SHA256.Create()) worldHash = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(layout))).Replace("-", "");
                worldReady = true;
                core.SaveEnabled = false;
                Log("World ready epoch=" + epoch + " notebooks=" + notebooks.Count + " doors=" + doors.Count + " hash=" + worldHash);
                if (!Hosting) SendReady();
            }
            catch (Exception ex) { Disconnect("Level compatibility check failed: " + ex.Message); }
        }
        bool labCompatFailed;
        void LabForceCompatibilityFailure()
        {
            if (Lab.Enabled && Lab.HasArg("-bt-compat-fail-tour") && !labCompatFailed) { labCompatFailed = true; throw new IOException("lab-forced level check failure"); }
        }

        void SendReady()
        {
            if (testReadyDelay > 0 && Time.realtimeSinceStartup - worldBuiltAt < testReadyDelay) return;
            if (!catalogApplied)
            { Network.Send(0, Wire.Packet(Op.CatalogRequest, w => { w.Write(epoch); Wire.Text(w, worldHash); })); return; }
            Network.Send(0, Wire.Packet(Op.Ready, w => { w.Write(epoch); Wire.Text(w, worldHash); Wire.Text(w, SupplyDigest()); }));
        }

        public void BeginPlay()
        { Log("Level began generation "+epoch+" nativeElevator="+(clientElevatorStarted!=null)); if (Hosting) SendAll(Wire.Packet(Op.Begin, w => w.Write(epoch))); }

        void SendPose()
        {
            CoreGameManager core = Singleton<CoreGameManager>.Instance;
            if (core == null || core.GetPlayer(0) == null) return;
            if (!Hosting && !readyAcknowledged) { if (Time.realtimeSinceStartup > readyRetry) { readyRetry = Time.realtimeSinceStartup + 1; SendReady(); } return; }
            Peer local;
            if (!peers.TryGetValue(self, out local)) return;
            var player = core.GetPlayer(0);
            local.Position = player.transform.position;
            local.Yaw = player.cameraBase != null ? player.cameraBase.eulerAngles.y : player.transform.eulerAngles.y;
            local.HasPose = true; local.PoseAt = Time.realtimeSinceStartup;
            local.Tagged = player.Tagged; local.Invisible = player.Invisible;
            local.InLocker=localLocker!=null&&localLocker.playerInside;
            if (spriteTourPose) local.Yaw = spriteTourYaw;
            byte[] packet = PosePacket(local);
            if (Hosting) BroadcastPose(local); else Network.Send(0, packet);
        }
        byte[] PosePacket(Peer peer) { return Wire.Packet(Op.Pose, w => { w.Write(epoch); w.Write(peer.Id); WriteVector(w, peer.Position); w.Write(peer.Yaw); w.Write(peer.InLocker); }); }
        void BroadcastPose(Peer peer)
        { byte[] packet = PosePacket(peer); foreach (Peer target in peers.Values) if (target.Id != 0 && target.Id != peer.Id && target.Ready) Network.Send(target.Id, packet); }

        public bool Request(Component target, byte kind)
        {
            if(Active&&LocalEliminated&&!Applying)return false;
            if (!Active || Applying || Hosting) return true;
            if (worldReady && readyAcknowledged)
                Network.Send(0, Wire.Packet(Op.Interact, w => { w.Write(epoch); w.Write(kind); Wire.Text(w, Key(target)); }));
            return false;
        }

        void SendWorld()
        {
            BaseGameManager manager = Singleton<BaseGameManager>.Instance;
            if (manager == null) return;
            npcs = manager.Ec.Npcs.ToArray();
            byte[] packet = Wire.Packet(Op.World, w =>
            {
                w.Write(epoch);
                Activity[] completed = activities.Values.Where(a => a != null && a.IsCompleted).ToArray();
                w.Write((ushort)completed.Length);
                foreach (Activity a in completed) { Wire.Text(w, Key(a)); w.Write((bool)HarmonyLib.AccessTools.Field(typeof(Activity), "correct").GetValue(a)); }
                Notebook[] collected = notebooks.Values.Where(n => n != null && n.collected).ToArray();
                w.Write((ushort)collected.Length); foreach (Notebook n in collected) Wire.Text(w, Key(n));
                w.Write((ushort)doors.Count);
                foreach (var entry in doors) { Wire.Text(w, entry.Key); w.Write(entry.Value.open); w.Write(entry.Value.locked); }
                w.Write((ushort)npcs.Length);
                foreach (NPC npc in npcs) { w.Write((int)npc.Character); Wire.Text(w,NpcPrefabName(npc)); WriteVector(w, npc.transform.position); w.Write(npc.transform.eulerAngles.y); }
                WriteSupplies(w);
                WriteShopWorld(w);
                WriteVendingWorld(w);
                WriteExtraDoors(w);
                WriteNpcPresentation(w); WriteNpcPushes(w); WritePrincipalWorld(w); WriteNpcFeatureWorld(w);
                WriteElevatorWorld(w);
            });
            foreach (Peer peer in peers.Values) if (peer.Id != 0 && peer.Ready) Network.Send(peer.Id, packet);
        }

        void ReadWorld(BinaryReader r)
        {
            int generation = r.ReadInt32(); bool apply = generation == epoch && worldReady && readyAcknowledged;
            Applying = true;
            try
            {
                int count = r.ReadUInt16(); if (count > 256) throw new IOException("Activity limit exceeded.");
                for (int i = 0; i < count; i++)
                { string key = Wire.Text(r, 256); bool correct = r.ReadBoolean(); Activity a; if (apply && activities.TryGetValue(key, out a) && a != null && !a.IsCompleted) a.Completed(0, correct); }
                count = r.ReadUInt16(); if (count > 256) throw new IOException("Notebook limit exceeded.");
                for (int i = 0; i < count; i++)
                { string key = Wire.Text(r, 256); Notebook n; if (apply && notebooks.TryGetValue(key, out n) && n != null && !n.collected) n.Clicked(0); }
                count = r.ReadUInt16(); if (count > 1024) throw new IOException("Door limit exceeded.");
                for (int i = 0; i < count; i++)
                {
                    string key = Wire.Text(r, 256); bool open = r.ReadBoolean(), locked = r.ReadBoolean(); StandardDoor door;
                    if (apply && doors.TryGetValue(key, out door) && door != null)
                    {
                        if (locked != door.locked) { if (locked) door.Lock(true); else door.Unlock(); }
                        if (open) { if (!door.open) observedDoorOpens++; door.OpenTimed(0.3f, false); } else if (door.open) door.Shut();
                    }
                }
                count = r.ReadUInt16(); if (count > 128) throw new IOException("NPC limit exceeded.");
                BaseGameManager manager = Singleton<BaseGameManager>.Instance;
                if (apply && manager != null)
                {
                    if (count > 0 && manager.Ec.Npcs.Count == 0)
                    {
                        foreach (HappyBaldi happy in Object.FindObjectsOfType<HappyBaldi>()) happy.gameObject.SetActive(false);
                        if (countdownReleased.Add(manager.Ec)) manager.BeginSpoopMode();
                    }
                    npcs = manager.Ec.Npcs.ToArray();
                }
                for (int i = 0; i < count; i++)
                {
                    int character = r.ReadInt32(); string prefab = Wire.Text(r,128); Vector3 pos = ReadVector(r); float yaw = Wire.Number(r);
                    if (apply && manager != null) EnsureReplicaNpc(manager.Ec,i,character,prefab,pos);
                    if (apply && i < npcs.Length && (int)npcs[i].Character == character)
                    {
                        observedNpcFrames++;
                        NPC npc = npcs[i];
                        if (npc.Navigator != null && npc.Navigator.enabled) { npc.Navigator.enabled = false; disabledNavigators.Add(npc.Navigator); }
                        ReceiveNpcPose(npc, pos, yaw);
                    }
                }
                if (apply && manager != null) TrimReplicaNpcs(manager.Ec,count);
                ReadSupplies(r, apply);
                ReadShopWorld(r, apply);
                ReadVendingWorld(r,apply);
                ReadExtraDoors(r, apply);
                ReadNpcPresentation(r,apply); ReadNpcPushes(r,apply); ReadPrincipalWorld(r,apply); ReadNpcFeatureWorld(r,apply);
                ReadElevatorWorld(r,apply);
            }
            finally { Applying = false; }
        }

        void CheckRemoteCatches()
        {
            if (NpcTestRunning) return;
            if (deathSent) return;
            var manager = Singleton<BaseGameManager>.Instance;
            if (manager != null && manager.Ec.Npcs.Count == 0 && peers.Values.Any(p => p.Id != 0 && p.HasPose && Vector3.Distance(p.Position, manager.Ec.spawnPoint) > 20))
            {
                var happy = Object.FindObjectOfType<HappyBaldi>();
                if (happy != null && !(bool)HarmonyLib.AccessTools.Field(typeof(HappyBaldi), "activated").GetValue(happy)) happy.Activate();
            }
            for (int i = 0; i < npcs.Length; i++)
            {
                Baldi baldi = npcs[i] as Baldi; if (baldi == null || !(baldi.behaviorStateMachine.CurrentState is Baldi_Chase)) continue;
                foreach (Peer peer in peers.Values)
                {
                    if (peer.Id == 0 || !peer.Accepted || !peer.Ready || !peer.HasPose || peer.Eliminated || peer.InLocker || Time.realtimeSinceStartup-peer.PoseAt > 2) continue;
                    Vector3 delta = peer.Position - baldi.transform.position; delta.y = 0;
                    if (delta.sqrMagnitude < 9 && NativeRemoteContactVisible(baldi,peer.Position))
                    {
                        EliminatePeer(peer.Id,baldi);return;
                    }
                }
            }
        }

        public void SharedDeath(Baldi baldi)
        {
            if (!Hosting || deathSent) return;
            deathSent = true;
            int index = Array.IndexOf(npcs, baldi);
            if (index >= 0) SendAll(Wire.Packet(Op.End, w => { w.Write(epoch); w.Write(index); }));
        }

        void UpdateAvatars()
        {
            foreach (Peer peer in peers.Values)
            {
                if (peer.Id == self || !peer.HasPose || !worldReady) continue;
                if(peer.InLocker||peer.Eliminated){if(peer.Avatar!=null)peer.Avatar.SetActive(false);continue;}
                if (peer.Avatar == null)
                {
                    if (!PlayerSpriteSheet.EnsureMaterial()) continue;
                    peer.Avatar = new GameObject("Co-op player: " + peer.Name);
                    peer.Avatar.name = "Co-op player: " + peer.Name;
                    peer.Visual = peer.Avatar.AddComponent<RemotePlayerSprite>();
                    Object.DontDestroyOnLoad(peer.Avatar); peer.Avatar.transform.position = peer.Position;
                }
                peer.Avatar.SetActive(true);
                peer.Visual.SetColour(peer.Colour);
                peer.Visual.SetPose(peer.Position, peer.Yaw);
                peer.Avatar.transform.position = Vector3.Distance(peer.Avatar.transform.position, peer.Position) > 20 ? peer.Position : Vector3.Lerp(peer.Avatar.transform.position, peer.Position, 1 - Mathf.Exp(-15 * Time.unscaledDeltaTime));
            }
        }
        static void DestroyAvatar(Peer peer) { if (peer.Avatar != null) Object.Destroy(peer.Avatar); peer.Avatar = null; peer.Visual = null; }
        static Vector3 ReadVector(BinaryReader r) { return new Vector3(Wire.Number(r), Wire.Number(r), Wire.Number(r)); }
        static void WriteVector(BinaryWriter w, Vector3 p) { w.Write(p.x); w.Write(p.y); w.Write(p.z); }

        void WriteDiagnostic()
        {
            try
            {
                string path = Path.Combine(Lab.Workspace, "private/runtime-" + Process.GetCurrentProcess().Id + ".json");
                File.WriteAllText(path, BuildDiagnostic());
            }
            catch (Exception ex) { Log("Diagnostic write: " + ex.Message); }
        }

        void SetPanel(bool visible, bool dissolveBack = false)
        {
            if (show == visible) return;
            show = visible;
            if (visible) { previousLock = Cursor.lockState; previousVisible = Cursor.visible; Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            else { Cursor.lockState = previousLock; Cursor.visible = previousVisible; }
            SetMenuInput(visible||DebugConsoleOpen);
            NativeMenuVisibility(visible,dissolveBack);
        }

        public bool PanelOpen { get { return show||DebugConsoleOpen||DebugConsoleConsumedKeyFrame; } }
        void OnApplicationQuit() { if (Network != null) Network.Dispose(); }
        void OnDestroy()
        {
            DisposeDebugConsole();
            ShutdownLanDiscovery();
            if (Network != null) Network.Dispose();
            foreach (Peer peer in peers.Values) DestroyAvatar(peer);
            PlayerSpriteSheet.Dispose();
            DisposeMultiplayerAudio(); DisposeTeamSounds();
            ReleaseSpectator();
            DisposeConnectionIndicators();
            SetMenuInput(false);
            if (multiplayerButton != null) Object.Destroy(multiplayerButton.gameObject);
            if(multiplayerArtworkSprite!=null)Object.Destroy(multiplayerArtworkSprite);
            if(multiplayerArtworkTexture!=null)Object.Destroy(multiplayerArtworkTexture);
            foreach (Texture2D texture in new[] { paper, ink, green }) if (texture != null) Object.Destroy(texture);
            if (Instance == this) Instance = null;
        }

        Texture2D Solid(Color color) { var texture = new Texture2D(1, 1); texture.SetPixel(0, 0, color); texture.Apply(); return texture; }
        void Styles()
        {
            if (textStyle != null) return;
            paper = Solid(new Color(1, 0.99f, 0.91f)); ink = Solid(new Color(0.08f, 0.12f, 0.08f)); green = Solid(new Color(0.1f, 0.42f, 0.12f));
            textStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true, richText = false };
            textStyle.normal.textColor = new Color(0.05f, 0.08f, 0.05f);
            titleStyle = new GUIStyle(textStyle) { fontSize = 24, fontStyle = FontStyle.Bold };
            fieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 17, padding = new RectOffset(8,8,7,7) };
            fieldStyle.normal.background = ink; fieldStyle.normal.textColor = Color.white; fieldStyle.focused.background = green; fieldStyle.focused.textColor = Color.white;
            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 17, padding = new RectOffset(8,8,9,9) };
            buttonStyle.normal.background = green; buttonStyle.normal.textColor = Color.white; buttonStyle.focused.background = ink; buttonStyle.focused.textColor = Color.yellow;
            buttonStyle.hover.background = ink; buttonStyle.hover.textColor = Color.white;
        }

        void DrawNames()
        {
            Camera camera = PlayerCamera;
            if (!worldReady || camera == null || Event.current.type != EventType.Repaint) return;
            if (playerNameStyle == null)
            {
                playerNameStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter, wordWrap = false, richText = false };
                playerNameStyle.normal.textColor = new Color(1,1,0.94f);
                playerNameShadow = new GUIStyle(playerNameStyle); playerNameShadow.normal.textColor = Color.black;
            }
            playerNameStyle.fontSize = playerNameShadow.fontSize = Mathf.RoundToInt(13 * Mathf.Clamp(Screen.height / 900f,0.85f,1.2f));
            foreach (Peer peer in peers.Values)
            {
                if (peer.Id == self || (LocalEliminated&&peer.Id==spectatorTarget) || peer.Avatar == null || !peer.Avatar.activeSelf) continue;
                Vector3 head = peer.Avatar.transform.position + Vector3.up * 3.4f;
                Vector3 delta = head-camera.transform.position;
                float distance = delta.magnitude, facing = Vector3.Dot(camera.transform.forward,delta.normalized);
                if (distance > 35 || facing < 0.94f || !ClearWorldLine(camera.transform.position,head)) continue;
                Vector3 point = camera.WorldToScreenPoint(head);
                if (point.z <= 0) continue;
                float width = Mathf.Min(180,playerNameStyle.CalcSize(new GUIContent(peer.Name)).x+8);
                Rect area = new Rect(point.x-width/2,Screen.height-point.y-9,width,20);
                Color before = GUI.color;
                GUI.color = new Color(before.r,before.g,before.b,Mathf.Clamp01((35-distance)/12) * Mathf.Clamp01((facing-.94f)/.035f));
                GUI.Label(new Rect(area.x+1,area.y+1,area.width,area.height),peer.Name,playerNameShadow);
                GUI.Label(area,peer.Name,playerNameStyle);float nameOpacity=GUI.color.a; GUI.color = before;
                DrawConnectionIndicator(peer,new Rect(area.xMax+3,area.y+3,18,14),nameOpacity);
            }
        }
    }
}

