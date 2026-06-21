using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;

namespace MerchantBlacklist.UI;

/// <summary>
/// 热键服务（双通道，全部反射，运行时检测）：
///
/// 1. ModConfig.ModConfigApi.Register —— 给玩家在 ModConfig 面板里看到/重绑「商店黑名单 切换」热键。
///    存值类型：long = Godot 的 keycode_with_modifiers（同 ModConfig.KeyCaptureNode 输出）。
///    OnChanged 回调：把新 keycode 写回 ToggleBinding，并尝试同步给 ritsulib 的 RuntimeHotkeyService 句柄。
///
/// 2. STS2RitsuLib.RuntimeInput.RuntimeHotkeyService.Register —— 走 ritsulib 的运行时路由器，
///    自带文本输入屏蔽 / 调试控制台屏蔽 / 输入吞咽。
///    Binding 字符串使用 ritsulib 规范化格式（先尝试 "F10"，失败则不动）。
///
/// 都没有时，回退到 BlacklistInputNode 自己的 _Process 轮询（保底永远工作）。
///
/// 数据流：
///   ToggleKeyCode (long) ──── 默认 = Key.F10 的 keycode_with_modifiers
///        │
///        ├─→ 轮询路径：BlacklistInputNode 每帧 Input.IsKeyPressed(Key.F10) → BlacklistPanel.Toggle()
///        │   （兼容老 mod 环境，无 ModConfig / 无 ritsulib 时唯一通道）
///        │
///        ├─→ ModConfig 面板：玩家改键 → ModConfigManager.SetValue → OnChanged(long) → 更新 ToggleKeyCode + Rebind ritsulib
///        │
///        └─→ ritsulib RuntimeHotkeyService：注册一次，获得 IRuntimeHotkeyHandle；
///             改键时调 handle.TryRebind(bindingText)。回调里直接 BlacklistPanel.Toggle()。
/// </summary>
internal static class HotkeyService
{
    public const string ModConfigKey = "toggle_panel_hotkey";
    public const string ModConfigRightClickModifierKey = "right_click_modifier";
    public const string RitsuHotkeyId = "MerchantBlacklist.TogglePanel";
    public const string DefaultToggleBinding = "F10";
    public const string DefaultRightClickModifier = "Shift";

    public static long ToggleKeyCode { get; private set; } = (long)Key.F10;
    internal static string ToggleBindingText => KeyCodeToBinding(ToggleKeyCode) ?? "F10";

    /// <summary>商店内右键 ban 要求按住的修饰键。默认 Shift，避免误 ban。</summary>
    public enum Modifier { None = 0, Shift = 1, Ctrl = 2, Alt = 3 }
    public static Modifier RightClickModifier { get; private set; } = Modifier.Shift;

    /// <summary>
    /// ritsulib RuntimeHotkeyService 已经接管热键路由后置 true。
    /// BlacklistInputNode 看到这个值就停用本地轮询，避免重复 Toggle。
    /// </summary>
    public static bool RouterActive { get; private set; }

    private static bool _initialized;
    private static object _ritsuHandle;
    private static MethodInfo _ritsuTryRebindSingle;

    /// <summary>
    /// Mod 启动时调用一次。先尝试从 ModConfig 读取已保存的值（如果 ModConfig 在场），
    /// 然后注册到 ModConfig（暴露给玩家面板），再注册到 ritsulib（提供热键路由）。
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try { TryRegisterModConfig(); }
        catch (Exception ex) { MerchantBlacklistLog.Warn($"ModConfig hookup failed: {ex.Message}"); }

        try { TryRegisterRitsuLib(); }
        catch (Exception ex) { MerchantBlacklistLog.Warn($"RitsuLib hotkey hookup failed: {ex.Message}"); }
    }

    /// <summary>当前热键对应的 Godot Key（用于 BlacklistInputNode 轮询）。</summary>
    public static Key CurrentKey
    {
        get
        {
            // keycode_with_modifiers = keycode | modifierMask；低位即 keycode 本身。
            const long keycodeMask = 0x00FFFFFF;
            var raw = ToggleKeyCode & keycodeMask;
            if (raw <= 0) return Key.F10;
            return (Key)raw;
        }
    }

    // ── ModConfig 通道 ──────────────────────────────────────────────────

    private static void TryRegisterModConfig()
    {
        var apiType = FindType("ModConfig.ModConfigApi");
        var entryType = FindType("ModConfig.ConfigEntry");
        var typeEnum = FindType("ModConfig.ConfigType");
        if (apiType == null || entryType == null || typeEnum == null)
        {
            MerchantBlacklistLog.Info("ModConfig not detected, skip ModConfig channel.");
            return;
        }

        // 反射构造 ConfigEntry
        var entry = Activator.CreateInstance(entryType);
        SetProp(entry, "Key", ModConfigKey);
        SetProp(entry, "Label", "Toggle Shop Blacklist");
        SetProp(entry, "Labels", new Dictionary<string, string>
        {
            ["en"]  = "Toggle Shop Blacklist",
            ["zhs"] = "切换商店黑名单",
            ["zht"] = "切換商店黑名單",
        });
        SetProp(entry, "Description", "Hotkey to open/close the shop blacklist panel.");
        SetProp(entry, "Descriptions", new Dictionary<string, string>
        {
            ["en"]  = "Hotkey to open/close the shop blacklist panel.",
            ["zhs"] = "用于打开 / 关闭商店黑名单面板的热键。",
            ["zht"] = "用於開啟 / 關閉商店黑名單面板的熱鍵。",
        });
        SetProp(entry, "Type", Enum.Parse(typeEnum, "KeyBind"));
        SetProp(entry, "DefaultValue", (long)Key.F10);

        // OnChanged: Action<object>
        Action<object> onChanged = OnModConfigChanged;
        SetProp(entry, "OnChanged", onChanged);

        // 提前读一次已存在的值，避免覆盖玩家偏好
        try
        {
            var getValue = apiType.GetMethod("GetValue", BindingFlags.Public | BindingFlags.Static);
            if (getValue != null)
            {
                var generic = getValue.MakeGenericMethod(typeof(long));
                var v = generic.Invoke(null, new object[] { MerchantBlacklistMod.ModId, ModConfigKey });
                if (v is long lv && lv > 0) ToggleKeyCode = lv;
            }
        }
        catch { /* 第一次注册时还没值，正常 */ }

        var entries = Array.CreateInstance(entryType, 2);
        entries.SetValue(entry, 0);
        entries.SetValue(BuildRightClickModifierEntry(entryType, typeEnum, apiType), 1);

        var displayNames = new Dictionary<string, string>
        {
            ["en"]  = "Shop Blacklist",
            ["zhs"] = "商店黑名单",
            ["zht"] = "商店黑名單",
        };

        // 优先使用支持本地化 displayNames 的重载
        var registerLocalized = apiType.GetMethod(
            "Register",
            new[] { typeof(string), typeof(string), typeof(Dictionary<string, string>), entries.GetType() });
        if (registerLocalized != null)
        {
            registerLocalized.Invoke(null, new object[] { MerchantBlacklistMod.ModId, "Shop Blacklist", displayNames, entries });
            MerchantBlacklistLog.Info($"Registered hotkey to ModConfig with i18n displayNames (current keycode={ToggleKeyCode}).");
            return;
        }

        var register = apiType.GetMethod("Register", new[] { typeof(string), typeof(string), entries.GetType() });
        if (register == null)
        {
            MerchantBlacklistLog.Warn("ModConfigApi.Register(modId, displayName, entries[]) not found.");
            return;
        }
        register.Invoke(null, new object[] { MerchantBlacklistMod.ModId, "Shop Blacklist", entries });
        MerchantBlacklistLog.Info($"Registered hotkey to ModConfig (current keycode={ToggleKeyCode}).");
    }

    private static void OnModConfigChanged(object value)
    {
        try
        {
            long newCode = Convert.ToInt64(value);
            if (newCode <= 0) newCode = (long)Key.F10;
            ApplyToggleKeyCode(newCode, "ModConfig hotkey changed");
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"OnModConfigChanged failed: {ex.Message}");
        }
    }

    internal static void SetToggleBindingFromRitsuLib(string binding)
    {
        var newCode = BindingToKeyCode(binding);
        ApplyToggleKeyCode(newCode, "RitsuLib hotkey changed");
        TrySetModConfigValue(ModConfigKey, newCode);
    }

    private static void ApplyToggleKeyCode(long newCode, string source)
    {
        if (newCode <= 0) newCode = (long)Key.F10;
        ToggleKeyCode = newCode;
        MerchantBlacklistLog.Info($"{source} → {newCode} (Key={CurrentKey}).");

        if (_ritsuHandle == null || _ritsuTryRebindSingle == null)
            return;

        var bindingText = KeyCodeToBinding(newCode);
        if (string.IsNullOrEmpty(bindingText))
            return;

        var args = new object[] { bindingText, null };
        var ok = _ritsuTryRebindSingle.Invoke(_ritsuHandle, args);
        MerchantBlacklistLog.Info($"RitsuLib rebind → {bindingText} (ok={ok}).");
    }

    /// <summary>
    /// 构造「右键 ban 修饰键」Dropdown ConfigEntry。
    /// 存值：string（"None" / "Shift" / "Ctrl" / "Alt"），默认 "Shift"。
    /// 注册前会从 ModConfigManager 读一次旧值同步进 RightClickModifier。
    /// </summary>
    private static object BuildRightClickModifierEntry(Type entryType, Type typeEnum, Type apiType)
    {
        var modEntry = Activator.CreateInstance(entryType);
        SetProp(modEntry, "Key", ModConfigRightClickModifierKey);
        SetProp(modEntry, "Label", "Shop right-click ban modifier");
        SetProp(modEntry, "Labels", new Dictionary<string, string>
        {
            ["en"]  = "Shop right-click ban modifier",
            ["zhs"] = "商店右键拉黑 修饰键",
            ["zht"] = "商店右鍵拉黑 修飾鍵",
        });
        SetProp(modEntry, "Description", "Hold this modifier with right-click to ban a relic/potion in shop. Set to None to ban with bare right-click.");
        SetProp(modEntry, "Descriptions", new Dictionary<string, string>
        {
            ["en"]  = "Hold this modifier with right-click to ban a relic/potion in shop. Set to None to ban with bare right-click.",
            ["zhs"] = "在商店内按住此修饰键 + 右键即可拉黑遗物/药水。设为 None 则裸右键即拉黑。",
            ["zht"] = "在商店內按住此修飾鍵 + 右鍵即可拉黑遺物/藥水。設為 None 則裸右鍵即拉黑。",
        });
        SetProp(modEntry, "Type", Enum.Parse(typeEnum, "Dropdown"));
        SetProp(modEntry, "DefaultValue", DefaultRightClickModifier);
        SetProp(modEntry, "Options", new[] { "None", "Shift", "Ctrl", "Alt" });

        Action<object> onChanged = OnRightClickModifierChanged;
        SetProp(modEntry, "OnChanged", onChanged);

        try
        {
            var getValue = apiType.GetMethod("GetValue", BindingFlags.Public | BindingFlags.Static);
            if (getValue != null)
            {
                var generic = getValue.MakeGenericMethod(typeof(string));
                var v = generic.Invoke(null, new object[] { MerchantBlacklistMod.ModId, ModConfigRightClickModifierKey }) as string;
                if (!string.IsNullOrEmpty(v)) ApplyRightClickModifierFromString(v);
            }
        }
        catch { }

        return modEntry;
    }

    private static void OnRightClickModifierChanged(object value)
    {
        try
        {
            ApplyRightClickModifierFromString(value?.ToString());
            MerchantBlacklistLog.Info($"ModConfig right-click modifier → {RightClickModifier}.");
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"OnRightClickModifierChanged failed: {ex.Message}");
        }
    }

    internal static void SetRightClickModifierFromRitsuLib(string s)
    {
        ApplyRightClickModifierFromString(s);
        TrySetModConfigValue(ModConfigRightClickModifierKey, RightClickModifier.ToString());
    }

    private static void ApplyRightClickModifierFromString(string s)
    {
        RightClickModifier = (s ?? DefaultRightClickModifier).Trim().ToLowerInvariant() switch
        {
            "none"  => Modifier.None,
            "shift" => Modifier.Shift,
            "ctrl"  => Modifier.Ctrl,
            "alt"   => Modifier.Alt,
            _ => Modifier.Shift,
        };
    }

    // ── RitsuLib 通道 ───────────────────────────────────────────────────

    private static void TryRegisterRitsuLib()
    {
        var serviceType = FindType("STS2RitsuLib.RuntimeInput.RuntimeHotkeyService");
        var optsType = FindType("STS2RitsuLib.RuntimeInput.RuntimeHotkeyOptions");
        if (serviceType == null)
        {
            MerchantBlacklistLog.Info("RitsuLib RuntimeHotkeyService not detected, skip ritsu channel.");
            return;
        }

        // 选择 Register(string, Action, RuntimeHotkeyOptions)
        MethodInfo register = null;
        foreach (var m in serviceType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Register") continue;
            var ps = m.GetParameters();
            if (ps.Length == 3
                && ps[0].ParameterType == typeof(string)
                && ps[1].ParameterType == typeof(Action))
            {
                register = m;
                break;
            }
        }
        if (register == null)
        {
            MerchantBlacklistLog.Warn("RuntimeHotkeyService.Register(string, Action, RuntimeHotkeyOptions) not found.");
            return;
        }

        object opts = null;
        if (optsType != null)
        {
            opts = Activator.CreateInstance(optsType);
            SetProp(opts, "Id", RitsuHotkeyId);
            SetProp(opts, "DisplayName", LocalizedHotkeyName());
            SetProp(opts, "Description", LocalizedHotkeyDesc());
            SetProp(opts, "Category", "MerchantBlacklist");
            SetProp(opts, "MarkInputHandled", true);
            SetProp(opts, "SuppressWhenTextInputFocused", true);
            SetProp(opts, "SuppressWhenDevConsoleVisible", true);
            SetProp(opts, "DebugName", "MerchantBlacklist/Toggle");
        }

        var binding = KeyCodeToBinding(ToggleKeyCode) ?? DefaultToggleBinding;
        Action callback = () => BlacklistPanel.Toggle();
        try
        {
            _ritsuHandle = register.Invoke(null, new object[] { binding, callback, opts });
            if (_ritsuHandle != null)
            {
                _ritsuTryRebindSingle = _ritsuHandle.GetType().GetMethod(
                    "TryRebind",
                    new[] { typeof(string), typeof(string).MakeByRefType() });
                RouterActive = true;
                MerchantBlacklistLog.Info($"Registered hotkey to RitsuLib runtime router (binding={binding}).");
            }
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"RuntimeHotkeyService.Register failed: {ex.Message}");
        }
    }

    // ── 语言探测：跟随 LocManager.Instance.Language（zh* 视为中文）─────────

    private static string CurrentLang()
    {
        try
        {
            var locType = Type.GetType("MegaCrit.Sts2.Core.Localization.LocManager")
                       ?? FindType("MegaCrit.Sts2.Core.Localization.LocManager");
            var instance = locType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var lang = locType?.GetProperty("Language")?.GetValue(instance) as string;
            if (string.IsNullOrEmpty(lang)) return "en";
            lang = lang.ToLowerInvariant();
            if (lang.StartsWith("zh")) return "zhs";
            return "en";
        }
        catch { return "en"; }
    }

    private static string LocalizedHotkeyName()
        => CurrentLang() == "zhs" ? "切换商店黑名单" : "Toggle Shop Blacklist";

    private static string LocalizedHotkeyDesc()
        => CurrentLang() == "zhs" ? "用于打开 / 关闭商店黑名单面板的热键。" : "Open or close the shop blacklist panel.";

    // ── 工具：keycode_with_modifiers → ritsulib binding 文本 ─────────────

    private static string KeyCodeToBinding(long keycodeWithModifiers)
    {
        const long modShift = 0x02000000;
        const long modCtrl  = 0x04000000;
        const long modAlt   = 0x08000000;
        const long modMeta  = 0x10000000;
        const long keycodeMask = 0x00FFFFFF;

        var key = (Key)(keycodeWithModifiers & keycodeMask);
        if (key == 0) return null;

        var prefix = "";
        if ((keycodeWithModifiers & modCtrl)  != 0) prefix += "Ctrl+";
        if ((keycodeWithModifiers & modAlt)   != 0) prefix += "Alt+";
        if ((keycodeWithModifiers & modShift) != 0) prefix += "Shift+";
        if ((keycodeWithModifiers & modMeta)  != 0) prefix += "Meta+";

        // ritsulib 的 TryNormalizeBinding 会把这个字符串规范化；失败时上层 fallback 到 "F10"
        return prefix + OS.GetKeycodeString(key);
    }

    private static long BindingToKeyCode(string binding)
    {
        const long modShift = 0x02000000;
        const long modCtrl  = 0x04000000;
        const long modAlt   = 0x08000000;
        const long modMeta  = 0x10000000;

        if (string.IsNullOrWhiteSpace(binding))
            return (long)Key.F10;

        var parts = binding.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return (long)Key.F10;

        long modifiers = 0;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "shift":
                    modifiers |= modShift;
                    break;
                case "ctrl":
                case "control":
                    modifiers |= modCtrl;
                    break;
                case "alt":
                    modifiers |= modAlt;
                    break;
                case "meta":
                case "cmd":
                case "command":
                case "super":
                    modifiers |= modMeta;
                    break;
            }
        }

        var keyName = parts[^1].Trim();
        if (!Enum.TryParse<Key>(keyName, true, out var key) || key == 0)
            key = Key.F10;

        return modifiers | (long)key;
    }

    private static void SetProp(object target, string name, object value)
    {
        if (target == null) return;
        var p = target.GetType().GetProperty(name);
        if (p != null && p.CanWrite)
        {
            try { p.SetValue(target, value); } catch { }
        }
    }

    private static void TrySetModConfigValue(string key, object value)
    {
        try
        {
            var apiType = FindType("ModConfig.ModConfigApi");
            var setValue = apiType?.GetMethod("SetValue", BindingFlags.Public | BindingFlags.Static);
            setValue?.Invoke(null, new object[] { MerchantBlacklistMod.ModId, key, value });
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"ModConfig sync failed [{key}]: {ex.Message}");
        }
    }

    private static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }
}
