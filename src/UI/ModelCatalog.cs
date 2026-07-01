using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Godot;

namespace MerchantBlacklist.UI;

/// <summary>
/// 通过反射枚举 ModelDb.AllRelics / AllPotions，预解析 entry id、标题、图标、稀有度。
/// 首次访问时建立缓存，后续 UI 渲染零反射。
/// </summary>
internal static class ModelCatalog
{
    public sealed class Entry
    {
        public string Id;
        public string Title;
        public Texture2D Icon;
        public string Rarity;
        public int RarityOrder;
        public object RawModel;
        public string CardType;
        public string PoolName;
    }

    private static List<Entry> _relics;
    private static List<Entry> _potions;
    private static List<Entry> _cards;
    private static bool _resolved;

    private static Type _modelDbType;
    private static Type _relicModelType;
    private static Type _potionModelType;
    private static Type _cardModelType;
    private static Type _modelIdType;
    private static Type _locStringType;

    public static IReadOnlyList<Entry> Relics
    {
        get { EnsureBuilt(); return _relics ?? (IReadOnlyList<Entry>)Array.Empty<Entry>(); }
    }

    public static IReadOnlyList<Entry> Potions
    {
        get { EnsureBuilt(); return _potions ?? (IReadOnlyList<Entry>)Array.Empty<Entry>(); }
    }

    public static IReadOnlyList<Entry> Cards
    {
        get { EnsureBuilt(); return _cards ?? (IReadOnlyList<Entry>)Array.Empty<Entry>(); }
    }

    public static void EnsureBuilt()
    {
        if (_relics != null && _potions != null && _cards != null) return;
        try { Build(); }
        catch (Exception ex) { MerchantBlacklistLog.Error($"ModelCatalog build failed: {ex.Message}"); }
    }

    public static void Invalidate()
    {
        _relics = null;
        _potions = null;
        _cards = null;
    }

    private static void Build()
    {
        if (!ResolveTypes()) return;

        var allRelicsProp = _modelDbType.GetProperty("AllRelics", BindingFlags.Public | BindingFlags.Static);
        var allPotionsProp = _modelDbType.GetProperty("AllPotions", BindingFlags.Public | BindingFlags.Static);
        var allCardsProp = _modelDbType.GetProperty("AllCards", BindingFlags.Public | BindingFlags.Static);
        if (allRelicsProp == null || allPotionsProp == null)
        {
            MerchantBlacklistLog.Warn("ModelDb.AllRelics/AllPotions not found.");
            return;
        }

        _relics = BuildRelicEntries(allRelicsProp.GetValue(null) as IEnumerable);
        _potions = BuildPotionEntries(allPotionsProp.GetValue(null) as IEnumerable);
        _cards = BuildCardEntries(allCardsProp?.GetValue(null) as IEnumerable);
        _resolved = true;

        MerchantBlacklistLog.Info($"ModelCatalog ready. relics={_relics.Count}, potions={_potions.Count}, cards={_cards.Count}.");
    }

    private static bool ResolveTypes()
    {
        if (_resolved && _modelDbType != null) return true;

        _modelDbType = FindType("MegaCrit.Sts2.Core.Models.ModelDb");
        _relicModelType = FindType("MegaCrit.Sts2.Core.Models.RelicModel");
        _potionModelType = FindType("MegaCrit.Sts2.Core.Models.PotionModel");
        _cardModelType = FindType("MegaCrit.Sts2.Core.Models.CardModel");
        _modelIdType = FindType("MegaCrit.Sts2.Core.Models.ModelId");
        _locStringType = FindType("MegaCrit.Sts2.Core.Localization.LocString");

        return _modelDbType != null && _relicModelType != null && _potionModelType != null && _cardModelType != null && _modelIdType != null;
    }

    private static List<Entry> BuildRelicEntries(IEnumerable models)
    {
        var list = new List<Entry>();
        if (models == null) return list;

        var idProp = _relicModelType.GetProperty("Id");
        var iconProp = _relicModelType.GetProperty("Icon");
        var titleProp = _relicModelType.GetProperty("Title");
        var rarityProp = _relicModelType.GetProperty("Rarity");
        var allowedProp = _relicModelType.GetProperty("IsAllowedInShops");
        var entryProp = _modelIdType.GetProperty("Entry");

        foreach (var model in models)
        {
            if (model == null) continue;
            try
            {
                if (allowedProp != null && allowedProp.GetValue(model) is bool allowed && !allowed)
                {
                    continue;
                }

                var idObj = idProp?.GetValue(model);
                var entryId = entryProp?.GetValue(idObj) as string;
                if (string.IsNullOrEmpty(entryId)) continue;

                var rarity = rarityProp?.GetValue(model);
                var rarityName = rarity?.ToString() ?? string.Empty;

                if (!IsRelicRarityInShopPool(rarityName)) continue;

                list.Add(new Entry
                {
                    Id = entryId,
                    Title = ResolveLocText(titleProp?.GetValue(model)) ?? entryId,
                    Icon = SafeLoadTexture(iconProp, model),
                    Rarity = rarityName,
                    RarityOrder = RarityOrderRelic(rarityName),
                    RawModel = model,
                });
            }
            catch (Exception ex)
            {
                MerchantBlacklistLog.Warn($"Skip relic model: {ex.Message}");
            }
        }

        list.Sort((a, b) =>
        {
            var c = a.RarityOrder.CompareTo(b.RarityOrder);
            return c != 0 ? c : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
        });
        return list;
    }

    private static List<Entry> BuildPotionEntries(IEnumerable models)
    {
        var list = new List<Entry>();
        if (models == null) return list;

        var idProp = _potionModelType.GetProperty("Id");
        var imageProp = _potionModelType.GetProperty("Image");
        var titleProp = _potionModelType.GetProperty("Title");
        var rarityProp = _potionModelType.GetProperty("Rarity");
        var entryProp = _modelIdType.GetProperty("Entry");

        foreach (var model in models)
        {
            if (model == null) continue;
            try
            {
                var idObj = idProp?.GetValue(model);
                var entryId = entryProp?.GetValue(idObj) as string;
                if (string.IsNullOrEmpty(entryId)) continue;

                var rarity = rarityProp?.GetValue(model);
                var rarityName = rarity?.ToString() ?? string.Empty;

                if (!IsPotionRarityInShopPool(rarityName)) continue;

                list.Add(new Entry
                {
                    Id = entryId,
                    Title = ResolveLocText(titleProp?.GetValue(model)) ?? entryId,
                    Icon = SafeLoadTexture(imageProp, model),
                    Rarity = rarityName,
                    RarityOrder = RarityOrderPotion(rarityName),
                    RawModel = model,
                });
            }
            catch (Exception ex)
            {
                MerchantBlacklistLog.Warn($"Skip potion model: {ex.Message}");
            }
        }

        list.Sort((a, b) =>
        {
            var c = a.RarityOrder.CompareTo(b.RarityOrder);
            return c != 0 ? c : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
        });
        return list;
    }

    private static List<Entry> BuildCardEntries(IEnumerable models)
    {
        var list = new List<Entry>();
        if (models == null || _cardModelType == null) return list;

        var idProp = _cardModelType.GetProperty("Id");
        var titleProp = _cardModelType.GetProperty("TitleLocString");
        var rarityProp = _cardModelType.GetProperty("Rarity");
        var typeProp = _cardModelType.GetProperty("Type");
        var poolProp = _cardModelType.GetProperty("Pool");
        var entryProp = _modelIdType.GetProperty("Entry");

        foreach (var model in models)
        {
            if (model == null) continue;
            try
            {
                var idObj = idProp?.GetValue(model);
                var entryId = entryProp?.GetValue(idObj) as string;
                if (string.IsNullOrEmpty(entryId)) continue;

                var pool = poolProp?.GetValue(model);
                if (pool == null) continue;

                var poolType = pool.GetType().Name;

                // 排除非商店池：Curse / Event / Status / Token / Quest / Deprecated
                if (poolType.Contains("Curse") || poolType.Contains("Event") ||
                    poolType.Contains("Status") || poolType.Contains("Token") ||
                    poolType.Contains("Quest") || poolType.Contains("Deprecated"))
                    continue;

                // 角色卡和无色卡都保留；非商店池已上面排除。

                var poolTitleProp = pool.GetType().GetProperty("Title");
                var poolName = poolTitleProp?.GetValue(pool)?.ToString() ?? string.Empty;

                var rarity = rarityProp?.GetValue(model);
                var rarityName = rarity?.ToString() ?? string.Empty;

                var cardTypeVal = typeProp?.GetValue(model);
                var cardTypeName = cardTypeVal?.ToString() ?? "Unknown";

                list.Add(new Entry
                {
                    Id = entryId,
                    Title = ResolveLocText(titleProp?.GetValue(model)) ?? entryId,
                    Icon = null,
                    Rarity = rarityName,
                    RarityOrder = RarityOrderCard(rarityName),
                    RawModel = model,
                    CardType = cardTypeName,
                    PoolName = poolName,
                });
            }
            catch (Exception ex)
            {
                MerchantBlacklistLog.Warn($"Skip card model: {ex.Message}");
            }
        }

        // 排序：角色卡在前，无色卡在后，再按 RarityOrder，再按 Id
        list.Sort((a, b) =>
        {
            var aColorless = a.PoolName != null && a.PoolName.Contains("Colorless");
            var bColorless = b.PoolName != null && b.PoolName.Contains("Colorless");
            var c = aColorless.CompareTo(bColorless);
            if (c != 0) return c;
            c = a.RarityOrder.CompareTo(b.RarityOrder);
            return c != 0 ? c : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
        });
        return list;
    }

    private static Texture2D SafeLoadTexture(PropertyInfo prop, object model)
    {
        if (prop == null || model == null) return null;
        try { return prop.GetValue(model) as Texture2D; }
        catch { return null; }
    }

    private static string ResolveLocText(object locString)
    {
        if (locString == null) return null;
        try
        {
            var t = locString.GetType();
            var m = t.GetMethod("GetFormattedText", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (m != null) return m.Invoke(locString, null) as string;

            var resolveProp = t.GetProperty("Text");
            if (resolveProp != null) return resolveProp.GetValue(locString) as string;
        }
        catch
        {
            // 本地化失败不阻塞 UI；调用方自行回退到 entry id。
        }
        return null;
    }

    private static int RarityOrderRelic(string rarity) => rarity switch
    {
        "Common" => 0,
        "Uncommon" => 1,
        "Rare" => 2,
        "Shop" => 3,
        _ => 9,
    };

    private static int RarityOrderPotion(string rarity) => rarity switch
    {
        "Common" => 0,
        "Uncommon" => 1,
        "Rare" => 2,
        _ => 9,
    };

    private static int RarityOrderCard(string rarity) => rarity switch
    {
        "Common" => 0,
        "Uncommon" => 1,
        "Rare" => 2,
        "Shop" => 3,
        _ => 9,
    };

    /// <summary>
    /// 商店遗物池：参考 MerchantInventory.PopulateRelicEntries / RelicGrabBag._rarities，
    /// 仅 Common/Uncommon/Rare/Shop 四档会出现在商店刷新中。
    /// </summary>
    private static bool IsRelicRarityInShopPool(string rarity)
    {
        return rarity == "Common" || rarity == "Uncommon" || rarity == "Rare" || rarity == "Shop";
    }

    /// <summary>
    /// 商店药水池：PotionFactory.CreateRandomPotionsOutOfCombat 只滚 Common/Uncommon/Rare，
    /// 排除 None/Event/Token 三个非池稀有度。
    /// </summary>
    private static bool IsPotionRarityInShopPool(string rarity)
    {
        return rarity == "Common" || rarity == "Uncommon" || rarity == "Rare";
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