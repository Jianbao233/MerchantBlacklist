using Godot;
using HarmonyLib;
using System;
using System.Reflection;

namespace MerchantBlacklist.Patches;

/// <summary>
/// 启动时机兜底：等 ModManager 装完，跳两帧后初始化日志/JSON/Harmony Patch。
/// </summary>
[HarmonyPatch]
internal static class ModManagerInitPostfix
{
    private static bool _scheduled;

    static ModManagerInitPostfix()
    {
        Schedule();
    }

    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("MegaCrit.Sts2.Core.Modding.ModManager")
                ?? AccessTools.TypeByName("ModManager");
        return t?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
    }

    static void Postfix() => Schedule();

    private static void Schedule()
    {
        if (_scheduled) return;

        try
        {
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree == null) return;
            _scheduled = true;
            tree.ProcessFrame += OnFrame1;
        }
        catch
        {
            // 启动期任何反射异常都不能阻塞游戏。
        }
    }

    private static void OnFrame1()
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null) return;
        tree.ProcessFrame -= OnFrame1;
        tree.ProcessFrame += OnFrame2;
    }

    private static void OnFrame2()
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null) return;
        tree.ProcessFrame -= OnFrame2;

        try
        {
            MerchantBlacklistMod.EnsureInitialized();
            MerchantBlacklistMod.ApplyHarmonyPatches();
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Error($"Init frame failed: {ex}");
        }
    }
}