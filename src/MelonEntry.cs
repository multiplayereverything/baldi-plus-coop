using MelonLoader;
using BaldiTogether;

[assembly: MelonInfo(typeof(MelonEntry), "Baldi Together", TogetherRuntime.ModVersion, "Local mod project")]
[assembly: MelonGame("Basically Games", "Baldi's Basics Plus")]
[assembly: HarmonyDontPatchAll]

namespace BaldiTogether
{
    public sealed class MelonEntry : MelonMod
    {
        public override void OnInitializeMelon()
        {
            TogetherRuntime.Log = message => LoggerInstance.Msg(message);
            ModHooks.InstallPatches();
            // Unity objects become available at application start, after loader initialization.
        }
        public override void OnLateInitializeMelon() { ModHooks.Install(); }
    }
}
