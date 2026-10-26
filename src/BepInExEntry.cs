using BepInEx;

namespace BaldiTogether
{
    [BepInPlugin("local.balditogether.multiplayer", "Baldi Together", TogetherRuntime.ModVersion)]
    public sealed class BepInExEntry : BaseUnityPlugin
    {
        void Awake()
        {
            TogetherRuntime.Log = message => Logger.LogInfo(message);
            ModHooks.Install();
        }
    }
}
