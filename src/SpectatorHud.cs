using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace BaldiTogether
{
    // Living players share their inventory and stamina so a spectator's own HUD can show the watched teammate.
    // The spectator's ItemManager and PlayerMovement are already disabled, so nothing local overwrites it.
    public sealed partial class TogetherRuntime
    {
        const int MaxHudSlots = 9;
        float nextHudSend;
        string appliedHudSignature;
        Dictionary<string, ItemObject> hudItemCatalog;

        void UpdateHudShare()
        {
            if (!worldReady || LocalEliminated || deathSent || (!Hosting && !readyAcknowledged)) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextHudSend) return;
            nextHudSend = now + .25f;
            var core = Singleton<CoreGameManager>.Instance;
            var player = core == null ? null : core.GetPlayer(0);
            if (player == null || player.itm == null || player.plm == null) return;
            var items = player.itm;
            int count = Mathf.Clamp(items.maxItem + 1, 0, MaxHudSlots);
            string[] names = new string[count];
            for (int i = 0; i < count; i++) names[i] = items.items[i] == null ? "" : items.items[i].name;
            float max = player.plm.StaminaMax;
            float stamina = Mathf.Clamp(max > 0 ? player.plm.stamina / max : 0, 0, 2);
            int selected = Mathf.Clamp(items.selectedItem, 0, Math.Max(0, count - 1));
            if (Hosting)
                SendAll(HudPacket(self, stamina, selected, names));
            else
                Network.Send(0, Wire.Packet(Op.PlayerHud, w => { w.Write(epoch); WriteHud(w, stamina, selected, names); }));
        }

        byte[] HudPacket(int id, float stamina, int selected, string[] names)
        { return Wire.Packet(Op.PlayerHud, w => { w.Write(epoch); w.Write(id); WriteHud(w, stamina, selected, names); }); }

        static void WriteHud(BinaryWriter w, float stamina, int selected, string[] names)
        {
            w.Write(stamina); w.Write((byte)selected); w.Write((byte)names.Length);
            foreach (string name in names) Wire.Text(w, name);
        }

        static void ReadHud(BinaryReader r, out float stamina, out int selected, out string[] names)
        {
            stamina = Mathf.Clamp(Wire.Number(r), 0, 2); selected = r.ReadByte(); int count = r.ReadByte();
            if (count > MaxHudSlots || selected >= Math.Max(1, count)) throw new IOException("Invalid player HUD.");
            names = new string[count];
            for (int i = 0; i < count; i++) names[i] = Wire.Text(r, 64);
        }

        static void StoreHud(Peer peer, float stamina, int selected, string[] names)
        { peer.HudStamina = stamina; peer.HudSelected = selected; peer.HudItems = names; peer.HudAt = Time.realtimeSinceStartup; }

        bool HandleHudClient(int id, Peer sender, Op op, BinaryReader r)
        {
            if (op != Op.PlayerHud) return false;
            int generation = r.ReadInt32(); float stamina; int selected; string[] names;
            ReadHud(r, out stamina, out selected, out names);
            if (generation != epoch || !sender.Ready || sender.Eliminated) return true;
            StoreHud(sender, stamina, selected, names);
            byte[] relay = HudPacket(id, stamina, selected, names);
            foreach (Peer peer in peers.Values.ToArray()) if (peer.Id != 0 && peer.Id != id && peer.Accepted) Network.Send(peer.Id, relay);
            return true;
        }

        bool HandleHudHost(Op op, BinaryReader r)
        {
            if (op != Op.PlayerHud) return false;
            int generation = r.ReadInt32(), id = r.ReadInt32(); float stamina; int selected; string[] names;
            ReadHud(r, out stamina, out selected, out names);
            Peer peer;
            if (generation == epoch && id != self && peers.TryGetValue(id, out peer)) StoreHud(peer, stamina, selected, names);
            return true;
        }

        ItemObject HudItem(string name)
        {
            if (hudItemCatalog == null)
            {
                hudItemCatalog = new Dictionary<string, ItemObject>();
                foreach (var item in Resources.FindObjectsOfTypeAll<ItemObject>())
                    if (item != null && !hudItemCatalog.ContainsKey(item.name)) hudItemCatalog.Add(item.name, item);
            }
            ItemObject found;
            return name != null && hudItemCatalog.TryGetValue(name, out found) ? found : null;
        }

        // Called every spectator frame; icons only change when the watched inventory does.
        void ApplySpectatorHud(Peer target)
        {
            var core = Singleton<CoreGameManager>.Instance;
            if (core == null || spectatorHud == null) return;
            bool visible = target != null && !spectatorBlank && target.HudItems != null && Time.realtimeSinceStartup - target.HudAt < 3;
            if (spectatorHud.activeSelf != visible) spectatorHud.SetActive(visible);
            if (!visible) { appliedHudSignature = null; return; }
            var hud = core.GetHud(0);
            hud.SetStaminaValue(target.HudStamina);
            string signature = target.Id + ":" + target.HudSelected + ":" + string.Join("|", target.HudItems);
            if (signature == appliedHudSignature) return;
            appliedHudSignature = signature;
            int count = Math.Max(1, target.HudItems.Length);
            hud.UpdateInventorySize(count);
            ItemObject empty = core.GetPlayer(0).itm.nothing;
            for (int i = 0; i < count; i++)
            {
                ItemObject item = i < target.HudItems.Length ? HudItem(target.HudItems[i]) : null;
                if (item == null) item = empty;
                if (item != null) hud.UpdateItemIcon(i, item.itemSpriteSmall);
            }
            ItemObject selected = target.HudSelected < target.HudItems.Length ? HudItem(target.HudItems[target.HudSelected]) : null;
            hud.SetItemSelect(Mathf.Clamp(target.HudSelected, 0, count - 1), selected == null ? "" : selected.nameKey);
        }

        // Lab evidence: each peer's synced colour, the colour its avatar renders, and its latest shared HUD.
        string LobbyFeatureDiagnostic()
        {
            return ",\"lobbyFeatures\":\"" + string.Join(";", peers.Values.OrderBy(p => p.Id).Select(p => p.Id + ":colour=" + p.Colour
                + ":avatar=" + (p.Visual == null ? -1 : p.Visual.Colour) + ":hudSlots=" + (p.HudItems == null ? -1 : p.HudItems.Length)
                + ":hudAge=" + (p.HudItems == null ? -1 : (int)(Time.realtimeSinceStartup - p.HudAt))).ToArray()) + "\""
                + ",\"lanBeacon\":" + (lanBeacon != null).ToString().ToLowerInvariant()
                + ",\"teamSoundsLoaded\":" + TeamSoundNames.Count(n => TeamSound(n) != null) + ",\"teamSoundsPlayed\":" + teamSoundsPlayed + ",\"teamSoundLog\":\"" + string.Join(",", teamSoundLog.ToArray()) + "\"";
        }

        // Put the spectator's own inventory and stamina back on the HUD for the next round.
        void RestoreLocalHud(PlayerManager player)
        {
            appliedHudSignature = null;
            if (player == null || player.itm == null) return;
            try
            {
                player.itm.UpdateTargetInventorySize(); player.itm.UpdateItems(); player.itm.UpdateSelect();
                var core = Singleton<CoreGameManager>.Instance;
                if (core != null && player.plm != null && player.plm.StaminaMax > 0) core.GetHud(0).SetStaminaValue(player.plm.stamina / player.plm.StaminaMax);
            }
            catch (Exception ex) { Log("Restoring the HUD after spectating failed: " + ex.Message); }
        }
    }
}
