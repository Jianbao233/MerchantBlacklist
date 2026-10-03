using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace MerchantBlacklist;

/// <summary>
/// 环境变量守卫的启动自检（**默认完全惰性**：不设 <c>MERCHANTBLACKLIST_SELFTEST=1</c> 时不会执行任何逻辑，
/// 也不会多挂一个逐帧回调）。
///
/// 为什么需要它：面板的 <see cref="UI.ModelCatalog"/> 只在「打开过一次面板」时才建立，所以真机验证
/// 必须有人进游戏按 F10。开着本自检时，游戏启动约 2 秒后会自动建立一次目录并把结果写进日志，
/// 于是「一条命令 + 一次启动」就能拿到判定，不需要人在游戏里操作。
///
/// **只读**：不写设置文件、不改商店库存、不动黑名单，只读 ModelDb 并打日志。
/// </summary>
internal static class SelfTest
{
    private const string EnvVar = "MERCHANTBLACKLIST_SELFTEST";

    /// <summary>启动预热帧数（60fps 下约 2 秒），等游戏把 ModelDb 注册流程走完。</summary>
    private const int WarmupFrames = 120;

    private static int _frames;
    private static bool _done;

    public static bool Enabled { get; } =
        // 必须写全 System.：本文件 using Godot; 会让裸 Environment 与 Godot.Environment 冲突
        string.Equals(System.Environment.GetEnvironmentVariable(EnvVar), "1", StringComparison.Ordinal);

    /// <summary>在 mod 初始化时调用。未启用时立即返回（零开销）。</summary>
    public static void Schedule()
    {
        if (!Enabled || _done) return;
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            MerchantBlacklistLog.Info($"[SelfTest] enabled ({EnvVar}=1); will run after {WarmupFrames} frames.");
            tree.ProcessFrame += OnFrame;
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"[SelfTest] schedule failed: {ex.Message}");
        }
    }

    private static void OnFrame()
    {
        if (_done) return;
        if (++_frames < WarmupFrames) return;
        _done = true;
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree) tree.ProcessFrame -= OnFrame;
        }
        catch { /* 退订失败不影响结果 */ }

        try { Run(); }
        catch (Exception ex) { MerchantBlacklistLog.Error($"[SelfTest] failed: {ex}"); }
    }

    private static void Run()
    {
        UI.ModelCatalog.EnsureBuilt();
        var relics = UI.ModelCatalog.Relics;
        var potions = UI.ModelCatalog.Potions;
        var cards = UI.ModelCatalog.Cards;

        // 与面板打开时那行完全同形（便于 verify-in-game.ps1 用同一个正则解析两种来源）
        MerchantBlacklistLog.Info(
            $"[SelfTest] ModelCatalog ready. relics={relics.Count}, potions={potions.Count}, cards={cards.Count}.");

        // 下面几行就是「面板应该显示什么」的判据
        var colorless = cards.Count(e => e.IsColorless);
        MerchantBlacklistLog.Info($"[SelfTest] cards split. character={cards.Count - colorless}, colorless={colorless}.");
        MerchantBlacklistLog.Info($"[SelfTest] relics by rarity. {Group(relics, e => e.Rarity)}");
        MerchantBlacklistLog.Info($"[SelfTest] potions by rarity. {Group(potions, e => e.Rarity)}");
        MerchantBlacklistLog.Info($"[SelfTest] cards by pool. {Group(cards, e => e.PoolName)}");

        if (cards.Count == 0)
            MerchantBlacklistLog.Warn("[SelfTest] cards=0 —— 卡牌页签会是空的（期望值见 docs/MEMORY.md）");
        if (cards.Count > 0 && colorless == 0)
            MerchantBlacklistLog.Warn("[SelfTest] colorless=0 —— 「无色卡」分组会是空的（无色判定失效）");

        MerchantBlacklistLog.Info("[SelfTest] done. 只读自检，未改动任何设置或商店状态。");
    }

    private static string Group(IReadOnlyList<UI.ModelCatalog.Entry> list, Func<UI.ModelCatalog.Entry, string> key) =>
        string.Join(", ", list.GroupBy(key)
                              .OrderByDescending(g => g.Count())
                              .Select(g => $"{g.Key}={g.Count()}"));
}
