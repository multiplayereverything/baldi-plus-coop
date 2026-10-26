using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        sealed class ShopSlot
        {
            public Pickup Pickup; public StoreRoomFunction Store; public PriceTag Tag;
            public ItemObject Item; public int Price; public bool Sold, Pending, IsMap, IsGlue;
        }
        sealed class ShopDelivery { public int Buyer, Price, Token; public string Key; public float Deadline; }
        // Deliveries past their deadline, kept so a late ack still settles them: a failure refunds, a success books the sale.
        readonly Dictionary<int, ShopDelivery> shopExpired = new Dictionary<int, ShopDelivery>();
        float shopAckSentAt = -1; // lab: when this client last sent a shop ack (the focused test waits for the host's answer to it)
        int shopLateRefunds, shopLateSales, glueTestShown = -1, glueTestCharged = -1, glueHostShelfPrice = -1, glueOwnedAtShow = -1;
        readonly Dictionary<string, ShopSlot> shopStock = new Dictionary<string, ShopSlot>();
        readonly Dictionary<string, StoreRoomFunction> shopStores = new Dictionary<string, StoreRoomFunction>();
        readonly Dictionary<int, ShopDelivery> shopDeliveries = new Dictionary<int, ShopDelivery>();
        readonly HashSet<int> shopReceipts = new HashSet<int>();
        readonly HashSet<string> shopMapsApplied = new HashSet<string>();
        readonly Dictionary<int, float> shopRequestTimes = new Dictionary<int, float>();
        Dictionary<string, ItemObject> shopAssets;
        bool shopPending; float shopPendingUntil; int shopSerial, shopPurchases, shopRefunds, shopRejected;
        int shopReplicaFrames, shopLastWallet, shopLastCharge, focusedShopStep, focusedShopCost;
        float focusedShopNext; string focusedShopKey; bool focusedShopRefundReceived;

        static T ShopField<T>(StoreRoomFunction store, string name)
        { return (T)AccessTools.Field(typeof(StoreRoomFunction), name).GetValue(store); }

        void ResetShop()
        {
            // Disconnect cannot safely infer delivery, so only explicit failed acknowledgements refund.
            shopStock.Clear(); shopStores.Clear(); shopDeliveries.Clear(); shopExpired.Clear(); shopReceipts.Clear();
            shopMapsApplied.Clear(); shopRequestTimes.Clear(); shopPending = false; shopAssets = null;
            shopReplicaFrames = 0; shopLastWallet = -1;
        }

        public bool IsShopPickup(Pickup pickup)
        { return pickup != null && shopStock.Values.Any(s => s.Pickup == pickup); }

        void BuildShopWorld()
        {
            shopStock.Clear(); shopStores.Clear();
            foreach (StoreRoomFunction store in Object.FindObjectsOfType<StoreRoomFunction>(true))
            {
                shopStores[Key(store)] = store;
                var ordinary = ShopField<List<Pickup>>(store, "pickups");
                var tags = ShopField<PriceTag[]>(store, "tag");
                for (int i = 0; i < ordinary.Count; i++) AddShopSlot(store, ordinary[i], i < tags.Length ? tags[i] : null, false, false);
                var stickers = ShopField<Pickup[]>(store, "stickerPickup");
                var stickerTags = ShopField<PriceTag[]>(store, "stickerTag");
                for (int i = 0; i < stickers.Length; i++) AddShopSlot(store, stickers[i], i < stickerTags.Length ? stickerTags[i] : null, false, false);
                AddShopSlot(store, ShopField<Pickup>(store, "mapPickup"), ShopField<PriceTag>(store, "mapTag"), true, false);
                AddShopSlot(store, ShopField<Pickup>(store, "glueStickPickup"), ShopField<PriceTag>(store, "glueStickTag"), false, true);
            }
            if (shopStock.Count > 64) throw new IOException("Shop stock limit exceeded.");
        }

        void AddShopSlot(StoreRoomFunction store, Pickup pickup, PriceTag tag, bool map, bool glue)
        {
            if (pickup == null) return;
            string key = Key(pickup);
            if (shopStock.ContainsKey(key)) throw new IOException("Duplicate shop stock ID.");
            shopStock[key] = new ShopSlot { Pickup = pickup, Store = store, Tag = tag, Item = pickup.item,
                Price = pickup.price, Sold = !pickup.gameObject.activeSelf || pickup.item == null || pickup.item.itemType == Items.None, IsMap = map, IsGlue = glue };
        }

        // Glue sticks are a personal sticker upgrade: the game prices one at 1000 + 500 per glue stick the player owns and
        // stops selling at 4 (StoreRoomFunction). The shared shelf used the host's count for everyone; each player now
        // sees and pays their own price, and the host accepts any price that matches a possible count.
        public const int GlueStickLimit = 4;
        public static int GluePriceFor(int owned) { return 1000 + 500 * owned; }
        public static bool GlueQuoteValid(int price) { return price >= GluePriceFor(0) && price <= GluePriceFor(GlueStickLimit - 1) && (price - 1000) % 500 == 0; }
        // lab mutant (-bt-lab-no-own-glue): one host-priced glue stick for everyone, the 0.9.0 behaviour
        static bool LabNoOwnGlue { get { return Lab.Enabled && Lab.HasArg("-bt-lab-no-own-glue"); } }
        static int OwnGlueSticks() { var stickers = Singleton<StickerManager>.Instance; return stickers == null ? 0 : stickers.TotalGlueSticks; }

        public bool BeforeShopClicked(Pickup pickup)
        {
            if(Active&&LocalEliminated&&!Applying)return false;
            if (!Active || Applying || !worldReady || !IsShopPickup(pickup)) return true;
            if (!Hosting && shopReplicaFrames == 0) { Notify("Waiting for Johnny's shop stock to synchronize."); return false; }
            string key = Key(pickup); ShopSlot slot = shopStock[key];
            if (slot.Sold || slot.Pending) { Notify("That shop item is sold or another purchase is pending."); return false; }
            if (!HasShopInventorySpace(slot.Item)) { Notify("Make an empty inventory slot before buying an item."); return false; }
            if (Hosting) PurchaseShop(0, null, key);
            else if (readyAcknowledged && !shopPending)
            {
                shopPending = true; shopPendingUntil = Time.realtimeSinceStartup + 10;
                SendShopRequest(key);
            }
            return false;
        }

        public bool BeforeShopCollect(Pickup pickup)
        {
            if (!Active || Applying || !worldReady || !IsShopPickup(pickup)) return true;
            // Collect can bypass Clicked (Johnny aid, direct interaction). Route it through the same purchase gate.
            return BeforeShopClicked(pickup);
        }

        bool HasShopInventorySpace(ItemObject item)
        {
            if (item == null || item.itemType == Items.None) return false;
            if (!item.addToInventory) return true;
            var core = Singleton<CoreGameManager>.Instance;
            if (core == null || core.GetPlayer(0) == null) return false;
            var inventory = core.GetPlayer(0).itm;
            return inventory.maxItem >= 0 && !inventory.InventoryFull();
        }

        void ShopDenied(int buyer, string message)
        {
            shopRejected++;
            if (buyer == 0) Notify(message);
            else Network.Send(buyer, Wire.Packet(Op.ShopGrant, w => { w.Write(epoch); w.Write(0); Wire.Text(w, message); }));
        }

        // remotePrice: the buyer's own glue-stick price (validated by GlueQuoteValid); -1 for every other item.
        void PurchaseShop(int buyer, Peer peer, string key, int remotePrice = -1)
        {
            ShopSlot slot;
            if (!shopStock.TryGetValue(key, out slot) || slot.Pickup == null || slot.Sold || slot.Pending || !slot.Pickup.gameObject.activeInHierarchy
                || slot.Pickup.item == null || slot.Pickup.item.itemType == Items.None)
            { ShopDenied(buyer, "That shop item is no longer available."); return; }
            if (buyer != 0 && (peer == null || !peer.Ready || !peer.HasPose || peer.Eliminated || Vector3.Distance(peer.Position, slot.Pickup.transform.position) > 20))
            { ShopDenied(buyer, "Move closer to the shop item before buying it."); return; }
            var core = Singleton<CoreGameManager>.Instance;
            int price = slot.Pickup.free ? 0 : buyer != 0 && slot.IsGlue && remotePrice >= 0 && !LabNoOwnGlue ? remotePrice : Math.Max(0, slot.Pickup.price);
            if (buyer != 0 && slot.IsGlue) { glueTestCharged = price; glueHostShelfPrice = slot.Pickup.price; }
            if (core.GetPoints(0) < price) { ShopDenied(buyer, "The team needs " + price + " YTPs for that item."); return; }
            slot.Item = slot.Pickup.item; slot.Price = slot.Pickup.price;
            if (buyer == 0)
            {
                if (!HasShopInventorySpace(slot.Item)) { ShopDenied(0, "Make an empty inventory slot before buying an item."); return; }
                bool previous = Applying; Applying = true;
                try { slot.Pickup.Clicked(0); shopPurchases++; RefreshShopSlot(slot); }
                finally { Applying = previous; }
                return;
            }
            int token = ++shopSerial;
            // Funds and stock are reserved before sending. A retry cannot buy the same shelf item twice.
            core.AddPoints(-price, 0, true, false, false);
            slot.Pending = true; slot.Pickup.Hide(true);
            shopDeliveries[token] = new ShopDelivery { Buyer = buyer, Token = token, Price = price, Key = key, Deadline = Time.realtimeSinceStartup + 12 };
            shopLastCharge = price;
            Network.Send(buyer, Wire.Packet(Op.ShopGrant, w => { w.Write(epoch); w.Write(token); Wire.Text(w, key); Wire.Text(w, slot.Item.name); w.Write(price); }));
        }

        void RefreshShopSlot(ShopSlot slot)
        {
            if (slot.Pending || slot.Pickup == null) return;
            slot.Sold = !slot.Pickup.gameObject.activeSelf || slot.Pickup.item == null || slot.Pickup.item.itemType == Items.None;
            if (!slot.Sold) { slot.Item = slot.Pickup.item; slot.Price = slot.Pickup.price; }
        }

        // LevelBuilder parents room functions to a controller placed at the level origin, not the room.
        static Vector3 ShopRoomMiddle(StoreRoomFunction store)
        {
            var manager = Singleton<BaseGameManager>.Instance;
            if (store.Room != null && manager != null && manager.Ec != null) return manager.Ec.RealRoomMid(store.Room);
            return store.transform.position;
        }

        void ReceiveShopGrant(int generation, int token, string key, string itemName, int chargedPrice)
        {
            shopPending = false;
            if (generation != epoch) return;
            ShopSlot slot;
            if (!worldReady || !shopStock.TryGetValue(key, out slot))
            {
                Log("Shop grant " + key + " could not be placed; asking the host for a refund.");
                SendShopAck(token, false);
                return;
            }
            ItemObject item = FindShopAsset(itemName);
            bool delivered = shopReceipts.Contains(token);
            if (!delivered && HasShopInventorySpace(item))
            {
                bool previous = Applying; Applying = true;
                try
                {
                    slot.Item = item; slot.Pickup.AssignItem(item); slot.Pickup.Hide(false);
                    slot.Pickup.Collect(0); delivered = true; shopReceipts.Add(token); shopPurchases++; shopLastCharge = chargedPrice;
                    Notify("Purchased from the team wallet: " + Singleton<LocalizationManager>.Instance.GetLocalizedText(item.nameKey));
                }
                finally { Applying = previous; }
            }
            if (!delivered) { focusedShopRefundReceived = true; Notify("Inventory filled before the purchase arrived. The team will be refunded."); }
            SendShopAck(token, delivered);
        }

        // Lab (-bt-lab-delay-shop-ack N): hold the buyer's ack for N s, past the host's 12 s deadline, to prove late acks settle.
        void SendShopAck(int token, bool delivered)
        {
            int generation = epoch;
            byte[] ack = Wire.Packet(Op.ShopAck, w => { w.Write(generation); w.Write(token); w.Write(delivered); });
            float delay = LabArgSeconds("-bt-lab-delay-shop-ack");
            if (delay <= 0) { Network.Send(0, ack); shopAckSentAt = Time.realtimeSinceStartup; return; }
            Log("LAB: holding shop ack " + token + " (" + (delivered ? "delivered" : "failed") + ") for " + delay + " s");
            StartCoroutine(SendShopAckLater(ack, delay));
        }

        System.Collections.IEnumerator SendShopAckLater(byte[] ack, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (Network != null && !Hosting) { Network.Send(0, ack); shopAckSentAt = Time.realtimeSinceStartup; }
        }

        static float LabArgSeconds(string flag)
        {
            if (!Lab.Enabled) return 0;
            string[] args = Environment.GetCommandLineArgs(); float value;
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == flag && float.TryParse(args[i + 1], out value)) return Mathf.Clamp(value, 0, 60);
            return 0;
        }

        ItemObject FindShopAsset(string name)
        {
            if (shopAssets == null) shopAssets = Resources.FindObjectsOfTypeAll<ItemObject>().GroupBy(i => i.name).ToDictionary(g => g.Key, g => g.First());
            ItemObject item; if (!shopAssets.TryGetValue(name, out item)) throw new IOException("Host shop item asset is unavailable: " + name);
            return item;
        }

        bool HandleShopClient(int id, Peer peer, Op op, BinaryReader r)
        {
            if (op == Op.ShopRequest)
            {
                int generation = r.ReadInt32(); byte kind = r.ReadByte(); string key = Wire.Text(r, 256);
                string quotedItem = kind == 0 ? Wire.Text(r, 128) : "";
                int quotedPrice = kind == 0 ? r.ReadInt32() : 0;
                if (generation != epoch || !worldReady) return true;
                if (kind > 1) throw new IOException("Invalid shop request.");
                float next;
                if (shopRequestTimes.TryGetValue(id, out next) && Time.realtimeSinceStartup < next) { ShopDenied(id, "Wait for your previous shop request."); return true; }
                shopRequestTimes[id] = Time.realtimeSinceStartup + .5f;
                if (kind == 0)
                {
                    ShopSlot quotedSlot;
                    bool glue = shopStock.TryGetValue(key, out quotedSlot) && quotedSlot.IsGlue;
                    if (quotedSlot != null && !quotedSlot.Sold && !quotedSlot.Pending && quotedSlot.Pickup != null
                        && (quotedSlot.Pickup.item == null || quotedSlot.Pickup.item.name != quotedItem
                            || (glue && !quotedSlot.Pickup.free ? !GlueQuoteValid(quotedPrice) : (quotedSlot.Pickup.free ? 0 : quotedSlot.Pickup.price) != quotedPrice)))
                    { ShopDenied(id, "Johnny's stock or price changed. Check the updated shelf and try again."); return true; }
                    PurchaseShop(id, peer, key, glue ? quotedPrice : -1);
                }
                else
                {
                    StoreRoomFunction store;
                    if (!peer.Ready || !peer.HasPose || peer.Eliminated || !shopStores.TryGetValue(key, out store) || Vector3.Distance(peer.Position, ShopRoomMiddle(store)) > 50)
                    { ShopDenied(id, "Move closer to Johnny before restocking."); return true; }
                    RestockShop(store, id);
                }
                return true;
            }
            if (op == Op.ShopAck)
            {
                int generation = r.ReadInt32(), token = r.ReadInt32(); bool delivered = r.ReadBoolean(); ShopDelivery delivery;
                if (generation == epoch && shopExpired.TryGetValue(token, out delivery) && delivery.Buyer == id) { SettleLateShopAck(token, delivery, delivered); return true; }
                if (generation != epoch || !shopDeliveries.TryGetValue(token, out delivery) || delivery.Buyer != id) return true;
                shopDeliveries.Remove(token); ShopSlot slot = shopStock[delivery.Key]; slot.Pending = false;
                if (!delivered)
                {
                    Singleton<CoreGameManager>.Instance.AddPoints(delivery.Price, 0, true, false, false); shopRefunds++;
                    slot.Pickup.AssignItem(slot.Item); slot.Pickup.price = slot.Price; slot.Pickup.Hide(false); slot.Sold = false;
                }
                else
                {
                    // Invoke the shop bookkeeping, without granting the remote buyer's item to the host.
                    AccessTools.Method(typeof(StoreRoomFunction), "ItemPurchased").Invoke(slot.Store, new object[] { slot.Pickup, 0 });
                    AccessTools.Method(typeof(StoreRoomFunction), "ItemCollected").Invoke(slot.Store, new object[] { slot.Pickup, 0 });
                    slot.Pickup.AssignItem(Singleton<CoreGameManager>.Instance.NoneItem); slot.Pickup.Hide(true); slot.Sold = true; shopPurchases++;
                }
                return true;
            }
            return false;
        }

        // The buyer's ack arrived after the deadline, which closed the slot as sold. Settle it now.
        void SettleLateShopAck(int token, ShopDelivery delivery, bool delivered)
        {
            shopExpired.Remove(token);
            ShopSlot slot = shopStock[delivery.Key];
            if (!delivered)
            {
                Singleton<CoreGameManager>.Instance.AddPoints(delivery.Price, 0, true, false, false); shopRefunds++; shopLateRefunds++;
                // put the item back only if the shelf still shows the deadline's sold state (not restocked meanwhile)
                if (slot.Sold && !slot.Pending && slot.Item != null && slot.Pickup.item == Singleton<CoreGameManager>.Instance.NoneItem)
                { slot.Pickup.AssignItem(slot.Item); slot.Pickup.price = slot.Price; slot.Pickup.Hide(false); slot.Sold = false; }
                Log("Shop: late failure ack for " + delivery.Key + "; refunded " + delivery.Price + ".");
            }
            else
            {
                // the same bookkeeping as an on-time ack (ItemCollected marks it sold and applies a bought map)
                AccessTools.Method(typeof(StoreRoomFunction), "ItemPurchased").Invoke(slot.Store, new object[] { slot.Pickup, 0 });
                AccessTools.Method(typeof(StoreRoomFunction), "ItemCollected").Invoke(slot.Store, new object[] { slot.Pickup, 0 });
                shopPurchases++; shopLateSales++;
                Log("Shop: late delivery ack for " + delivery.Key + ".");
            }
        }

        bool HandleShopHost(Op op, BinaryReader r)
        {
            if (op != Op.ShopGrant) return false;
            int generation = r.ReadInt32(), token = r.ReadInt32(); string key = Wire.Text(r, 256);
            if (token == 0) { if (generation == epoch) { shopPending = false; Notify(key); } return true; }
            string item = Wire.Text(r, 128); int price = r.ReadInt32();
            if (price < 0) throw new IOException("Invalid shop receipt price.");
            ReceiveShopGrant(generation, token, key, item, price); return true;
        }

        public bool BeforeShopRestock(StoreRoomFunction store)
        {
            if(Active&&LocalEliminated&&!Applying)return false;
            if (!Active || Applying || !worldReady) return true;
            if (!Hosting && shopReplicaFrames == 0) { Notify("Waiting for Johnny's shop stock to synchronize."); return false; }
            if (Hosting) RestockShop(store, 0);
            else if (readyAcknowledged && !shopPending)
            {
                shopPending = true; shopPendingUntil = Time.realtimeSinceStartup + 3;
                Network.Send(0, Wire.Packet(Op.ShopRequest, w => { w.Write(epoch); w.Write((byte)1); Wire.Text(w, Key(store)); }));
            }
            return false;
        }

        void RestockShop(StoreRoomFunction store, int buyer)
        {
            if (shopStock.Values.Any(s => s.Store == store && s.Pending)) { ShopDenied(buyer, "Wait for pending shop purchases before restocking."); return; }
            int price = ShopField<int>(store, "restockPrice");
            if (Singleton<CoreGameManager>.Instance.GetPoints(0) < price) { ShopDenied(buyer, "The team needs " + price + " YTPs to restock."); return; }
            bool previous = Applying; Applying = true;
            try { store.PurchaseRestock(); foreach (ShopSlot slot in shopStock.Values.Where(s => s.Store == store)) RefreshShopSlot(slot); }
            finally { Applying = previous; }
            if (buyer != 0) ShopDenied(buyer, "Johnny restocked the shop using the team wallet.");
        }

        void UpdateShop()
        {
            if (shopPending && Time.realtimeSinceStartup > shopPendingUntil) { shopPending = false; Notify("Shop request finished or timed out. Check the shelf before retrying."); }
            if (!Hosting || !worldReady) return;
            foreach (ShopSlot slot in shopStock.Values) RefreshShopSlot(slot);
            foreach (var pair in shopExpired.Where(p => Time.realtimeSinceStartup > p.Value.Deadline || !peers.ContainsKey(p.Value.Buyer)).ToArray()) shopExpired.Remove(pair.Key);
            foreach (var pair in shopDeliveries.Where(p => Time.realtimeSinceStartup > p.Value.Deadline || !peers.ContainsKey(p.Value.Buyer)).ToArray())
            {
                // Delivery can have succeeded before a lost ACK/disconnect. Never refund an uncertain delivery.
                ShopSlot slot = shopStock[pair.Value.Key]; slot.Pending = false; slot.Sold = true;
                slot.Pickup.AssignItem(Singleton<CoreGameManager>.Instance.NoneItem); slot.Pickup.Hide(true); shopDeliveries.Remove(pair.Key);
                if (peers.ContainsKey(pair.Value.Buyer) && !(Lab.Enabled && Lab.HasArg("-bt-lab-no-late-ack"))) { pair.Value.Deadline = Time.realtimeSinceStartup + 120; shopExpired[pair.Key] = pair.Value; }
            }
        }

        void WriteShopWorld(BinaryWriter w)
        {
            var core = Singleton<CoreGameManager>.Instance;
            w.Write(core.GetPoints(0)); w.Write(core.nextLevel != null && core.levelMapHasBeenPurchasedFor == core.nextLevel);
            w.Write((byte)shopStock.Count);
            foreach (var pair in shopStock)
            {
                ShopSlot slot = pair.Value; RefreshShopSlot(slot);
                Wire.Text(w, pair.Key); Wire.Text(w, slot.Item == null ? "" : slot.Item.name);
                w.Write(slot.Price); w.Write(slot.Sold); w.Write(slot.Pending); w.Write(slot.Pickup.free);
            }
        }

        void ReadShopWorld(BinaryReader r, bool apply)
        {
            int points = r.ReadInt32(); bool mapPurchased = r.ReadBoolean();
            int count = r.ReadByte(); if (count > 64) throw new IOException("Shop stock limit exceeded.");
            if (apply)
            {
                var core = Singleton<CoreGameManager>.Instance;
                core.AddPoints(points - core.GetPoints(0), 0, false, false, false);
                shopLastWallet = points; shopReplicaFrames++;
            }
            for (int i = 0; i < count; i++)
            {
                string key = Wire.Text(r, 256), itemName = Wire.Text(r, 128); int price = r.ReadInt32();
                bool sold = r.ReadBoolean(), pending = r.ReadBoolean(), free = r.ReadBoolean(); ShopSlot slot;
                if (price < 0) throw new IOException("Invalid shop price.");
                if (!apply) continue;
                if (!shopStock.TryGetValue(key, out slot)) throw new IOException("Host shop layout does not match this client.");
                slot.Item = itemName.Length == 0 ? null : FindShopAsset(itemName); slot.Price = price; slot.Sold = sold; slot.Pending = pending;
                if (slot.Item != null) slot.Pickup.AssignItem(sold ? Singleton<CoreGameManager>.Instance.NoneItem : slot.Item);
                slot.Pickup.price = sold ? 0 : price; slot.Pickup.free = free; slot.Pickup.showDescription = !sold; slot.Pickup.Hide(sold || pending);
                if (slot.Tag != null)
                {
                    if (sold) slot.Tag.SetText(Singleton<LocalizationManager>.Instance.GetLocalizedText("TAG_Sold"));
                    else if (slot.Item != null && price < slot.Item.price && !free) slot.Tag.SetSale(slot.Item.price, price);
                    else slot.Tag.SetText(price.ToString());
                }
                if (slot.IsGlue && !sold && !free && !LabNoOwnGlue) ApplyOwnGluePrice(slot);
                if (mapPurchased && slot.IsMap && shopMapsApplied.Add(key)) slot.Store.BuyMap();
            }
        }

        // Client: show the shared glue-stick shelf at this player's own price, or sold out once they own the limit.
        void ApplyOwnGluePrice(ShopSlot slot)
        {
            int owned = OwnGlueSticks();
            if (owned >= GlueStickLimit)
            {
                slot.Sold = true; slot.Pickup.price = 0; slot.Pickup.showDescription = false; slot.Pickup.Hide(true);
                if (slot.Tag != null) slot.Tag.SetText(Singleton<LocalizationManager>.Instance.GetLocalizedText("TAG_Sold"));
                return;
            }
            slot.Price = GluePriceFor(owned); slot.Pickup.price = slot.Price;
            if (slot.Tag != null) slot.Tag.SetText(slot.Price.ToString());
        }

        public bool ShopPointsAllowed() { return !Active || Hosting || Applying || !Running; }

        // Explicit lab flag only: exercise actual generated shelf pickups through the normal network handlers.
        public bool RunShopFocusedTest()
        {
            if (!FocusedSync || !worldReady || shopStock.Count == 0 || (!Hosting && !readyAcknowledged)) return false;
            var core = Singleton<CoreGameManager>.Instance;
            if (Hosting)
            {
                if (focusedShopStep == 0)
                {
                    core.AddPoints(-core.GetPoints(0), 0, false, false, false); focusedShopStep = 1;
                    Log("FOCUSED SHOP: team wallet emptied for insufficient-funds check.");
                }
                else if (focusedShopStep == 1 && shopRejected > 0)
                {
                    core.AddPoints(10000 - core.GetPoints(0), 0, false, false, false); focusedShopStep = 2;
                    Log("FOCUSED SHOP: seeded isolated lab wallet with 10000 YTPs.");
                }
                else if (focusedShopStep == 2 && shopRefunds > 0) focusedShopStep = 3;
                if (focusedShopStep == 3 && shopPurchases > 0 && shopRejected >= 2) focusedShopStep = 4;
                return focusedShopStep == 4;
            }
            if (Time.realtimeSinceStartup < focusedShopNext) return false;
            var inventory = core.GetPlayer(0).itm;
            if (focusedShopStep == 0)
            {
                var entry = shopStock.FirstOrDefault(p => !p.Value.Sold && !p.Value.Pickup.free && p.Value.Price > 0 && p.Value.Item != null && p.Value.Item.addToInventory);
                if (entry.Value == null || shopReplicaFrames == 0 || shopLastWallet != 0) return false;
                focusedShopKey = entry.Key; focusedShopCost = entry.Value.Price;
                inventory.ClearItems(); core.GetPlayer(0).plm.Entity.Teleport(entry.Value.Pickup.transform.position + Vector3.back * 4);
                focusedShopNext = Time.realtimeSinceStartup + 1; focusedShopStep = 1;
            }
            else if (focusedShopStep == 1) { SendFocusedShopRequest(); focusedShopNext = Time.realtimeSinceStartup + .75f; focusedShopStep = 2; }
            else if (focusedShopStep == 2 && shopLastWallet == 10000)
            {
                // Deliberately bypass the client UI precheck to model a slot filling in transit.
                for (int i = 0; i <= inventory.maxItem; i++) inventory.items[i] = shopStock[focusedShopKey].Item;
                inventory.UpdateItems(); SendFocusedShopRequest(); focusedShopStep = 3;
            }
            // the snapshot can still show the pre-reservation wallet right after the failed delivery: wait for the ack to go out and settle
            else if (focusedShopStep == 3 && focusedShopRefundReceived && shopAckSentAt > 0 && Time.realtimeSinceStartup > shopAckSentAt + 1 && shopLastWallet == 10000 && !shopStock[focusedShopKey].Pending)
            {
                inventory.ClearItems(); focusedShopNext = Time.realtimeSinceStartup + .75f; focusedShopStep = 4;
            }
            else if (focusedShopStep == 4) { focusedShopCost = shopStock[focusedShopKey].Pickup.free ? 0 : shopStock[focusedShopKey].Price; SendFocusedShopRequest(); focusedShopStep = 5; }
            else if (focusedShopStep == 5 && shopReceipts.Count > 0 && shopStock[focusedShopKey].Sold)
            {
                SendFocusedShopRequest(); focusedShopStep = 6;
                Log("FOCUSED SHOP: purchase delivered; duplicate shelf request submitted.");
                focusedShopNext = Time.realtimeSinceStartup + 1.5f;
            }
            else if (focusedShopStep == 6)
            {
                // Glue stick at this player's own price: the lab profile gets 2 glue stickers, so 2000 here while
                // the host (none) still sees 1000 on the same shelf.
                var glue = shopStock.FirstOrDefault(p => p.Value.IsGlue && !p.Value.Sold && !p.Value.Pending);
                if (glue.Value == null) { focusedShopStep = 9; Log("FOCUSED SHOP: no glue stick on this shelf."); return true; }
                core.GetPlayer(0).plm.Entity.Teleport(glue.Value.Pickup.transform.position + Vector3.back * 4); // within the host's 20-unit purchase range
                if (OwnGlueSticks() < 2) { Singleton<StickerManager>.Instance.AddStickerToInventory(Sticker.GlueStick, false); Singleton<StickerManager>.Instance.AddStickerToInventory(Sticker.GlueStick, false); }
                focusedShopNext = Time.realtimeSinceStartup + 1; focusedShopStep = 7; // wait for a snapshot to show the own price
            }
            else if (focusedShopStep == 7)
            {
                var glue = shopStock.First(p => p.Value.IsGlue);
                glueTestShown = glue.Value.Pickup.price; glueOwnedAtShow = OwnGlueSticks();
                focusedShopKey = glue.Key; focusedShopCost = glue.Value.Price;
                Log("FOCUSED SHOP: glue stick shown at " + glueTestShown + " with " + OwnGlueSticks() + " glue sticks owned; buying.");
                SendFocusedShopRequest(); focusedShopStep = 8;
            }
            return focusedShopStep >= 6;
        }

        void SendFocusedShopRequest()
        { SendShopRequest(focusedShopKey); }

        void SendShopRequest(string key)
        {
            ShopSlot slot = shopStock[key];
            Network.Send(0, Wire.Packet(Op.ShopRequest, w =>
            { w.Write(epoch); w.Write((byte)0); Wire.Text(w, key); Wire.Text(w, slot.Item == null ? "" : slot.Item.name); w.Write(slot.Pickup.free ? 0 : slot.Price); }));
        }

        string ShopDiagnostic()
        { return ",\"shopStock\":" + shopStock.Count + ",\"shopPurchases\":" + shopPurchases + ",\"shopRefunds\":" + shopRefunds + ",\"shopRejected\":" + shopRejected + ",\"shopReserved\":" + shopDeliveries.Count
            + ",\"shopReplicaFrames\":" + shopReplicaFrames + ",\"shopWallet\":" + (Singleton<CoreGameManager>.Instance == null ? -1 : Singleton<CoreGameManager>.Instance.GetPoints(0))
            + ",\"glueShown\":" + glueTestShown + ",\"glueCharged\":" + glueTestCharged + ",\"glueHostShelf\":" + glueHostShelfPrice + ",\"glueOwned\":" + glueOwnedAtShow + ",\"shopLateRefunds\":" + shopLateRefunds + ",\"shopLateSales\":" + shopLateSales + ",\"shopTestStep\":" + focusedShopStep + ",\"shopTestCost\":" + focusedShopCost + ",\"shopLastCharge\":" + shopLastCharge; }
    }
}
