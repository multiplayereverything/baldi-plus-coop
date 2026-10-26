using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        sealed class Supply { public Pickup Pickup; public ItemObject Item; public bool Consumed; }
        sealed class Grant { public int Owner; public float Deadline; }
        sealed class Marker { public GameObject Object; public Material Material; public Vector3 Position; public float Expires; }
        readonly Dictionary<string, Supply> supplies = new Dictionary<string, Supply>();
        readonly Dictionary<int, Grant> grants = new Dictionary<int, Grant>();
        readonly HashSet<int> receivedGrants = new HashSet<int>();
        readonly Dictionary<int, Marker> markers = new Dictionary<int, Marker>();
        readonly Dictionary<int, float> pingCooldowns = new Dictionary<int, float>();
        readonly Dictionary<int, int> probes = new Dictionary<int, int>();
        readonly Dictionary<int, int> latency = new Dictionary<int, int>();
        SupplyClaims claims = new SupplyClaims();
        bool pickupPending, waitingForPlayers;
        int localClaim, pickupGrants, teamPings;
        float pickupDeadline, readyRetry, levelStartedAt;
        string toast;
        float toastUntil;
        float testReadyDelay, worldBuiltAt;
        string testSupply;
        int inventoryBeforeGrant;
        bool duplicateItemBlocked, fullInventoryBlocked;
        bool catalogApplied;
        bool catalogPerturbed, testTransition;

        void ResetFeatures()
        {
            foreach (Marker marker in markers.Values) DestroyMarker(marker);
            markers.Clear(); pingCooldowns.Clear(); supplies.Clear(); grants.Clear(); receivedGrants.Clear();
            claims = new SupplyClaims(); pickupPending = waitingForPlayers = false;
            catalogApplied = false;
            levelStartedAt = Time.realtimeSinceStartup; readyRetry = 0;
        }

        void Notify(string message)
        { toast = message; toastUntil = Time.realtimeSinceStartup + 5; Tell(message); }

        void SendNotice(int peer, string message)
        { Network.Send(peer, Wire.Packet(Op.Notice, w => Wire.Text(w, message))); }

        void SendHeartbeat()
        {
            int nonce = Environment.TickCount;
            if (Hosting)
            {
                foreach (Peer peer in peers.Values.Where(p => p.Id != 0 && p.Accepted))
                { probes[peer.Id] = nonce; Network.Send(peer.Id, Wire.Packet(Op.Ping, w => w.Write(nonce))); }
            }
            else { probes[0] = nonce; Network.Send(0, Wire.Packet(Op.Ping, w => w.Write(nonce))); }
        }

        void ReadHeartbeat(int peer, int nonce)
        {
            int expected;
            if (probes.TryGetValue(peer, out expected) && expected == nonce)
            {
                int elapsed = unchecked(Environment.TickCount - nonce);
                if (elapsed >= 0 && elapsed < 15000){latency[peer] = elapsed;latencySampleAt[peer]=Time.realtimeSinceStartup;}
                probes.Remove(peer);
            }
        }

        public bool CanBeginPlay()
        {
            if (!Running) return true;
            waitingForPlayers = !worldReady || (!Hosting && (!readyAcknowledged || !beginRequested));
            return !waitingForPlayers;
        }

        void UpdateFeatures()
        {
            float now = Time.realtimeSinceStartup;
            if (Hosting && waitingForPlayers && worldReady)
            {
                waitingForPlayers = false;
                var manager = Singleton<BaseGameManager>.Instance;
                if (manager != null && !manager.Ec.Active && Object.FindObjectOfType<ElevatorScreen>() == null) manager.BeginPlay();
            }
            if (Running && now - levelStartedAt > 90 && (!worldReady || (!Hosting && !readyAcknowledged)))
            { Disconnect("Level loading timed out. Check that everyone uses matching game files and mods."); return; }
            foreach (int id in markers.Where(p => now >= p.Value.Expires).Select(p => p.Key).ToArray())
            { DestroyMarker(markers[id]); markers.Remove(id); }
            if (Hosting)
            {
                foreach (var pair in grants.Where(p => !peers.ContainsKey(p.Value.Owner)).ToArray())
                {
                    // An unacknowledged delivery stays consumed when its recipient disappears.
                    string key; claims.Resolve(pair.Key, pair.Value.Owner, true, out key); grants.Remove(pair.Key);
                }
            }
            if (pickupPending && now > pickupDeadline) { pickupPending = false; Notify("Item request timed out. Check the connection before trying again."); }
            if (!show && worldReady && Input.GetKeyDown(KeyCode.G)) PingTeam();
            if (Hosting && testTransition && epoch == 1 && worldReady && now - worldBuiltAt > (SpriteTour ? 60 : 45))
            {
                testTransition = false;
                var manager = Singleton<BaseGameManager>.Instance; var core = Singleton<CoreGameManager>.Instance;
                core.StopAllCoroutines(); manager.StopAllCoroutines(); manager.Ec.ResetEvents(); manager.Ec.gameObject.SetActive(false); core.PrepareForReload();
                Log("LAB: invoking the game's scene transition path for a repeatable transition check.");
                AccessTools.Method(typeof(BaseGameManager), "LoadSceneObject", new[] { typeof(SceneObject), typeof(bool) }).Invoke(manager, new object[] { core.sceneObject, true });
            }
        }

        void BuildFeatureWorld()
        {
            worldBuiltAt = Time.realtimeSinceStartup;
            supplies.Clear();
            foreach (Pickup pickup in Object.FindObjectsOfType<Pickup>(true))
            {
                if (!pickup.gameObject.activeInHierarchy || IsShopPickup(pickup) || !pickup.free || pickup.repeatable || pickup.item == null || !pickup.item.addToInventory || pickup.item.itemType == Items.None
                    || (bool)AccessTools.Field(typeof(Pickup), "survivePickup").GetValue(pickup)) continue;
                string key = Key(pickup);
                if (supplies.ContainsKey(key)) throw new IOException("Duplicate shared item ID.");
                supplies.Add(key, new Supply { Pickup = pickup, Item = pickup.item });
            }
            if (supplies.Count > 256) throw new IOException("Shared item limit exceeded.");
            if (Lab.Enabled && testActions && !Hosting && supplies.Count > 0)
            {
                var removed = supplies.First(); removed.Value.Pickup.Hide(true); supplies.Remove(removed.Key); catalogPerturbed = true;
                Log("LAB: removed one local item before host-catalog reconciliation.");
            }
        }

        string SupplyDigest()
        {
            string value = string.Join("\n", supplies.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value.Item.name).ToArray());
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
        }

        void SendSupplyCatalog(int peer)
        {
            Network.Send(peer, Wire.Packet(Op.SupplyCatalog, w =>
            {
                w.Write(epoch); w.Write((ushort)supplies.Count);
                foreach (var pair in supplies) { Wire.Text(w, pair.Key); Wire.Text(w, pair.Value.Item.name); WriteVector(w, pair.Value.Pickup.transform.position); }
            }));
        }

        void ReadSupplyCatalog(BinaryReader r)
        {
            int generation = r.ReadInt32(), count = r.ReadUInt16();
            if (count > 256) throw new IOException("Shared item catalog limit exceeded.");
            var catalog = new List<Tuple<string, string, Vector3>>();
            var keys = new HashSet<string>();
            for (int i = 0; i < count; i++)
            {
                string key = Wire.Text(r, 256), item = Wire.Text(r, 128); Vector3 position = ReadVector(r);
                if (!keys.Add(key)) throw new IOException("Duplicate shared item catalog ID.");
                catalog.Add(Tuple.Create(key, item, position));
            }
            if (generation != epoch || !worldReady || catalogApplied) return;
            var manager = Singleton<BaseGameManager>.Instance;
            var existing = Object.FindObjectsOfType<Pickup>(true).Where(p => !IsShopPickup(p) && p.free && !p.repeatable).GroupBy(p => Key(p)).ToDictionary(g => g.Key, g => g.First());
            var items = Resources.FindObjectsOfTypeAll<ItemObject>().GroupBy(item => item.name).ToDictionary(g => g.Key, g => g.First());
            foreach (Supply supply in supplies.Values) { supply.Pickup.Hide(true); supply.Pickup.AssignItem(Singleton<CoreGameManager>.Instance.NoneItem); }
            supplies.Clear();
            foreach (var entry in catalog)
            {
                ItemObject item; if (!items.TryGetValue(entry.Item2, out item) || !item.addToInventory) throw new IOException("Host item asset is unavailable: " + entry.Item2);
                Pickup pickup;
                if (!existing.TryGetValue(entry.Item1, out pickup))
                {
                    RoomController room = manager.Ec.CellFromPosition(entry.Item3).room;
                    if (room == null) throw new IOException("Host item placement is outside a room.");
                    pickup = manager.Ec.CreateItem(room, item, new Vector2(entry.Item3.x, entry.Item3.z));
                    pickup.icon = manager.Ec.map.AddIcon(pickup.iconPre, pickup.transform, Color.white); room.pickups.Add(pickup);
                }
                pickup.transform.position = entry.Item3;
                if (Key(pickup) != entry.Item1) throw new IOException("Host item coordinates did not match its ID.");
                pickup.free = true; pickup.AssignItem(item); pickup.Hide(false);
                supplies.Add(entry.Item1, new Supply { Pickup = pickup, Item = item });
            }
            catalogApplied = true; Log("Applied host item catalog: " + supplies.Count + " supplies, hash=" + SupplyDigest());
            SendReady();
        }

        public bool BeforePickup(Pickup pickup)
        {
            // A Collect that threw skipped AfterPickup; release its claim so the host can pick up again.
            if (localClaim != 0) { string stale; claims.Resolve(localClaim, 0, false, out stale); }
            localClaim = 0;
            if(Active&&LocalEliminated&&!Applying)return false;
            if (!Active || Applying || !worldReady || !supplies.ContainsKey(Key(pickup))) return true;
            Supply supply = supplies[Key(pickup)];
            if (supply.Consumed || (Hosting && claims.IsClaimed(Key(pickup)))) return false;
            if (!Hosting && !readyAcknowledged) return false;
            var inventory = Singleton<CoreGameManager>.Instance.GetPlayer(0).itm;
            if (inventory.maxItem < 0 || inventory.InventoryFull())
            { Notify("Make an empty inventory slot before taking a shared item."); return false; }
            if (Hosting)
            { int token; if (!claims.TryClaim(Key(pickup), 0, out token)) return false; localClaim = token; return true; }
            if (!pickupPending)
            {
                pickupPending = true; pickupDeadline = Time.realtimeSinceStartup + 8;
                Network.Send(0, Wire.Packet(Op.PickupRequest, w => { w.Write(epoch); Wire.Text(w, Key(pickup)); }));
            }
            return false;
        }

        public void AfterPickup(Pickup pickup)
        {
            if (!Hosting || Applying || localClaim == 0) return;
            string key; bool success = pickup.item.itemType == Items.None;
            claims.Resolve(localClaim, 0, success, out key); localClaim = 0;
            if (key != null && supplies.ContainsKey(key)) supplies[key].Consumed = success;
        }

        void GrantPickup(int id, Peer sender, string key)
        {
            Supply supply;
            if (!sender.Ready || !sender.HasPose || sender.Eliminated || !supplies.TryGetValue(key, out supply) || supply.Pickup == null
                || !supply.Pickup.gameObject.activeInHierarchy || Vector3.Distance(sender.Position, supply.Pickup.transform.position) > 20)
            { SendNotice(id, "That item is unavailable or too far away."); return; }
            int token;
            if (!claims.TryClaim(key, id, out token)) { SendNotice(id, "That shared item was already taken, or another item request is pending."); return; }
            supply.Consumed = true; supply.Pickup.AssignItem(Singleton<CoreGameManager>.Instance.NoneItem); supply.Pickup.Hide(true);
            grants[token] = new Grant { Owner = id, Deadline = Time.realtimeSinceStartup + 10 };
            Network.Send(id, Wire.Packet(Op.PickupGrant, w => { w.Write(epoch); w.Write(token); Wire.Text(w, key); Wire.Text(w, supply.Item.name); }));
        }

        void ReceivePickup(int generation, int token, string key, string itemName)
        {
            pickupPending = false;
            if (generation != epoch || !worldReady) return;
            Supply supply;
            if (!supplies.TryGetValue(key, out supply) || supply.Item.name != itemName) throw new IOException("Shared item did not match this level.");
            bool success = receivedGrants.Contains(token);
            if (!success)
            {
                var inventory = Singleton<CoreGameManager>.Instance.GetPlayer(0).itm;
                if (inventory.maxItem >= 0 && !inventory.InventoryFull())
                {
                    Applying = true;
                    try
                    {
                        supply.Pickup.AssignItem(supply.Item); supply.Pickup.Hide(false); supply.Pickup.Collect(0);
                        success = true; receivedGrants.Add(token); pickupGrants++;
                        Notify("Shared item received: " + Singleton<LocalizationManager>.Instance.GetLocalizedText(supply.Item.nameKey));
                    }
                    finally { Applying = false; }
                }
                else Notify("Inventory filled before the item arrived. The item is being returned.");
            }
            Network.Send(0, Wire.Packet(Op.PickupAck, w => { w.Write(epoch); w.Write(token); w.Write(success); }));
        }

        void WriteSupplies(BinaryWriter w)
        {
            w.Write((ushort)supplies.Count);
            foreach (var pair in supplies) { Wire.Text(w, pair.Key); w.Write(claims.IsClaimed(pair.Key)); }
        }

        void ReadSupplies(BinaryReader r, bool apply)
        {
            int count = r.ReadUInt16(); if (count > 256) throw new IOException("Shared item limit exceeded.");
            for (int i = 0; i < count; i++)
            {
                string key = Wire.Text(r, 256); bool consumed = r.ReadBoolean(); Supply supply;
                if (!apply || !supplies.TryGetValue(key, out supply) || supply.Pickup == null) continue;
                if (supply.Consumed != consumed || supply.Pickup.gameObject.activeSelf == consumed)
                {
                    supply.Consumed = consumed;
                    supply.Pickup.AssignItem(consumed ? Singleton<CoreGameManager>.Instance.NoneItem : supply.Item);
                    supply.Pickup.Hide(consumed);
                }
            }
        }

        bool HandleFeatureClient(int id, Peer sender, Op op, BinaryReader r)
        {
            if (op == Op.CatalogRequest)
            {
                int generation = r.ReadInt32(); string hash = Wire.Text(r, 64);
                if (generation == epoch && worldReady)
                { if (hash != worldHash) throw new IOException("Generated level mismatch. Use identical game files and settings."); SendSupplyCatalog(id); }
                return true;
            }
            if (op == Op.PickupRequest)
            { int generation = r.ReadInt32(); string key = Wire.Text(r, 256); if (generation == epoch && worldReady) GrantPickup(id, sender, key); return true; }
            if (op == Op.PickupAck)
            {
                int generation = r.ReadInt32(), token = r.ReadInt32(); bool delivered = r.ReadBoolean(); string key;
                if (generation == epoch && claims.Resolve(token, id, delivered, out key))
                {
                    grants.Remove(token); Supply supply = supplies[key]; supply.Consumed = delivered;
                    if (!delivered) { supply.Pickup.AssignItem(supply.Item); supply.Pickup.Hide(false); }
                    else { acceptedInteractions++; Log("Shared item delivered to " + sender.Name + ": " + key); }
                }
                return true;
            }
            if (op == Op.TeamPing)
            {
                int generation = r.ReadInt32(); Vector3 position = ReadVector(r);
                if (generation == epoch && sender.Ready && sender.HasPose && !sender.Eliminated && Vector3.Distance(sender.Position, position) <= 110) RelayPing(id, position);
                return true;
            }
            return false;
        }

        bool HandleFeatureHost(Op op, BinaryReader r)
        {
            if (HandleHidingHost(op,r) || HandleShopHost(op,r)) return true;
            if (op == Op.SupplyCatalog) { ReadSupplyCatalog(r); return true; }
            if (op == Op.Reject) { string reason = Wire.Text(r, 180);RecordCompatibilityRejection(reason); Disconnect("Cannot join: " + reason); return true; }
            if (op == Op.Notice) { pickupPending = false; Notify(Wire.Text(r, 256)); return true; }
            if (op == Op.PickupGrant)
            { int generation = r.ReadInt32(), token = r.ReadInt32(); string key = Wire.Text(r, 256), item = Wire.Text(r, 128); ReceivePickup(generation, token, key, item); return true; }
            if (op == Op.TeamPing)
            { int generation = r.ReadInt32(), owner = r.ReadInt32(); Vector3 position = ReadVector(r); if (generation == epoch && peers.ContainsKey(owner)) ShowPing(owner, position); return true; }
            return false;
        }

        public void PingTeam()
        {
            if (!Active || !worldReady || LocalEliminated || (!Hosting && !readyAcknowledged)) return;
            var core = Singleton<CoreGameManager>.Instance;
            Camera camera = core.GetCamera(0).camCom;
            RaycastHit hit; Vector3 position = camera.transform.position + camera.transform.forward * 25;
            if (Physics.Raycast(camera.transform.position, camera.transform.forward, out hit, 100, ~(1 << 2), QueryTriggerInteraction.Ignore)) position = hit.point;
            if (Hosting) RelayPing(0, position);
            else Network.Send(0, Wire.Packet(Op.TeamPing, w => { w.Write(epoch); WriteVector(w, position); }));
        }

        void RelayPing(int owner, Vector3 position)
        {
            float next; if (pingCooldowns.TryGetValue(owner, out next) && Time.realtimeSinceStartup < next) return;
            pingCooldowns[owner] = Time.realtimeSinceStartup + 0.75f;
            ShowPing(owner, position);
            SendAll(Wire.Packet(Op.TeamPing, w => { w.Write(epoch); w.Write(owner); WriteVector(w, position); }));
        }

        void ShowPing(int owner, Vector3 position)
        {
            Marker previous; if (markers.TryGetValue(owner, out previous)) DestroyMarker(previous);
            Marker marker = new Marker { Position = position, Expires = Time.realtimeSinceStartup + 8 };
            marker.Object = GameObject.CreatePrimitive(PrimitiveType.Sphere); marker.Object.name = "Team ping";
            Object.Destroy(marker.Object.GetComponent<Collider>()); marker.Object.transform.position = position; marker.Object.transform.localScale = Vector3.one * 0.9f;
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            marker.Material = new Material(shader); marker.Material.color = new Color(1,0.65f,0.1f);
            if (marker.Material.HasProperty("_BaseColor")) marker.Material.SetColor("_BaseColor", marker.Material.color);
            marker.Object.GetComponent<Renderer>().material = marker.Material;
            markers[owner] = marker; teamPings++;
            PlayTeamSoundAt("ping", marker.Object);
            Peer peer; if (peers.TryGetValue(owner, out peer)) Notify(peer.Name + " marked a location.");
        }

        static void DestroyMarker(Marker marker) { if (marker.Object != null) Object.Destroy(marker.Object); if (marker.Material != null) Object.Destroy(marker.Material); }

        void DrawFeatureHud()
        {
            if (waitingForPlayers) GUI.Box(new Rect((Screen.width-480)/2,52,480,38), "Matching the host's level...", fieldStyle);
            else if (toast != null && Time.realtimeSinceStartup < toastUntil)
                GUI.Box(new Rect((Screen.width-540)/2,Screen.height-65,540,40), toast, fieldStyle);
            if (!worldReady) return;
            var core = Singleton<CoreGameManager>.Instance; if (core == null) return;
            Camera camera = core.GetCamera(0).camCom;
            foreach (var pair in markers)
            {
                Vector3 point = camera.WorldToScreenPoint(pair.Value.Position + Vector3.up * 1.5f);
                Peer peer; if (point.z <= 0 || !peers.TryGetValue(pair.Key, out peer)) continue;
                GUI.Box(new Rect(point.x-110, Screen.height-point.y,220,36), peer.Name + ": look here", fieldStyle);
            }
        }

        string ConnectionSummary()
        {
            if (!Active) return "";
            int milliseconds;
            string delay = !Hosting && latency.TryGetValue(0, out milliseconds) ? " | " + milliseconds + " ms" : "";
            return " | " + peers.Values.Count(p => p.Accepted) + "/4" + delay;
        }

        string FeatureDiagnostic()
        {
            int milliseconds;
            return ",\"supplies\":" + supplies.Count + ",\"consumedSupplies\":" + supplies.Count(p => Hosting ? claims.IsClaimed(p.Key) : p.Value.Consumed)
                + ",\"pickupGrants\":" + pickupGrants + ",\"teamPings\":" + teamPings + ",\"latencySamples\":" + latency.Count
                + ",\"rttMs\":" + (latency.TryGetValue(0, out milliseconds) ? milliseconds : -1) + ",\"loadingBarrier\":" + waitingForPlayers.ToString().ToLowerInvariant()
                + ",\"catalogApplied\":" + catalogApplied.ToString().ToLowerInvariant() + ",\"catalogPerturbed\":" + catalogPerturbed.ToString().ToLowerInvariant()
                + ",\"catalogHash\":\"" + SupplyDigest() + "\"";
        }

        string BuildDiagnostic()
        {
            return "{\"sampleUtcMs\":" + (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond) + ",\"host\":" + Hosting.ToString().ToLowerInvariant() + ",\"self\":" + self + ",\"peers\":" + peers.Values.Count(p => p.Accepted)
                + ",\"epoch\":" + epoch + ",\"ready\":" + worldReady.ToString().ToLowerInvariant() + ",\"ack\":" + readyAcknowledged.ToString().ToLowerInvariant()
                + ",\"notebooks\":" + notebooks.Values.Count(n => n != null && n.collected) + ",\"openDoors\":" + doors.Values.Count(d => d != null && d.open)
                + ",\"acceptedInteractions\":" + acceptedInteractions + ",\"observedDoorOpens\":" + observedDoorOpens + ",\"observedNpcFrames\":" + observedNpcFrames
                + ",\"completedActivities\":" + activities.Values.Count(a => a != null && a.IsCompleted) + ",\"testStep\":" + testStep + ",\"npcCount\":" + npcs.Length
                + ",\"worldHash\":\"" + worldHash + "\",\"remotePoses\":" + peers.Values.Count(p => p.Id != self && p.HasPose)
                + ",\"duplicateItemBlocked\":" + duplicateItemBlocked.ToString().ToLowerInvariant() + ",\"fullInventoryBlocked\":" + fullInventoryBlocked.ToString().ToLowerInvariant()
                + ",\"seed\":" + Seed + ",\"npcRenderSteps\":" + npcRenderSteps + ",\"extraDoors\":" + extraDoors.Count
                + ",\"playing\":" + (Singleton<BaseGameManager>.Instance != null && Singleton<BaseGameManager>.Instance.Ec != null && Singleton<BaseGameManager>.Instance.Ec.Active).ToString().ToLowerInvariant()
                + ",\"elevatorScreens\":" + Object.FindObjectsOfType<ElevatorScreen>().Length
                + ",\"npcPresentationFrames\":" + npcPresentationFrames + ",\"npcAnimationCorrections\":" + npcAnimationCorrections
                + NpcPlayerDiagnostic() + NpcLabDiagnostic() + NpcInteractionDiagnostic() + PrincipalDiagnostic() + NpcFeatureDiagnostic() + ReplicationDiagnostic() + FeatureDiagnostic() + SpriteDiagnostic() + LobbyFeatureDiagnostic() + HidingDiagnostic() + ShopDiagnostic() + FocusedDiagnostic() + LinkDiagnostic() + SteamDiagnostic() + "}";
        }
    }
}
