using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MerchantBlacklist.Core;

/// <summary>
/// 商店生成 Postfix 过滤层。
/// 对 MerchantInventory.RelicEntries / PotionEntries 中命中黑名单的条目，
/// 使用 grab bag 重抽到非黑名单条目；池子打空时按 settings 退化为 keep_original。
/// </summary>
internal static class InventoryFilter
{
    private static Type _merchantInventoryType;
    private static Type _merchantRelicEntryType;
    private static Type _merchantPotionEntryType;
    private static Type _relicFactoryType;
    private static Type _potionFactoryType;
    private static Type _relicModelType;
    private static Type _potionModelType;
    private static Type _modelIdType;
    private static Type _modelDbType;

    private static FieldInfo _relicEntriesField;
    private static FieldInfo _potionEntriesField;
    private static PropertyInfo _relicEntryModelProp;
    private static PropertyInfo _potionEntryModelProp;
    private static PropertyInfo _modelIdProp;
    private static PropertyInfo _modelIdEntryProp;
    private static PropertyInfo _relicRarityProp;
    private static PropertyInfo _isAllowedInShopsProp;
    private static PropertyInfo _playerProp;
    private static PropertyInfo _playerRngProp;
    private static PropertyInfo _shopsRngProp;
    private static MethodInfo _pullNextRelicFromBackFiltered;
    private static MethodInfo _setRelicModelMethod;
    private static MethodInfo _toMutableRelicMethod;

    private static MethodInfo _createRandomPotionMethod;
    private static MethodInfo _toMutablePotionMethod;
    private static MethodInfo _calcCostMethod;
    private static MethodInfo _markPotionSeenMethod;

    private static Type _merchantCardEntryType;
    private static Type _cardModelType;
    private static Type _cardCreationResultType;
    private static PropertyInfo _characterCardEntriesProp;
    private static PropertyInfo _colorlessCardEntriesProp;
    private static PropertyInfo _cardEntryCreationResultProp;
    private static PropertyInfo _creationResultCardProp;
    private static PropertyInfo _cardModelIdProp;
    private static MethodInfo _cardPopulateMethod;

    private static bool _initialized;
    private static bool _initFailed;

    private static bool EnsureReflectionReady()
    {
        if (_initialized) return true;
        if (_initFailed) return false;

        try
        {
            _merchantInventoryType = FindType("MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory");
            _merchantRelicEntryType = FindType("MegaCrit.Sts2.Core.Entities.Merchant.MerchantRelicEntry");
            _merchantPotionEntryType = FindType("MegaCrit.Sts2.Core.Entities.Merchant.MerchantPotionEntry");
            _relicFactoryType = FindType("MegaCrit.Sts2.Core.Factories.RelicFactory");
            _potionFactoryType = FindType("MegaCrit.Sts2.Core.Factories.PotionFactory");
            _relicModelType = FindType("MegaCrit.Sts2.Core.Models.RelicModel");
            _potionModelType = FindType("MegaCrit.Sts2.Core.Models.PotionModel");
            _modelIdType = FindType("MegaCrit.Sts2.Core.Models.ModelId");
            _modelDbType = FindType("MegaCrit.Sts2.Core.Models.ModelDb");

            if (_merchantInventoryType == null || _merchantRelicEntryType == null || _merchantPotionEntryType == null
                || _relicFactoryType == null || _potionFactoryType == null
                || _relicModelType == null || _potionModelType == null || _modelIdType == null
                || _modelDbType == null)
            {
                _initFailed = true;
                return false;
            }

            _relicEntriesField = _merchantInventoryType.GetField("_relicEntries", BindingFlags.NonPublic | BindingFlags.Instance);
            _potionEntriesField = _merchantInventoryType.GetField("_potionEntries", BindingFlags.NonPublic | BindingFlags.Instance);
            _relicEntryModelProp = _merchantRelicEntryType.GetProperty("Model");
            _potionEntryModelProp = _merchantPotionEntryType.GetProperty("Model");
            _modelIdProp = FindAbstractProp(_relicModelType, "Id") ?? _relicModelType.GetProperty("Id");
            _modelIdEntryProp = _modelIdType.GetProperty("Entry");
            _relicRarityProp = _relicModelType.GetProperty("Rarity");
            _isAllowedInShopsProp = _relicModelType.GetProperty("IsAllowedInShops");
            _playerProp = _merchantInventoryType.GetProperty("Player");

            _setRelicModelMethod = _merchantRelicEntryType.GetMethod("SetModel", BindingFlags.NonPublic | BindingFlags.Instance);
            _toMutableRelicMethod = _relicModelType.GetMethod("ToMutable", BindingFlags.Public | BindingFlags.Instance);
            _toMutablePotionMethod = _potionModelType.GetMethod("ToMutable", BindingFlags.Public | BindingFlags.Instance);
            _calcCostMethod = _merchantPotionEntryType.GetMethod("CalcCost", BindingFlags.Public | BindingFlags.Instance);

            foreach (var m in _relicFactoryType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "PullNextRelicFromBack") continue;
                var p = m.GetParameters();
                if (p.Length == 3 && p[1].ParameterType.Name == "RelicRarity")
                {
                    _pullNextRelicFromBackFiltered = m;
                    break;
                }
            }

            foreach (var m in _potionFactoryType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "CreateRandomPotionOutOfCombat") continue;
                var p = m.GetParameters();
                if (p.Length == 3)
                {
                    _createRandomPotionMethod = m;
                    break;
                }
            }

            var playerType = _playerProp?.PropertyType;
            _playerRngProp = playerType?.GetProperty("PlayerRng");
            if (_playerRngProp != null)
            {
                _shopsRngProp = _playerRngProp.PropertyType.GetProperty("Shops");
            }

            var saveManagerType = FindType("MegaCrit.Sts2.Core.Saves.SaveManager");
            if (saveManagerType != null)
            {
                _markPotionSeenMethod = saveManagerType.GetMethod("MarkPotionAsSeen", BindingFlags.Public | BindingFlags.Instance);
            }

            _merchantCardEntryType = FindType("MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardEntry");
            _cardModelType = FindType("MegaCrit.Sts2.Core.Models.CardModel");
            _cardCreationResultType = FindType("MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult");
            _characterCardEntriesProp = _merchantInventoryType.GetProperty("CharacterCardEntries");
            _colorlessCardEntriesProp = _merchantInventoryType.GetProperty("ColorlessCardEntries");
            _cardEntryCreationResultProp = _merchantCardEntryType?.GetProperty("CreationResult");
            _creationResultCardProp = _cardCreationResultType?.GetProperty("Card");
            _cardModelIdProp = FindAbstractProp(_cardModelType, "Id") ?? _cardModelType?.GetProperty("Id");
            _cardPopulateMethod = _merchantCardEntryType?.GetMethod("Populate", BindingFlags.Public | BindingFlags.Instance);

            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Error($"InventoryFilter init failed: {ex.Message}");
            _initFailed = true;
            return false;
        }
    }

    public static void ApplyToInventory(object inventory)
    {
        if (inventory == null) return;
        if (!EnsureReflectionReady()) return;

        try
        {
            FilterRelics(inventory);
            FilterPotions(inventory);
            FilterCards(inventory);
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Error($"ApplyToInventory failed: {ex.Message}");
        }
    }

    private static void FilterRelics(object inventory)
    {
        var relics = _relicEntriesField?.GetValue(inventory) as IList;
        if (relics == null || relics.Count == 0) return;

        var player = _playerProp?.GetValue(inventory);
        if (player == null) return;

        var alreadyChosen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in relics)
        {
            var id = GetEntryIdFromRelic(entry);
            if (!string.IsNullOrEmpty(id)) alreadyChosen.Add(id);
        }

        for (int i = 0; i < relics.Count; i++)
        {
            var entry = relics[i];
            var origId = GetEntryIdFromRelic(entry);
            if (string.IsNullOrEmpty(origId)) continue;
            if (!BlacklistStore.IsRelicBanned(origId)) continue;

            var origModel = _relicEntryModelProp.GetValue(entry);
            var rarity = _relicRarityProp.GetValue(origModel);

            var newModel = TryRerollRelic(player, rarity, alreadyChosen, origId);
            if (newModel == null)
            {
                MerchantBlacklistLog.Info($"Relic '{origId}' kept (no replacement available, keep_original).");
                continue;
            }

            var newId = GetEntryIdFromModel(newModel);
            alreadyChosen.Remove(origId);
            alreadyChosen.Add(newId);

            var mutable = _toMutableRelicMethod?.Invoke(newModel, null) ?? newModel;
            _setRelicModelMethod?.Invoke(entry, new[] { mutable });
            MerchantBlacklistLog.Info($"Relic banned '{origId}' -> '{newId}'.");
        }
    }

    private static object TryRerollRelic(object player, object rarity, HashSet<string> alreadyChosen, string origId)
    {
        // 不走 RelicFactory.PullFromBack（会永久消耗 grab bag，导致遗物池耗尽出现"头环"）。
        // 改为从 ModelDb.AllRelics 中查找同稀有度、非 ban、非已选、允许在商店出现的遗物，
        // 取 ToMutable() 实例，不碰 grab bag。
        var allRelicsProp = _modelDbType?.GetProperty("AllRelics", BindingFlags.Public | BindingFlags.Static);
        var allRelics = allRelicsProp?.GetValue(null) as System.Collections.IEnumerable;
        if (allRelics == null) return null;

        // 收集候选列表
        var candidates = new System.Collections.Generic.List<object>();
        foreach (var relic in allRelics)
        {
            if (relic == null) continue;
            var entryId = GetEntryIdFromModel(relic);
            if (string.IsNullOrEmpty(entryId)) continue;
            if (entryId == origId) continue;
            if (BlacklistStore.IsRelicBanned(entryId)) continue;
            if (alreadyChosen.Contains(entryId)) continue;

            // 检查稀有度匹配
            var relicRarity = _relicRarityProp?.GetValue(relic);
            if (relicRarity == null || !relicRarity.Equals(rarity)) continue;

            // 检查允许在商店出现
            var allowed = (bool)(_isAllowedInShopsProp?.GetValue(relic) ?? false);
            if (!allowed) continue;

            candidates.Add(relic);
        }

        if (candidates.Count == 0) return null;

        // 随机选一个（用 player 的 Shops RNG 保持确定性）
        var playerRng = _playerRngProp?.GetValue(player);
        var shopsRng = _shopsRngProp?.GetValue(playerRng);
        int index;
        if (shopsRng != null)
        {
            var nextIntMethod = shopsRng.GetType().GetMethod("NextInt", new[] { typeof(int) });
            index = (int)(nextIntMethod?.Invoke(shopsRng, new object[] { candidates.Count }) ?? 0);
        }
        else
        {
            index = new System.Random().Next(candidates.Count);
        }

        return candidates[index % candidates.Count];
    }

    private static void FilterPotions(object inventory)
    {
        var potions = _potionEntriesField?.GetValue(inventory) as IList;
        if (potions == null || potions.Count == 0) return;

        var player = _playerProp?.GetValue(inventory);
        if (player == null) return;

        var rng = _playerRngProp != null ? _playerRngProp.GetValue(player) : null;
        var shopsRng = rng != null ? _shopsRngProp?.GetValue(rng) : null;

        var alreadyChosen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in potions)
        {
            var id = GetEntryIdFromPotion(entry);
            if (!string.IsNullOrEmpty(id)) alreadyChosen.Add(id);
        }

        for (int i = 0; i < potions.Count; i++)
        {
            var entry = potions[i];
            var origId = GetEntryIdFromPotion(entry);
            if (string.IsNullOrEmpty(origId)) continue;
            if (!BlacklistStore.IsPotionBanned(origId)) continue;

            var newModel = TryRerollPotion(player, shopsRng, alreadyChosen, origId);
            if (newModel == null)
            {
                MerchantBlacklistLog.Info($"Potion '{origId}' kept (no replacement available, keep_original).");
                continue;
            }

            var newId = GetEntryIdFromModel(newModel);
            alreadyChosen.Remove(origId);
            alreadyChosen.Add(newId);

            var mutable = _toMutablePotionMethod?.Invoke(newModel, null) ?? newModel;
            _potionEntryModelProp.SetValue(entry, mutable);
            _calcCostMethod?.Invoke(entry, null);

            try
            {
                var saveManagerType = FindType("MegaCrit.Sts2.Core.Saves.SaveManager");
                var instance = saveManagerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (instance != null && _markPotionSeenMethod != null)
                {
                    _markPotionSeenMethod.Invoke(instance, new[] { mutable });
                }
            }
            catch
            {
                // 标记 seen 失败不影响主流程。
            }

            MerchantBlacklistLog.Info($"Potion banned '{origId}' -> '{newId}'.");
        }
    }

    private static object TryRerollPotion(object player, object shopsRng, HashSet<string> alreadyChosen, string origId)
    {
        if (_createRandomPotionMethod == null || shopsRng == null) return null;

        var blacklistList = BuildPotionBlacklist(alreadyChosen, origId);

        for (int attempt = 0; attempt < BlacklistStore.MaxRerollAttempts; attempt++)
        {
            var result = _createRandomPotionMethod.Invoke(null, new[] { player, shopsRng, blacklistList });
            if (result == null) continue;

            var resultId = GetEntryIdFromModel(result);
            if (string.IsNullOrEmpty(resultId)) continue;
            if (BlacklistStore.IsPotionBanned(resultId))
            {
                blacklistList = AppendToPotionBlacklist(blacklistList, result);
                continue;
            }
            if (resultId != origId && alreadyChosen.Contains(resultId))
            {
                blacklistList = AppendToPotionBlacklist(blacklistList, result);
                continue;
            }
            return result;
        }
        return null;
    }

    private static IList BuildPotionBlacklist(HashSet<string> alreadyChosen, string origId)
    {
        var listType = typeof(List<>).MakeGenericType(_potionModelType);
        var list = (IList)Activator.CreateInstance(listType);
        // 重抽时主动排除：已被选中的非自身 id 全部拒绝（通过 ID 在 wrapper 里判断），
        // 黑名单本身的 PotionModel 实例需要传给原生工厂，所以这里返回空列表，
        // 由上层多次重试 + filter 判断完成黑名单收敛。
        return list;
    }

    private static IList AppendToPotionBlacklist(IList list, object potionModel)
    {
        if (list == null || potionModel == null) return list;
        list.Add(potionModel);
        return list;
    }

    private static string GetEntryIdFromRelic(object entry)
    {
        if (entry == null) return null;
        var model = _relicEntryModelProp?.GetValue(entry);
        return GetEntryIdFromModel(model);
    }

    private static string GetEntryIdFromPotion(object entry)
    {
        if (entry == null) return null;
        var model = _potionEntryModelProp?.GetValue(entry);
        return GetEntryIdFromModel(model);
    }

    private static void FilterCards(object inventory)
    {
        if (_merchantCardEntryType == null || _cardPopulateMethod == null) return;

        // 合并 CharacterCardEntries + ColorlessCardEntries
        var charCards = _characterCardEntriesProp?.GetValue(inventory) as IList;
        var colorlessCards = _colorlessCardEntriesProp?.GetValue(inventory) as IList;

        FilterCardList(charCards);
        FilterCardList(colorlessCards);
    }

    private static void FilterCardList(IList entries)
    {
        if (entries == null || entries.Count == 0) return;

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var cardId = GetEntryIdFromCard(entry);
            if (string.IsNullOrEmpty(cardId)) continue;
            if (!BlacklistStore.IsCardBanned(cardId)) continue;

            // 被 ban，重抽
            bool rerolled = false;
            for (int attempt = 0; attempt < BlacklistStore.MaxRerollAttempts; attempt++)
            {
                _cardPopulateMethod?.Invoke(entry, null);
                var newId = GetEntryIdFromCard(entry);
                if (string.IsNullOrEmpty(newId)) break;
                if (!BlacklistStore.IsCardBanned(newId))
                {
                    MerchantBlacklistLog.Info($"Card banned '{cardId}' -> '{newId}'.");
                    rerolled = true;
                    break;
                }
            }
            if (!rerolled)
            {
                MerchantBlacklistLog.Info($"Card '{cardId}' kept (no replacement available, keep_original).");
            }
        }
    }

    private static string GetEntryIdFromCard(object entry)
    {
        if (entry == null) return null;
        var creationResult = _cardEntryCreationResultProp?.GetValue(entry);
        if (creationResult == null) return null;
        var card = _creationResultCardProp?.GetValue(creationResult);
        if (card == null) return null;
        return GetEntryIdFromModel(card);
    }

    private static string GetEntryIdFromModel(object model)
    {
        if (model == null) return null;
        var id = _modelIdProp?.GetValue(model);
        if (id == null)
        {
            var idDirect = model.GetType().GetProperty("Id")?.GetValue(model);
            if (idDirect == null) return null;
            return _modelIdEntryProp?.GetValue(idDirect) as string;
        }
        return _modelIdEntryProp?.GetValue(id) as string;
    }

    private static PropertyInfo FindAbstractProp(Type type, string name)
    {
        var t = type;
        while (t != null)
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (p != null) return p;
            t = t.BaseType;
        }
        return null;
    }

    private static object BuildTypedPredicate(Func<object, bool> generic, Type modelType, Type delegateType)
    {
        var helperMethod = typeof(InventoryFilter)
            .GetMethod(nameof(BuildTypedPredicateGeneric), BindingFlags.NonPublic | BindingFlags.Static)
            .MakeGenericMethod(modelType);
        return helperMethod.Invoke(null, new object[] { generic, delegateType });
    }

    private static Delegate BuildTypedPredicateGeneric<TModel>(Func<object, bool> generic, Type delegateType)
    {
        Func<TModel, bool> typed = m => generic((object)m);
        return Delegate.CreateDelegate(delegateType, typed.Target, typed.Method);
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