using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace MerchantBlacklist;

/// <summary>
/// 环境守卫的启动自检（**默认完全惰性**：没有触发标记时不做任何事，也不会多挂回调）。
///
/// 触发方式（二选一）：
///   1. 标记文件 <c>%TEMP%\MerchantBlacklist.selftest</c>，内容为模式（为空则按 catalog）
///   2. 环境变量 <c>MERCHANTBLACKLIST_SELFTEST</c>
/// 之所以以**文件**为主：实测从外部启动游戏时，环境变量未必能可靠传到游戏进程里
/// （第一次无头测试就是这么静默失败的：日志里连一行自检都没有）。
///
/// 模式：
///   catalog — 只建立 ModelCatalog，打印"面板应该显示什么"
///   full    — 额外同步构建面板、逐个页签跑一遍，最后退出游戏（无头回归用）
///
/// **驱动必须用 `SceneTreeTimer` 链**：节点 `_Process` 与 `SceneTree.ProcessFrame` 信号
/// 在游戏启动过程中会被暂停/中断，用它们会让自检静默卡死
/// （LoadOrderManager 会话实测踩过，见其 README「无头端到端自检」一节）。
/// 每个阶段都打一行心跳，方便卡住时定位到具体步骤。
/// </summary>
internal static class SelfTest
{
    private const string EnvVar = "MERCHANTBLACKLIST_SELFTEST";
    private const string FlagFileName = "MerchantBlacklist.selftest";
    private const double StepSeconds = 0.5;
    private const int WarmupSteps = 6;          // 约 3 秒，等游戏启动流程把 ModelDb 注册完

    private static int _step;
    private static bool _done;
    private static string _mode = "";
    private static string _trigger = "";

    public static bool Enabled => _mode.Length > 0;
    public static bool Full => _mode == "full";
    public static bool Shots => _mode == "shots";

    public static string FlagPath => Path.Combine(Path.GetTempPath(), FlagFileName);

    static SelfTest()
    {
        try
        {
            var raw = ReadTrigger();
            if (string.IsNullOrWhiteSpace(raw)) return;
            _mode = raw.Trim().ToLowerInvariant() switch
            {
                "full" => "full",
                "shots" or "shot" => "shots",
                _ => "catalog",
            };
        }
        catch
        {
            _mode = "";
        }
    }

    /// <summary>返回触发值；未触发返回 null。</summary>
    private static string ReadTrigger()
    {
        var path = FlagPath;
        if (File.Exists(path))
        {
            _trigger = "flagfile:" + path;
            var text = File.ReadAllText(path).Trim();
            return text.Length > 0 ? text : "catalog";
        }
        var env = System.Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            _trigger = "env:" + EnvVar;
            return env;
        }
        return null;
    }

    public static void Schedule()
    {
        if (!Enabled || _done) return;
        try
        {
            MerchantBlacklistLog.Info($"[SelfTest] mode={_mode} trigger={_trigger}; 用 SceneTreeTimer 链驱动，预热 {WarmupSteps}×{StepSeconds}s。");
            NextStep();
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"[SelfTest] schedule failed: {ex.Message}");
        }
    }

    private static void NextStep()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            MerchantBlacklistLog.Warn("[SelfTest] no SceneTree; abort.");
            return;
        }
        var timer = tree.CreateTimer(StepSeconds);
        timer.Timeout += () =>
        {
            if (_done) return;
            if (++_step < WarmupSteps) { NextStep(); return; }
            _done = true;
            try
            {
                MerchantBlacklistLog.Info($"[SelfTest] warm-up done after {_step} steps; running checks.");
                Run();
            }
            catch (Exception ex)
            {
                MerchantBlacklistLog.Error($"[SelfTest] failed: {ex}");
                // 只有异常时才在这里收尾；正常路径由 Run() 决定何时 Finish()
                // （full 模式还要等分帧构建跑完，提前 Finish 会把游戏过早退掉）
                Finish();
            }
        };
    }

    private static void Run()
    {
        MerchantBlacklistLog.Info("[SelfTest] step: build ModelCatalog…");
        UI.ModelCatalog.EnsureBuilt();
        var relics = UI.ModelCatalog.Relics;
        var potions = UI.ModelCatalog.Potions;
        var cards = UI.ModelCatalog.Cards;

        MerchantBlacklistLog.Info(
            $"[SelfTest] ModelCatalog ready. relics={relics.Count}, potions={potions.Count}, cards={cards.Count}.");

        var colorless = cards.Count(e => e.IsColorless);
        MerchantBlacklistLog.Info($"[SelfTest] cards split. character={cards.Count - colorless}, colorless={colorless}.");
        MerchantBlacklistLog.Info($"[SelfTest] relics by rarity. {Group(relics, e => e.Rarity)}");
        MerchantBlacklistLog.Info($"[SelfTest] potions by rarity. {Group(potions, e => e.Rarity)}");
        MerchantBlacklistLog.Info($"[SelfTest] cards by pool. {Group(cards, e => e.PoolName)}");

        if (cards.Count == 0)
            MerchantBlacklistLog.Warn("[SelfTest] cards=0 —— 卡牌页签会是空的");
        if (cards.Count > 0 && colorless == 0)
            MerchantBlacklistLog.Warn("[SelfTest] colorless=0 —— 「无色卡」分组会是空的（无色判定失效）");

        if (Shots)
        {
            RunShotPass();
            return;
        }

        if (Full)
        {
            MerchantBlacklistLog.Info("[SelfTest] step: panel pass（同步构建）…");
            UI.BlacklistPanel.RunSelfTestPass();
            MerchantBlacklistLog.Info("[SelfTest] step: panel pass returned; 开始分帧构建路径的验证…");
            UI.BlacklistPanel.RunSelfTestAsyncPassBegin();

            // 分帧构建由 Timer 逐帧驱动：再等 8 步（约 4 秒）让它跑完再汇报
            WaitSteps(8, () =>
            {
                UI.BlacklistPanel.RunSelfTestAsyncPassReport();

                // 最后再验一次「用户真实路径」：Toggle() → EnsureCreated → CallDeferred(AddChild)
                UI.BlacklistPanel.RunSelfTestHotkeyPassBegin();
                WaitSteps(3, () =>
                {
                    UI.BlacklistPanel.RunSelfTestHotkeyPassReport();
                    Finish();
                });
            });
            return;
        }

        Finish();
    }

    /// <summary>再等 n 个 timer 步后执行 then（同样走 SceneTreeTimer 链）</summary>
    private static void WaitSteps(int n, Action then)
    {
        if (n <= 0) { then(); return; }
        if (Engine.GetMainLoop() is not SceneTree tree) { then(); return; }
        var timer = tree.CreateTimer(StepSeconds);
        timer.Timeout += () => WaitSteps(n - 1, then);
    }

    // ── 截图模式：逐个"摆姿势 → 等布局稳定 → 记录几何 → 截图" ──────────────
    private static int _pose;
    private static string _shotDir = "";

    private static void RunShotPass()
    {
        _shotDir = Path.Combine(Path.GetTempPath(), "mb-shots");
        MerchantBlacklistLog.Info($"[SelfTest] shots mode: output dir = {_shotDir}");
        // 开场 logo 期间截图只能拍到 MegaCrit 标志（实测踩过），所以等主菜单真的起来
        MerchantBlacklistLog.Info("[SelfTest] shots: 等主菜单就绪（最多 20 秒）…");
        WaitForMainMenu(40, () =>
        {
            MerchantBlacklistLog.Info("[SelfTest] shots: 主菜单就绪（或等待超时），用真实入口显示面板。");
            UI.BlacklistPanel.RunSelfTestShotPassBegin();
            WaitSteps(3, NextPose);      // 留几帧让 CallDeferred(AddChild) 与布局都落定
        });
    }

    /// <summary>等主菜单出现（找名字含 MainMenu 的节点）；超时也继续，避免卡死。</summary>
    private static void WaitForMainMenu(int attemptsLeft, Action then)
    {
        if (attemptsLeft <= 0) { MerchantBlacklistLog.Warn("[SelfTest] shots: 等主菜单超时"); then(); return; }
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (root != null && _FindByName(root, "MainMenu", 8) != null) { then(); return; }
        WaitSteps(1, () => WaitForMainMenu(attemptsLeft - 1, then));
    }

    private static Node _FindByName(Node node, string token, int depth)
    {
        if (node == null || depth < 0) return null;
        if (!string.IsNullOrEmpty(node.Name) &&
            node.Name.ToString().Contains(token, StringComparison.OrdinalIgnoreCase)) return node;
        foreach (var child in node.GetChildren())
        {
            var hit = _FindByName(child, token, depth - 1);
            if (hit != null) return hit;
        }
        return null;
    }

    private static void NextPose()
    {
        if (!UI.BlacklistPanel.ApplySelfTestShotPose(_pose))
        {
            UI.BlacklistPanel.EndSelfTestShotPass();
            Finish();
            return;
        }
        int pose = _pose;
        WaitSteps(2, () =>
        {
            UI.BlacklistPanel.ReportLayout($"pose{pose}");
            UI.BlacklistPanel.CaptureSelfTest(Path.Combine(_shotDir, $"{pose:00}.png"));
            _pose++;
            NextPose();
        });
    }

    private static void Finish()
    {
        if (!Full && !Shots)
        {
            MerchantBlacklistLog.Info("[SelfTest] all done (catalog mode; game keeps running).");
            return;
        }
        // 先给一帧时间让面板的 QueueFree 真的执行掉，再退出；否则 Godot 会在退出时报
        // "resources still in use at exit"（测试脚手架噪声，不是功能问题）。
        MerchantBlacklistLog.Info("[SelfTest] all done; settling before quit…");
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            MerchantBlacklistLog.Warn("[SelfTest] no SceneTree; cannot quit.");
            return;
        }
        var timer = tree.CreateTimer(0.5);
        timer.Timeout += () =>
        {
            MerchantBlacklistLog.Info("[SelfTest] quitting game (headless regression mode).");
            tree.Quit();
        };
    }

    private static string Group(IReadOnlyList<UI.ModelCatalog.Entry> list, Func<UI.ModelCatalog.Entry, string> key) =>
        string.Join(", ", list.GroupBy(key)
                              .OrderByDescending(g => g.Count())
                              .Select(g => $"{g.Key}={g.Count()}"));
}
