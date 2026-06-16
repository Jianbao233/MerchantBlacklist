using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MerchantBlacklist.UI;

namespace MerchantBlacklist.Patches;

/// <summary>
/// 在 NTopBar._Ready 后注入「商店黑名单」入口按钮（PauseButton 左侧）。
/// 按钮克隆 PauseButton 的 Icon 子节点（齿轮纹理 + Material）；点击 Toggle BlacklistPanel。
/// 与 NoClientCheats.TopBarHistoryButtonPatch 同套路；不同 Name 不冲突。
/// </summary>
[HarmonyPatch]
internal static class TopBarBlacklistButtonPatch
{
    private const string ButtonName = "MBLBlacklistButton";

    private static MethodBase TargetMethod()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.CommonUi.NTopBar")
                 ?? AccessTools.TypeByName("NTopBar");
        return type?.GetMethod("_Ready", AccessTools.all);
    }

    [HarmonyPostfix]
    private static void Postfix(object __instance)
    {
        if (__instance == null) return;
        try
        {
            var node = __instance as Node;
            if (node == null) return;

            var pauseBtn = node.GetNodeOrNull<Control>("%PauseButton");
            if (pauseBtn == null) return;

            var parent = pauseBtn.GetParent();
            if (parent == null) return;
            if (parent.HasNode(ButtonName)) return;

            var btn = new Button
            {
                Name = ButtonName,
                Flat = true,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = "商店黑名单（F10）",
                CustomMinimumSize = new Vector2(40f, 40f),
            };

            var transparent = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) };
            btn.AddThemeStyleboxOverride("normal", transparent);
            btn.AddThemeStyleboxOverride("hover", transparent);
            btn.AddThemeStyleboxOverride("pressed", transparent);

            var pauseIcon = pauseBtn.GetNodeOrNull<Control>("Control/Icon");
            if (pauseIcon is TextureRect srcRect)
            {
                var icon = new TextureRect
                {
                    Name = "Icon",
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    CustomMinimumSize = new Vector2(24f, 24f),
                    Texture = srcRect.Texture,
                    Material = srcRect.Material != null ? (Material)srcRect.Material.Duplicate() : null,
                    Modulate = new Color(1f, 0.7f, 0.5f, 1f),
                };
                icon.SetAnchorsPreset(Control.LayoutPreset.Center);
                btn.AddChild(icon);
            }

            btn.Pressed += BlacklistPanel.Toggle;

            parent.AddChild(btn, false, Node.InternalMode.Disabled);
            parent.MoveChild(btn, pauseBtn.GetIndex(false));
            MerchantBlacklistLog.Info("Top bar entry button injected.");
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"TopBar injection failed: {ex.Message}");
        }
    }
}