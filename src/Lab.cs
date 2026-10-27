using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace BaldiTogether
{
    // Explicit test launches keep the game's saves inside the workspace.
    public static class Lab
    {
        static readonly bool enabled = Environment.GetCommandLineArgs().Contains("-bt-lab");
        // Command-line flags never change; Environment.GetCommandLineArgs allocates a new array on every call.
        static readonly HashSet<string> args = new HashSet<string>(Environment.GetCommandLineArgs());
        public static bool HasArg(string flag) { return args.Contains(flag); }
        public static bool Enabled { get { return enabled; } }
        // Lab files (results, logs, saves, captures) go to the mod workspace: <game>/MultiplayerMod, or -bt-workspace
        // when the repo lives elsewhere. Clients read the host's command files from -bt-shared; by default that is the
        // workspace three folders above a lab copy kept in <workspace>/private/melon-lab.
        static string workspace, shared;
        public static string Workspace { get { return workspace ?? (workspace = Path.GetFullPath(ArgValue("-bt-workspace") ?? Path.Combine(TogetherRuntime.Root, "MultiplayerMod"))); } }
        public static string Shared { get { return shared ?? (shared = Path.GetFullPath(ArgValue("-bt-shared") ?? Path.Combine(TogetherRuntime.Root, "../../../MultiplayerMod"))); } }
        internal static string ArgValue(string flag)
        {
            string[] all = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(all, flag);
            return i >= 0 && i + 1 < all.Length ? all[i + 1] : null;
        }
        public static string SavePath()
        {
            string path = Path.Combine(Lab.Workspace, "private/lab-saves");
            Directory.CreateDirectory(path); return path;
        }
        public static void SetString(string key, string value) { }
        public static void SetInt(string key, int value) { }
        public static void SetFloat(string key, float value) { }
        public static void SavePreferences() { }
    }

    [HarmonyPatch]
    static class LabSavePathPatch
    {
        static bool Prepare() { return Lab.Enabled; }
        static IEnumerable<MethodBase> TargetMethods()
        {
            if (!Lab.Enabled) return new MethodBase[0];
            return new[] { typeof(PlayerFileManager), typeof(NameManager), typeof(HighScoreManager), typeof(MainMenu), typeof(PlayerFileManager).Assembly.GetType("Rewired.Data.UserDataStore_File") }
                .Where(t => t != null).SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Where(m => !m.IsAbstract && m.GetMethodBody() != null).Cast<MethodBase>();
        }
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getter = AccessTools.PropertyGetter(typeof(Application), "persistentDataPath");
            MethodInfo replacement = AccessTools.Method(typeof(Lab), "SavePath");
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(getter)) { instruction.opcode = OpCodes.Call; instruction.operand = replacement; }
                MethodInfo method = instruction.operand as MethodInfo;
                if (method != null && method.DeclaringType == typeof(PlayerPrefs))
                {
                    if (method.Name == "SetString" || method.Name == "SetInt" || method.Name == "SetFloat") instruction.operand = AccessTools.Method(typeof(Lab), method.Name);
                    if (method.Name == "Save") instruction.operand = AccessTools.Method(typeof(Lab), "SavePreferences");
                }
                yield return instruction;
            }
        }
    }
}
