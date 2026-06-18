using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MerchantBlacklist.Core;

namespace MerchantBlacklist.Patches;

/// <summary>
/// 商店内右键 ban：
/// - Postfix NMerchantRelic._Ready / NMerchantPotion._Ready；
/// - 在 ConnectSignals 之后给 _hitbox.MousePressed 再挂一个监听；
/// - 仅识别 Right + Pressed，加入黑名单 → 立即 ApplyToInventory 重抽。
/// 不消耗事件，不影响左键购买流程；纯本机库存过滤，联机安全。
/// </summary>
[HarmonyPatch]
internal static class ShopRightClickBanRelicPatch
{
    private static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantRelic");
        return t?.GetMethod("_Ready", AccessTools.all);
    }

    [HarmonyPostfix]
    private static void Postfix(object __instance)
    {
        if (__instance is not Node node) return;
        ShopRightClickBanShared.AttachHitboxListener(node, isRelic: true);
    }
}

[HarmonyPatch]
internal static class ShopRightClickBanPotionPatch
{
    private static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantPotion");
        return t?.GetMethod("_Ready", AccessTools.all);
    }

    [HarmonyPostfix]
    private static void Postfix(object __instance)
    {
        if (__instance is not Node node) return;
        ShopRightClickBanShared.AttachHitboxListener(node, isRelic: false);
    }
}

internal static class ShopRightClickBanShared
{
    public static void AttachHitboxListener(Node slotNode, bool isRelic)
    {
        try
        {
            var hitbox = ResolveHitbox(slotNode);
            if (hitbox == null) return;

            var sigName = ResolveMousePressedSignalName(hitbox);
            if (string.IsNullOrEmpty(sigName)) return;

            Action<InputEvent> handler = ev => OnMousePressed(slotNode, ev, isRelic);
            var callable = Callable.From(handler);
            hitbox.Connect(sigName, callable);
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"AttachHitboxListener failed: {ex.Message}");
        }
    }

    private static Node ResolveHitbox(Node slotNode)
    {
        try
        {
            var prop = slotNode.GetType().GetProperty("Hitbox", BindingFlags.Public | BindingFlags.Instance);
            if (prop?.GetValue(slotNode) is Node hb) return hb;
        }
        catch { }
        return slotNode.GetNodeOrNull("%Hitbox");
    }

    private static string ResolveMousePressedSignalName(Node hitbox)
    {
        var t = hitbox.GetType();
        while (t != null)
        {
            var nested = t.GetNestedType("SignalName");
            var f = nested?.GetField("MousePressed", BindingFlags.Public | BindingFlags.Static);
            if (f != null) return f.GetValue(null)?.ToString();
            t = t.BaseType;
        }
        return "MousePressed";
    }

    private static void OnMousePressed(Node slotNode, InputEvent ev, bool isRelic)
    {
        if (ev is not InputEventMouseButton mb) return;
        if (!mb.Pressed) return;
        if (mb.ButtonIndex != MouseButton.Right) return;

        // 客机模式：主机才是商店库存权威源；客机本地 ban 会让"看见≠拿到"。
        if (MerchantBlacklist.Core.MultiplayerSession.IsClient)
        {
            MerchantBlacklistLog.Info("Right-click ban suppressed on client peer.");
            return;
        }

        // 修饰键门卫：默认要求 Shift+RMB 才触发，避免误 ban。
        // 由 HotkeyService.RightClickModifier 决定（None/Shift/Ctrl/Alt），ModConfig 可改。
        if (!IsRequiredModifierPressed(mb))
        {
            return;
        }

        try
        {
            var entryId = ResolveEntryId(slotNode, isRelic);
            if (string.IsNullOrEmpty(entryId)) return;

            bool added = isRelic
                ? BlacklistStore.AddRelic(entryId)
                : BlacklistStore.AddPotion(entryId);

            if (!added)
            {
                MerchantBlacklistLog.Info($"Right-click ban skipped (already banned): '{entryId}'");
                return;
            }

            MerchantBlacklistLog.Info($"Right-click banned: '{entryId}'. Re-applying inventory filter.");
            ReapplyHostInventory(slotNode);
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"Right-click ban failed: {ex.Message}");
        }
    }

    private static bool IsRequiredModifierPressed(InputEventMouseButton mb)
    {
        var required = MerchantBlacklist.UI.HotkeyService.RightClickModifier;
        return required switch
        {
            MerchantBlacklist.UI.HotkeyService.Modifier.None  => true,
            MerchantBlacklist.UI.HotkeyService.Modifier.Shift => mb.ShiftPressed,
            MerchantBlacklist.UI.HotkeyService.Modifier.Ctrl  => mb.CtrlPressed,
            MerchantBlacklist.UI.HotkeyService.Modifier.Alt   => mb.AltPressed,
            _ => true,
        };
    }

    private static string ResolveEntryId(Node slotNode, bool isRelic)
    {
        var entryProp = slotNode.GetType().GetProperty("Entry", BindingFlags.Public | BindingFlags.Instance);
        var entry = entryProp?.GetValue(slotNode);
        if (entry == null) return null;

        var modelProp = entry.GetType().GetProperty("Model");
        var model = modelProp?.GetValue(entry);
        if (model == null) return null;

        var idProp = model.GetType().GetProperty("Id");
        var idObj = idProp?.GetValue(model);
        if (idObj == null) return null;

        var entryProp2 = idObj.GetType().GetProperty("Entry");
        return entryProp2?.GetValue(idObj) as string;
    }

    private static void ReapplyHostInventory(Node slotNode)
    {
        try
        {
            var rugField = FindFieldRecursive(slotNode.GetType(), "_merchantRug");
            if (rugField == null) return;
            var rug = rugField.GetValue(slotNode);
            if (rug == null) return;

            var inventoryProp = rug.GetType().GetProperty("Inventory");
            var inventory = inventoryProp?.GetValue(rug);
            if (inventory == null) return;

            InventoryFilter.ApplyToInventory(inventory);
            NotifyEntriesUpdated(inventory);
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"ReapplyHostInventory failed: {ex.Message}");
        }
    }

    private static void NotifyEntriesUpdated(object inventory)
    {
        try
        {
            var t = inventory.GetType();
            var relicEntriesProp = t.GetProperty("RelicEntries");
            var potionEntriesProp = t.GetProperty("PotionEntries");
            BroadcastEntryUpdate(relicEntriesProp?.GetValue(inventory));
            BroadcastEntryUpdate(potionEntriesProp?.GetValue(inventory));
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"NotifyEntriesUpdated failed: {ex.Message}");
        }
    }

    private static void BroadcastEntryUpdate(object entries)
    {
        if (entries is not System.Collections.IEnumerable list) return;
        foreach (var entry in list)
        {
            if (entry == null) continue;
            try
            {
                var m = entry.GetType().GetMethod("OnMerchantInventoryUpdated", BindingFlags.Public | BindingFlags.Instance);
                m?.Invoke(entry, null);
            }
            catch
            {
                // 单个 entry 触发失败不影响其他。
            }
        }
    }

    private static FieldInfo FindFieldRecursive(Type t, string name)
    {
        while (t != null)
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (f != null) return f;
            t = t.BaseType;
        }
        return null;
    }
}