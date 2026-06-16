using Godot;
using HarmonyLib;
using System;
using System.Reflection;
using MerchantBlacklist.Core;
using MerchantBlacklist.UI;

namespace MerchantBlacklist;

public static class MerchantBlacklistMod
{
    public const string ModId = "MerchantBlacklist";
    private const string HarmonyId = "com.jianbao233.merchantblacklist";

    private static bool _initialized;
    private static bool _patched;
    private static BlacklistInputNode _input;

    internal static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            BlacklistStore.LoadFromDisk();
            MerchantBlacklistLog.Info($"Loaded. Banned relics={BlacklistStore.RelicCount}, potions={BlacklistStore.PotionCount}.");
            EnsureInputNode();
            HotkeyService.Initialize();
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Error($"Init failed: {ex}");
        }
    }

    private static void EnsureInputNode()
    {
        if (GodotObject.IsInstanceValid(_input)) return;
        _input = new BlacklistInputNode();
        var tree = Engine.GetMainLoop() as SceneTree;
        tree?.Root?.CallDeferred(Node.MethodName.AddChild, _input);
    }

    internal static void ApplyHarmonyPatches()
    {
        if (_patched) return;

        try
        {
            var harmony = new Harmony(HarmonyId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            _patched = true;
            MerchantBlacklistLog.Info("Harmony patches applied.");
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Error($"Harmony patch failed: {ex}");
        }
    }
}