using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BaldiTogether
{
    // Challenges and Endless. A challenge win (BaseChallengeGameManager.LoadNextLevel) shows the win screen and quits
    // to the menu after 7 s; clients never ran it, and the host's quit dropped them mid-floor. The host now sends the
    // win and every copy shows its own win screen; a host leaving after that is expected, not a lost connection.
    // Endless never ends on a floor exit: a team wipe shows its results screen (RestartLevel). Its notebooks come back
    // through activity reset timers and InstantReset, and each collection respawns a random item with UnityEngine.Random,
    // so client copies drifted. The host owns both: clients skip their own reset timers and respawns, and mirror the
    // host's by key.
    public partial class TogetherRuntime
    {
        const byte ModeChallengeWon = 0, ModeActivityReset = 1, ModeSupplyRespawn = 2;
        static readonly FieldInfo ActivityResetting = AccessTools.Field(typeof(Activity), "resetting"), EndlessResults = AccessTools.Field(typeof(EndlessGameManager), "resultsScreen");
        static readonly MethodInfo ActivityReset = AccessTools.Method(typeof(Activity), "ResetActivity");

        bool modeFinished; // the host ended the run with a win or results screen; its leaving is expected
        int challengeWinsSent, challengeWinsShown, activityResetsSent, activityResetsMirrored, clientResetTimersBlocked,
            supplyRespawnsSent, supplyRespawnsMirrored, clientRespawnsBlocked, endlessResultsShown, modeEndDisconnects, modeMismatches;
        bool challengeWinSeen, endlessResultsSeen;
        string lastRespawnItem = "";

        internal bool ModeFinished { get { return modeFinished; } }
        static bool ModeMutant { get { return Lab.Enabled && Lab.HasArg("-bt-lab-no-mode-sync"); } } // mutation twin
        // In co-op a collected notebook is "taken" while hidden; Endless shows it again after its activity resets.
        internal static bool Taken(Notebook notebook) { return notebook.collected && notebook.hidden; }

        void ResetModes() { modeFinished = false; modeLabStep = 0; labNotebook = null; labNotebookSeenTaken = false; }
        internal void NoteBlockedResetTimer() { clientResetTimersBlocked++; }
        internal void NoteBlockedRespawn() { clientRespawnsBlocked++; }

        // Host: a challenge was won.
        internal void SendChallengeWin()
        {
            if (!Active || !Hosting || !Running || !worldReady || Applying) return;
            modeFinished = true;
            if (ModeMutant) return;
            challengeWinsSent++;
            SendAll(Wire.Packet(Op.ModeState, w => { w.Write(epoch); w.Write(ModeChallengeWon); }));
            Log("Challenge won; sent the win to the team.");
        }

        // Host: one of its activities reset (an Endless notebook is back).
        internal void SendActivityReset(Activity activity)
        {
            if (!Active || !Hosting || !Running || !worldReady || activity == null || ModeMutant) return;
            string key = Key(activity);
            if (!activities.ContainsKey(key)) return;
            activityResetsSent++;
            SendAll(Wire.Packet(Op.ModeState, w => { w.Write(epoch); w.Write(ModeActivityReset); Wire.Text(w, key); }));
        }

        // Host: the game respawned an item into a hidden pickup. The pickup joins (or rejoins) the shared supplies.
        internal void SendSupplyRespawn(Pickup pickup, ItemObject item)
        {
            if (!Active || !Hosting || !worldReady || pickup == null || item == null || !item.addToInventory || IsShopPickup(pickup)) return;
            string key = Key(pickup); Supply supply;
            if (supplies.TryGetValue(key, out supply))
            {
                if (!claims.Release(key)) { Log("Item respawn skipped: " + key + " is still being delivered."); return; }
                supply.Item = item; supply.Consumed = false;
            }
            else
            {
                if (supplies.Count >= 256) return;
                supplies.Add(key, new Supply { Pickup = pickup, Item = item });
            }
            if (ModeMutant) return;
            supplyRespawnsSent++; lastRespawnItem = item.name;
            Vector3 position = pickup.transform.position;
            SendAll(Wire.Packet(Op.ModeState, w => { w.Write(epoch); w.Write(ModeSupplyRespawn); Wire.Text(w, key); Wire.Text(w, item.name); WriteVector(w, position); }));
        }

        // Both sides, on a team wipe: Endless always shows its results screen (EndlessGameManager.EndGame sets two lives;
        // the co-op wipe calls CoreGameManager.EndGame directly).
        void PrepareModeWipe()
        {
            if (!(Singleton<BaseGameManager>.Instance is EndlessGameManager)) return;
            Singleton<CoreGameManager>.Instance.SetLives(2, true);
            modeFinished = true;
        }

        // Client: Endless's results screen is RestartLevel, which clients otherwise never run.
        internal bool AllowClientModeEnd(BaseGameManager manager, string method)
        {
            if (method != "RestartLevel" || !(manager is EndlessGameManager) || !deathSent || !modeFinished || ModeMutant) return false;
            endlessResultsShown++; Log("Endless results shown after the team wipe.");
            return true;
        }

        // Client: the host left. After a win or results screen that is the end of the run, not a lost connection.
        bool ModeEndedByHost()
        {
            if (!modeFinished) return false;
            modeEndDisconnects++;
            Disconnect("The host finished the run.", false);
            return true;
        }

        bool HandleModeHost(Op op, BinaryReader r)
        {
            if (op != Op.ModeState) return false;
            int generation = r.ReadInt32(); byte kind = r.ReadByte();
            if (kind > ModeSupplyRespawn) throw new IOException("Invalid mode state.");
            string key = kind == ModeChallengeWon ? null : Wire.Text(r, 256);
            string itemName = kind == ModeSupplyRespawn ? Wire.Text(r, 128) : null;
            Vector3 position = kind == ModeSupplyRespawn ? ReadVector(r) : Vector3.zero;
            var manager = Singleton<BaseGameManager>.Instance;
            if (generation != epoch || Hosting || !worldReady || manager == null) return true;
            Applying = true;
            try
            {
                if (kind == ModeChallengeWon)
                {
                    var challenge = manager as BaseChallengeGameManager;
                    if (challenge == null) { modeMismatches++; return true; }
                    modeFinished = true; ReleaseSpectator();
                    challenge.LoadNextLevel(); challengeWinsShown++;
                    Log("Challenge win shown.");
                }
                else if (kind == ModeActivityReset)
                {
                    Activity activity;
                    if (activities.TryGetValue(key, out activity) && activity != null) { ActivityReset.Invoke(activity, null); activityResetsMirrored++; }
                    else modeMismatches++;
                }
                else MirrorSupplyRespawn(key, itemName, position);
            }
            finally { Applying = false; }
            return true;
        }

        void MirrorSupplyRespawn(string key, string itemName, Vector3 position)
        {
            ItemObject item = Resources.FindObjectsOfTypeAll<ItemObject>().FirstOrDefault(i => i.name == itemName);
            if (item == null || !item.addToInventory) throw new IOException("Host item asset is unavailable: " + itemName);
            Supply supply;
            if (!supplies.TryGetValue(key, out supply))
            {
                if (supplies.Count >= 256) throw new IOException("Shared item limit exceeded.");
                var existing = UnityEngine.Object.FindObjectsOfType<Pickup>(true).Where(p => !IsShopPickup(p) && p.free && !p.repeatable).GroupBy(p => Key(p)).ToDictionary(g => g.Key, g => g.First());
                supply = new Supply { Pickup = SupplyPickupAt(key, item, position, existing) };
                supplies.Add(key, supply);
            }
            supply.Item = item; supply.Consumed = false;
            supply.Pickup.free = true; supply.Pickup.AssignItem(item); supply.Pickup.Hide(false);
            supplyRespawnsMirrored++; lastRespawnItem = item.name;
        }

        // Lab (-bt-mode-sync, both sides, started on a challenge or Endless scene with -bt-scene):
        // challenge: the host wins once a peer is ready, and the client must show the win and take the host's leaving
        // as the end of the run. Endless: the host completes and collects a notebook, resets its activity and completes
        // it again; the client collects it the second time; then the host wipes the team for the results screen.
        int modeLabStep; float modeLabNext; Notebook labNotebook; bool labNotebookSeenTaken;
        void UpdateModeLab()
        {
            var manager = Singleton<BaseGameManager>.Instance;
            var challengeManager = manager as BaseChallengeGameManager;
            if (challengeManager != null && challengeManager.winScreen != null && challengeManager.winScreen.gameObject.activeInHierarchy) challengeWinSeen = true;
            var endless = manager as EndlessGameManager;
            if (endless != null && ((Canvas)EndlessResults.GetValue(endless)).gameObject.activeInHierarchy) endlessResultsSeen = true;
            if (!Lab.Enabled || !Lab.HasArg("-bt-mode-sync") || !Active || !Running || !worldReady || manager == null) return;
            float now = Time.realtimeSinceStartup;
            if (modeLabStep == 0)
            {
                if (Hosting && !peers.Values.Any(p => p.Id != self && p.Ready)) return;
                if (!Hosting && !readyAcknowledged) return;
                labNotebook = notebooks.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).FirstOrDefault(n => n != null && n.activity != null && activities.ContainsValue(n.activity));
                Log("LAB modes: " + manager.GetType().Name + ", notebook " + (labNotebook == null ? "none" : Key(labNotebook)));
                modeLabStep = 1; modeLabNext = now + 8;
                return;
            }
            if (now < modeLabNext) return;
            if (challengeManager != null)
            {
                if (Hosting && modeLabStep == 1) { Log("LAB modes: host wins the challenge."); modeLabStep = 2; manager.LoadNextLevel(); }
                return;
            }
            if (endless == null || labNotebook == null) return;
            var activity = labNotebook.activity;
            if (Hosting)
            {
                if (modeLabStep == 1) { activity.Completed(0, true); labNotebook.Clicked(0); Log("LAB modes: host collected " + Key(labNotebook)); modeLabStep = 2; modeLabNext = now + 4; }
                else if (modeLabStep == 2) { activity.InstantReset(); Log("LAB modes: host reset its activity."); modeLabStep = 3; modeLabNext = now + 3; }
                else if (modeLabStep == 3) { activity.Completed(0, true); Log("LAB modes: host completed the activity again."); modeLabStep = 4; modeLabNext = now + 1; }
                else if (modeLabStep == 4 && endless.FoundNotebooks >= 2) { modeLabStep = 5; modeLabNext = now + 3; }
                else if (modeLabStep == 5)
                {
                    modeLabStep = 6;
                    foreach (var peer in peers.Values) if (peer.Accepted) peer.Eliminated = true;
                    Log("LAB modes: host wipes the team; Baldi " + (npcs.OfType<Baldi>().Any() ? "present" : "missing"));
                    CheckTeamWipe(null);
                }
                return;
            }
            if (Taken(labNotebook)) labNotebookSeenTaken = true;
            if (modeLabStep == 1 && labNotebookSeenTaken && !labNotebook.hidden)
            {
                Singleton<CoreGameManager>.Instance.GetPlayer(0).Teleport(labNotebook.transform.position + new Vector3(3, 0, 0));
                modeLabStep = 2; modeLabNext = now + 1;
            }
            else if (modeLabStep == 2) { labNotebook.Clicked(0); Log("LAB modes: client asked for the notebook again."); modeLabStep = 3; }
        }

        string ModeDiagnostic()
        {
            var manager = Singleton<BaseGameManager>.Instance;
            var activity = labNotebook == null ? null : labNotebook.activity;
            return ",\"modeManager\":\"" + (manager == null ? "" : manager.GetType().Name) + "\",\"challenge\":" + (manager is BaseChallengeGameManager ? "true" : "false")
                + ",\"foundNotebooks\":" + (manager == null ? 0 : manager.FoundNotebooks) + ",\"modeFinished\":" + (modeFinished ? "true" : "false")
                + ",\"challengeWinsSent\":" + challengeWinsSent + ",\"challengeWinsShown\":" + challengeWinsShown + ",\"challengeWinSeen\":" + (challengeWinSeen ? "true" : "false")
                + ",\"activityResetsSent\":" + activityResetsSent + ",\"activityResetsMirrored\":" + activityResetsMirrored + ",\"clientResetTimersBlocked\":" + clientResetTimersBlocked
                + ",\"supplyRespawnsSent\":" + supplyRespawnsSent + ",\"supplyRespawnsMirrored\":" + supplyRespawnsMirrored + ",\"clientRespawnsBlocked\":" + clientRespawnsBlocked
                + ",\"lastRespawnItem\":\"" + lastRespawnItem + "\",\"supplyDigest\":\"" + (worldReady ? SupplyDigest() : "") + "\""
                + ",\"endlessResultsShown\":" + endlessResultsShown + ",\"endlessResultsSeen\":" + (endlessResultsSeen ? "true" : "false")
                + ",\"modeEndDisconnects\":" + modeEndDisconnects + ",\"modeMismatches\":" + modeMismatches + ",\"modeLabStep\":" + modeLabStep
                + ",\"labNotebookTaken\":" + (labNotebook != null && Taken(labNotebook) ? "true" : "false") + ",\"labActivityResetting\":" + (activity != null && activity.Resetting ? "true" : "false");
        }
    }

    [HarmonyPatch(typeof(BaseChallengeGameManager), "LoadNextLevel")]
    static class ChallengeWinPatch { static void Postfix() { var r = ModHooks.Current; if (r != null) r.SendChallengeWin(); } }

    [HarmonyPatch(typeof(Activity), "StartResetTimer")]
    static class ClientResetTimerPatch
    {
        static readonly FieldInfo resetting = AccessTools.Field(typeof(Activity), "resetting");
        static bool Prefix(Activity __instance)
        {
            // Not even while applying host state: a mirrored collection runs the client's CollectNotebook, which starts these.
            if (!ModHooks.ClientWorld || (Lab.Enabled && Lab.HasArg("-bt-lab-client-resets"))) return true; // mutation twin
            resetting.SetValue(__instance, true); // the host's reset clears it
            ModHooks.Current.NoteBlockedResetTimer();
            return false;
        }
    }

    [HarmonyPatch(typeof(Activity), "InstantReset")]
    static class ClientInstantResetPatch
    { static bool Prefix() { return !ModHooks.ClientWorld || (Lab.Enabled && Lab.HasArg("-bt-lab-client-resets")); } }

    [HarmonyPatch(typeof(Activity), "ResetActivity")]
    static class ActivityResetPatch { static void Postfix(Activity __instance) { var r = ModHooks.Current; if (r != null) r.SendActivityReset(__instance); } }

    [HarmonyPatch]
    static class ItemRespawnPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            return new MethodBase[] { AccessTools.Method(typeof(EnvironmentController), "RespawnItemInRandomRoom"), AccessTools.Method(typeof(EnvironmentController), "RespawnItemInRoom") };
        }
        static bool Prefix(EnvironmentController __instance, out List<Pickup> __state)
        {
            __state = null;
            if (ModHooks.ClientWorld && !(Lab.Enabled && Lab.HasArg("-bt-lab-client-resets"))) { ModHooks.Current.NoteBlockedRespawn(); return false; }
            var r = ModHooks.Current;
            if (r == null || !r.Active || !r.Hosting) return true;
            __state = __instance.rooms.SelectMany(room => room.pickups).Where(p => p != null && !p.gameObject.activeSelf).ToList();
            return true;
        }
        static void Postfix(ItemObject item, List<Pickup> __state)
        {
            if (__state == null) return;
            var shown = __state.FirstOrDefault(p => p != null && p.gameObject.activeSelf);
            if (shown != null) ModHooks.Current.SendSupplyRespawn(shown, item);
        }
    }
}
