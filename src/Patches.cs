using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BaldiTogether
{
    [HarmonyPatch]
    static class CaptureTitleArtworkPatch
    {
        static MethodBase TargetMethod(){return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(GlobalCam),"Dither"));}
        static void Prefix(object __instance)
        {
            var r=ModHooks.Current;if(r==null)return;
            var type=__instance.GetType();
            if((int)AccessTools.Field(type,"<>1__state").GetValue(__instance)!=1||!(bool)AccessTools.Field(type,"updateToCopy").GetValue(__instance))return;
            r.PrepareTitleTransitionCapture((GlobalCam)AccessTools.Field(type,"<>4__this").GetValue(__instance));
        }
    }

    [HarmonyPatch(typeof(MenuInitializer), "Start")]
    static class PrepareTitleArtworkPatch
    {
        static void Prefix(MenuInitializer __instance)
        {
            var r=ModHooks.Current;if(r==null)return;
            var root=AccessTools.Field(typeof(MenuInitializer),"mainMenu").GetValue(__instance) as UnityEngine.GameObject;
            if(root!=null)r.PrepareTitleArtwork(root.GetComponentInChildren<MainMenu>(true));
        }
    }

    [HarmonyPatch(typeof(MainMenu), "Start")]
    static class EnsureTitleArtworkPatch
    {static void Prefix(MainMenu __instance){var r=ModHooks.Current;if(r!=null)r.PrepareTitleArtwork(__instance);} }

    public static class ModHooks
    {
        static bool installed;
        public static void Install()
        {
            TogetherRuntime.Boot();
            InstallPatches();
            if(Lab.Enabled)TogetherRuntime.Log("Elevator start patch count="+HarmonyLib.Harmony.GetPatchInfo(AccessTools.Method(typeof(ElevatorScreen),"StartGame")).Prefixes.Count);
        }
        public static void InstallPatches()
        {
            if(installed)return;
            installed=true;
            // Patch per class so one missing game method cannot silently skip every later patch.
            var harmony=new HarmonyLib.Harmony("local.balditogether.multiplayer");
            foreach(Type type in AccessTools.GetTypesFromAssembly(typeof(ModHooks).Assembly))
            {
                try{harmony.CreateClassProcessor(type).Patch();}
                catch(Exception ex){TogetherRuntime.Log("Patch "+type.Name+" failed: "+ex.Message);}
            }
        }
        public static TogetherRuntime Current { get { return TogetherRuntime.Instance; } }
        public static bool ClientWorld { get { return Current != null && Current.Active && !Current.Hosting && Current.Running; } }
    }

    [HarmonyPatch(typeof(CoreGameManager), "SetSeed")]
    static class SeedPatch
    { static void Prefix(ref int value) { var r = ModHooks.Current; if (r != null && r.Active) value = r.Seed; } }

    [HarmonyPatch(typeof(CoreGameManager), "SetRandomSeed")]
    static class RandomSeedPatch
    {
        static bool Prefix(CoreGameManager __instance)
        { var r = ModHooks.Current; if (r == null || !r.Active) return true; __instance.SetSeed(r.Seed); return false; }
    }

    [HarmonyPatch(typeof(GameLoader), "Initialize")]
    static class InitializePatch
    {
        static bool Prefix(GameLoader __instance)
        {
            var r = ModHooks.Current;
            if (r == null || !r.Active) return true;
            if (!r.Hosting && !TogetherRuntime.Applying) return false;
            if (r.Hosting) r.PrepareRunSeed();
            __instance.seed = r.Seed; __instance.useSeed = true; return true;
        }
    }

    [HarmonyPatch(typeof(GameLoader), "LoadSavedGame")]
    static class SavedRunPatch
    { static bool Prefix() { var r = ModHooks.Current; return r == null || !r.Active; } }

    [HarmonyPatch(typeof(GameLoader), "LoadLevel")]
    static class StartPatch
    {
        static bool Prefix(SceneObject sceneObject)
        {
            var r = ModHooks.Current;
            if (r == null || !r.Active || TogetherRuntime.Applying) return true;
            if (!r.Hosting) return false;
            r.HostLoad(sceneObject); return true;
        }
    }

    [HarmonyPatch]
    static class TransitionPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            // Derived managers can replace a destination with a pitstop. Observe the final base call.
            return new MethodBase[] { AccessTools.Method(typeof(BaseGameManager), "LoadSceneObject", new[] { typeof(SceneObject), typeof(bool) }) };
        }
        static bool Prefix(SceneObject sceneObject)
        {
            var r = ModHooks.Current;
            if (r == null || !r.Active || TogetherRuntime.Applying) return true;
            if (!r.Hosting) return false;
            r.HostLoad(sceneObject); return true;
        }
    }

    [HarmonyPatch(typeof(CoreGameManager), "Save")]
    static class SavePatch
    { static bool Prefix() { var r = ModHooks.Current; return r == null || !r.Active; } }

    [HarmonyPatch]
    static class ClientTransitionPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(BaseGameManager).Assembly.GetTypes().Where(t => typeof(BaseGameManager).IsAssignableFrom(t))
                .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(m => (m.Name == "RestartLevel" || m.Name == "LoadNextLevel") && m.GetParameters().Length == 0).Cast<MethodBase>();
        }
        static bool Prefix() { return !ModHooks.ClientWorld || TogetherRuntime.Applying; }
    }

    [HarmonyPatch(typeof(BaseGameManager), "BeginPlay")]
    static class BeginPatch
    {
        static bool Prefix() { var r = ModHooks.Current; return r == null || !r.Active || r.CanBeginPlay(); }
        static void Postfix(BaseGameManager __instance) { var r = ModHooks.Current; if (r != null && r.Active && __instance.Ec.Active) r.BeginPlay(); }
    }

    [HarmonyPatch(typeof(Pickup), "Collect")]
    static class SupplyPatch
    {
        static bool Prefix(Pickup __instance) { var r = ModHooks.Current; return r == null || r.BeforePickup(__instance); }
        static void Postfix(Pickup __instance) { var r = ModHooks.Current; if (r != null && r.Active) r.AfterPickup(__instance); }
    }

    [HarmonyPatch(typeof(ItemManager), "UseItem")]
    static class PanelItemPatch
    { static bool Prefix() { var r = ModHooks.Current; return r == null || (!r.PanelOpen&&!r.LocalEliminated); } }

    [HarmonyPatch(typeof(ItemManager),"Update")]
    static class MultiplayerInventoryUpdatePatch
    {static bool Prefix(){var r=ModHooks.Current;return r==null||(!r.PanelOpen&&(!r.Active||!r.Running||(r.WorldReady&&!r.LocalEliminated)));}}

    // Pause and map keys must not fire while typing in the menu or F2 console.
    [HarmonyPatch(typeof(CoreGameManager),"Update")]
    static class PanelCoreInputPatch
    {static bool Prefix(){var r=ModHooks.Current;return r==null||!r.PanelOpen;}}

    [HarmonyPatch(typeof(Notebook), "Clicked")]
    static class NotebookPatch
    {
        static bool Prefix(Notebook __instance)
        { if (__instance.collected) return false; var r = ModHooks.Current; return r == null || r.Request(__instance, 0); }
    }

    [HarmonyPatch(typeof(StandardDoor), "Clicked")]
    static class DoorPatch
    { static bool Prefix(StandardDoor __instance) { var r = ModHooks.Current; return r == null || r.Request(__instance, 1); } }

    [HarmonyPatch(typeof(Activity), "Completed", new[] { typeof(int), typeof(bool) })]
    static class ActivityPatch
    { static bool Prefix(Activity __instance, bool correct) { var r = ModHooks.Current; return r == null || r.Request(__instance, correct ? (byte)2 : (byte)3); } }

    [HarmonyPatch(typeof(NPC), "Update")]
    static class NpcUpdatePatch
    { static bool Prefix() { return !ModHooks.ClientWorld || !ModHooks.Current.WorldReady; } }

    [HarmonyPatch(typeof(NPC), "EntityTriggerEnter")]
    static class NpcEnterPatch
    { static bool Prefix() { return !ModHooks.ClientWorld; } }

    [HarmonyPatch(typeof(NPC), "EntityTriggerStay")]
    static class NpcStayPatch
    { static bool Prefix() { return !ModHooks.ClientWorld; } }

    [HarmonyPatch(typeof(NPC), "EntityTriggerExit")]
    static class NpcExitPatch
    { static bool Prefix() { return !ModHooks.ClientWorld; } }

    [HarmonyPatch(typeof(CoreGameManager), "EndGame")]
    static class DeathPatch
    {
        static bool Prefix(Baldi baldi)
        {
            var r = ModHooks.Current;
            return r==null||r.BeforeMultiplayerDeath(baldi);
        }
    }

    [HarmonyPatch(typeof(Baldi),"CaughtPlayer")]
    static class IndividualBaldiCapturePatch
    {
        static bool Prefix(Baldi __instance)
        {var r=ModHooks.Current;return r==null||r.BeforeMultiplayerDeath(__instance);}
    }

    [HarmonyPatch(typeof(BaseGameManager),"EndGame")]
    static class IndividualManagerCapturePatch
    {
        static bool Prefix(Baldi baldi)
        {var r=ModHooks.Current;return r==null||r.BeforeMultiplayerDeath(baldi);}
    }

    [HarmonyPatch(typeof(CoreGameManager), "Pause")]
    static class PausePatch
    { static bool Prefix() { var r = ModHooks.Current; return r == null || !r.Active || !r.Running; } }

    [HarmonyPatch(typeof(CoreGameManager), "ReturnToMenu")]
    static class MenuExitPatch
    {
        static void Postfix()
        { var r = ModHooks.Current; if (r != null && r.Active && !TogetherRuntime.Applying) r.Disconnect("Run ended. Host or join a new room to play again.", false); }
    }

    [HarmonyPatch(typeof(PlayerMovement), "Update")]
    static class PanelMovementPatch
    { static bool Prefix() { var r = ModHooks.Current; return r == null || !r.PanelOpen; } }

    [HarmonyPatch(typeof(PlayerClick), "Update")]
    static class PanelClickPatch
    { static bool Prefix() { var r = ModHooks.Current; return r == null || !r.PanelOpen; } }
}
