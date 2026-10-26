using HarmonyLib;

namespace BaldiTogether
{
    [HarmonyPatch(typeof(Pickup), "Clicked")]
    static class ShopClickPatch
    { static bool Prefix(Pickup __instance) { var r = ModHooks.Current; return r == null || r.BeforeShopClicked(__instance); } }

    [HarmonyPatch(typeof(Pickup), "Collect")]
    static class ShopCollectPatch
    { static bool Prefix(Pickup __instance) { var r = ModHooks.Current; return r == null || r.BeforeShopCollect(__instance); } }

    [HarmonyPatch(typeof(StoreRoomFunction), "PurchaseRestock")]
    static class ShopRestockPatch
    { static bool Prefix(StoreRoomFunction __instance) { var r = ModHooks.Current; return r == null || r.BeforeShopRestock(__instance); } }

    [HarmonyPatch(typeof(CoreGameManager), "AddPoints", new[] { typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool) })]
    static class ShopWalletPatch
    { static bool Prefix() { var r = ModHooks.Current; return r == null || r.ShopPointsAllowed(); } }
}
