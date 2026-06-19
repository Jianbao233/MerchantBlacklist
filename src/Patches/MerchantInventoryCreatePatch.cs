using HarmonyLib;
using System.Reflection;
using MerchantBlacklist.Core;

namespace MerchantBlacklist.Patches;

/// <summary>
/// 在 MerchantInventory.CreateForNormalMerchant 返回后，对 Relic / Potion 条目过滤一次。
/// 联机安全（已验证）：商店 inventory 在每个 peer 用 Player.PlayerRng.Shops 同种子各自 roll，
/// 购买消息走 RewardObtainedMessage(完整 model) + GoldLostMessage(int)，主机不查 inventory。
/// 客机本地过滤不会让自己点的物品和发给主机的不一致 —— 客机点哪个 entry 就发哪个 model 的购买。
/// 与 RefreshShop 重建路径自动兼容（后者也走 CreateForNormalMerchant）。
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