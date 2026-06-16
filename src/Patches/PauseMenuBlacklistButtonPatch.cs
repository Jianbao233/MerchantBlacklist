using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MerchantBlacklist.UI;

namespace MerchantBlacklist.Patches;

/// <summary>
/// 暂停菜单注入「商店黑名单」按钮：
/// - Postfix NPauseMenu._Ready；从 _buttonContainer 取 Settings 按钮 Duplicate 一份；
/// - 仅复制 Scripts + 用 instantiate 模式，不继承 Settings 自身的 Released 回调；
/// - 文案按 LocManager.Instance.Language 在中文/英文之间切换；
/// - 插入到 Settings 节点正下方（index+1），并修复上下焦点邻居链。
/// </summary>
[HarmonyPatch]
internal static class PauseMenuBlacklistButtonPatch
{
    private const string ButtonName = "MBLBlacklistButton";

    private static MethodBase TargetMethod()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu.NPauseMenu")
                 ?? AccessTools.TypeByName("NPauseMenu");
        return type?.GetMethod("_Ready", AccessTools.all);
    }

    [HarmonyPostfix]
    private static void Postfix(object __instance)
    {
        if (__instance is not Node menuNode) return;
        try
        {
            var menuType = __instance.GetType();
            var settingsField = menuType.GetField("_settingsButton", AccessTools.all);
            var compendiumField = menuType.GetField("_compendiumButton", AccessTools.all);
            var containerField = menuType.GetField("_buttonContainer", AccessTools.all);

            if (settingsField?.GetValue(menuNode) is not Control settingsBtn) return;
            if (containerField?.GetValue(menuNode) is not Node container) return;
            if (container.HasNode(ButtonName)) return;

            var dupFlags = (int)(Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation);
            if (settingsBtn.Duplicate(dupFlags) is not Control clone) return;

            clone.Name = ButtonName;
            container.AddChild(clone);
            container.MoveChild(clone, settingsBtn.GetIndex() + 1);

            ApplyLabel(clone);
            DetachSharedShaderMaterial(clone);
            FixFocusChain(menuNode, settingsBtn, clone, compendiumField);
            BindReleased(clone);

            MerchantBlacklistLog.Info("Pause menu entry button injected.");
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"Pause menu injection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// NPauseMenuButton.OnFocus/OnUnfocus 通过修改 _image.Material（ShaderMaterial 资源）
    /// 的 s/v 参数实现 hover 高亮。Material 是按引用共享的资源，clone 出来的按钮如果
    /// 仍指向原 ShaderMaterial，hover 时两个按钮会同时变亮。这里 Duplicate 一份独立材质。
    /// </summary>
    private static void DetachSharedShaderMaterial(Node clone)
    {
        try
        {
            if (clone.GetNodeOrNull("ButtonImage") is not TextureRect image) return;
            if (image.Material is not ShaderMaterial shared) return;
            if (shared.Duplicate() is ShaderMaterial dup)
            {
                image.Material = dup;
            }
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"Detach shader material failed: {ex.Message}");
        }
    }

    private static void ApplyLabel(Node clone)
    {
        var label = clone.GetNodeOrNull("Label");
        if (label == null) return;

        var text = IsChinese() ? "商店黑名单" : "Shop Blacklist";
        var setAuto = label.GetType().GetMethod("SetTextAutoSize", new[] { typeof(string) });
        if (setAuto != null)
        {
            setAuto.Invoke(label, new object[] { text });
            return;
        }
        label.Set("text", text);
    }

    private static bool IsChinese()
    {
        try
        {
            var locType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Localization.LocManager");
            var instance = locType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var lang = locType?.GetProperty("Language")?.GetValue(instance) as string;
            return !string.IsNullOrEmpty(lang) && lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void FixFocusChain(Node menu, Control settings, Control inserted, FieldInfo compendiumField)
    {
        var compendium = compendiumField?.GetValue(menu) as Control;
        var settingsPath = settings.GetPath();
        var insertedPath = inserted.GetPath();

        inserted.FocusNeighborTop = settingsPath;
        inserted.FocusNeighborLeft = insertedPath;
        inserted.FocusNeighborRight = insertedPath;
        settings.FocusNeighborBottom = insertedPath;

        if (compendium != null)
        {
            inserted.FocusNeighborBottom = compendium.GetPath();
            compendium.FocusNeighborTop = insertedPath;
        }
        else
        {
            inserted.FocusNeighborBottom = insertedPath;
        }
    }

    private static void BindReleased(Control clone)
    {
        var sigName = ResolveReleasedSignalName(clone);
        if (string.IsNullOrEmpty(sigName)) return;

        try
        {
            foreach (var dict in clone.GetSignalConnectionList(sigName))
            {
                if (dict.TryGetValue("callable", out var c) && c.Obj is Callable callable)
                {
                    clone.Disconnect(sigName, callable);
                }
            }
        }
        catch
        {
            // 旧连接清理失败不阻断；多绑一次最多日志噪声。
        }

        var bridge = Callable.From<GodotObject>(_ => BlacklistPanel.Toggle());
        clone.Connect(sigName, bridge);
    }

    private static string ResolveReleasedSignalName(Control clone)
    {
        var t = clone.GetType();
        while (t != null)
        {
            var nested = t.GetNestedType("SignalName");
            var f = nested?.GetField("Released", BindingFlags.Public | BindingFlags.Static);
            if (f != null) return f.GetValue(null)?.ToString();
            t = t.BaseType;
        }
        return "Released";
    }
}