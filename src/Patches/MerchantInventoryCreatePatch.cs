using HarmonyLib;
using System.Reflection;
using MerchantBlacklist.Core;

namespace MerchantBlacklist.Patches;

/// <summary>
/// 在 MerchantInventory.CreateForNormalMerchant 返回后，对 Relic / Potion 条目过滤一次。
/// 仅在 单机 / 主机 模式下生效；客机模式直接 return，不动本地 inventory，
/// 因为联机时主机才是权威库存源，副机本地裁剪会和主机权威值不一致（"看见≠拿到"）。
/// 与 RefreshShop 重建路径自动兼容（后者也会走 CreateForNormalMerchant）。
/// </summary>
[HarmonyPatch]
internal static class MerchantInventoryCreatePatch
{
    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory");
        return t?.GetMethod("CreateForNormalMerchant", BindingFlags.Public | BindingFlags.Static);
    }

    static void Postfix(object __result)
    {
        if (__result == null) return;
        if (MultiplayerSession.IsClient)
        {
            MerchantBlacklistLog.Info("Skipped inventory filter on client peer (host is authoritative).");
            return;
        }
        InventoryFilter.ApplyToInventory(__result);
    }
}