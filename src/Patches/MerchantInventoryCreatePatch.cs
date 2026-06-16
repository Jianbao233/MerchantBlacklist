using HarmonyLib;
using System.Reflection;
using MerchantBlacklist.Core;

namespace MerchantBlacklist.Patches;

/// <summary>
/// 在 MerchantInventory.CreateForNormalMerchant 返回后，对 Relic / Potion 条目过滤一次。
/// 仅作用于本机调用路径；不发任何网络消息。
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
        InventoryFilter.ApplyToInventory(__result);
    }
}