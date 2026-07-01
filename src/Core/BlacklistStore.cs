using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MerchantBlacklist.Core;

/// <summary>
/// 黑名单持久化存储（v0.3.0 三层结构（遗物 + 药水 + 卡牌））：全局 + 5 角色两层扁平列表。
/// 路径：%APPDATA%\SlayTheSpire2\mods_settings\MerchantBlacklist.json
///
/// IsRelicBanned(id) = GlobalRelics.Contains(id) ∪ CurrentCharacterRelics.Contains(id)
/// 面板编辑时操作的是 ActiveCategory 层（"GLOBAL" 或角色 ID）。
/// 过滤时自动取全局 ∪ 当前角色（CharacterDetector 检测）。
/// </summary>
internal static class BlacklistStore
{
    private const int CurrentSchemaVersion = 4;
    private const int DefaultMaxRerollAttempts = 12;
    private const string GlobalCategory = "GLOBAL";

    private static readonly object _gate = new();

    // 全局层
    private static readonly HashSet<string> _globalRelics = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _globalPotions = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _globalCards = new(StringComparer.Ordinal);

    // 角色层：key = "IRONCLAD" 等
    private static readonly Dictionary<string, HashSet<string>> _perCharRelics = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<string>> _perCharPotions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<string>> _perCharCards = new(StringComparer.Ordinal);

    /// <summary>面板当前编辑的分类："GLOBAL" 或角色 ID。</summary>
    public static string ActiveCategory { get; set; } = GlobalCategory;

    public static int MaxRerollAttempts { get; private set; } = DefaultMaxRerollAttempts;
    public static bool FallbackKeepOriginal { get; private set; } = true;
    public static bool EnableQuickBanInShop { get; private set; } = false;
    public static bool HoverIdDebug { get; private set; } = false;

    static BlacklistStore()
    {
        foreach (var charId in CharacterDetector.KnownCharacters)
        {
            _perCharRelics[charId] = new HashSet<string>(StringComparer.Ordinal);
            _perCharPotions[charId] = new HashSet<string>(StringComparer.Ordinal);
            _perCharCards[charId] = new HashSet<string>(StringComparer.Ordinal);
        }
    }

    // ── 过滤查询（全局 ∪ 当前角色）──────────────────────────────────

    public static bool IsRelicBanned(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        lock (_gate)
        {
            if (_globalRelics.Contains(entryId)) return true;
            var charId = CharacterDetector.GetCurrentCharacterId();
            if (charId != null && _perCharRelics.TryGetValue(charId, out var set) && set.Contains(entryId))
                return true;
            return false;
        }
    }

    public static bool IsPotionBanned(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        lock (_gate)
        {
            if (_globalPotions.Contains(entryId)) return true;
            var charId = CharacterDetector.GetCurrentCharacterId();
            if (charId != null && _perCharPotions.TryGetValue(charId, out var set) && set.Contains(entryId))
                return true;
            return false;
        }
    }

    public static bool IsCardBanned(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        lock (_gate)
        {
            if (_globalCards.Contains(entryId)) return true;
            var charId = CharacterDetector.GetCurrentCharacterId();
            if (charId != null && _perCharCards.TryGetValue(charId, out var set) && set.Contains(entryId))
                return true;
            return false;
        }
    }

    // ── 面板编辑（操作 ActiveCategory 层）────────────────────────────

    /// <summary>
    /// 当前 ActiveCategory 层是否 ban 了该遗物。
    /// 当 ActiveCategory 是角色层时，也显示全局已 ban 的（全局 ∪ 当前角色层）。
    /// </summary>
    public static bool IsRelicBannedInActive(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        lock (_gate)
        {
            if (GetActiveRelicSet().Contains(entryId)) return true;
            // 角色层视图下，全局 ban 的也显示为 banned
            if (ActiveCategory != "GLOBAL" && _globalRelics.Contains(entryId)) return true;
            return false;
        }
    }

    public static bool IsPotionBannedInActive(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        lock (_gate)
        {
            if (GetActivePotionSet().Contains(entryId)) return true;
            if (ActiveCategory != "GLOBAL" && _globalPotions.Contains(entryId)) return true;
            return false;
        }
    }

    public static bool IsCardBannedInActive(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        lock (_gate)
        {
            if (GetActiveCardSet().Contains(entryId)) return true;
            if (ActiveCategory != "GLOBAL" && _globalCards.Contains(entryId)) return true;
            return false;
        }
    }

    public static bool ToggleRelic(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        bool nowBanned;
        lock (_gate)
        {
            var set = GetActiveRelicSet();
            nowBanned = set.Add(entryId);
            if (!nowBanned) set.Remove(entryId);
        }
        SaveToDisk();
        return nowBanned;
    }

    public static bool TogglePotion(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        bool nowBanned;
        lock (_gate)
        {
            var set = GetActivePotionSet();
            nowBanned = set.Add(entryId);
            if (!nowBanned) set.Remove(entryId);
        }
        SaveToDisk();
        return nowBanned;
    }

    public static bool ToggleCard(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        bool nowBanned;
        lock (_gate)
        {
            var set = GetActiveCardSet();
            nowBanned = set.Add(entryId);
            if (!nowBanned) set.Remove(entryId);
        }
        SaveToDisk();
        return nowBanned;
    }

    public static bool AddRelic(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        bool added;
        lock (_gate) added = GetActiveRelicSet().Add(entryId);
        if (added) SaveToDisk();
        return added;
    }

    public static bool AddPotion(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        bool added;
        lock (_gate) added = GetActivePotionSet().Add(entryId);
        if (added) SaveToDisk();
        return added;
    }

    public static bool AddCard(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        bool added;
        lock (_gate) added = GetActiveCardSet().Add(entryId);
        if (added) SaveToDisk();
        return added;
    }

    public static void SetRelics(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            var set = GetActiveRelicSet();
            set.Clear();
            foreach (var id in ids ?? Enumerable.Empty<string>())
                if (!string.IsNullOrEmpty(id)) set.Add(id);
        }
        SaveToDisk();
    }

    public static void SetPotions(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            var set = GetActivePotionSet();
            set.Clear();
            foreach (var id in ids ?? Enumerable.Empty<string>())
                if (!string.IsNullOrEmpty(id)) set.Add(id);
        }
        SaveToDisk();
    }

    public static void SetCards(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            var set = GetActiveCardSet();
            set.Clear();
            foreach (var id in ids ?? Enumerable.Empty<string>())
                if (!string.IsNullOrEmpty(id)) set.Add(id);
        }
        SaveToDisk();
    }

    public static void ClearRelics()
    {
        lock (_gate) GetActiveRelicSet().Clear();
        SaveToDisk();
    }

    public static void ClearPotions()
    {
        lock (_gate) GetActivePotionSet().Clear();
        SaveToDisk();
    }

    public static void ClearCards()
    {
        lock (_gate) GetActiveCardSet().Clear();
        SaveToDisk();
    }

    // ── 快照 / 计数（ActiveCategory 层）──────────────────────────────

    public static IReadOnlyCollection<string> SnapshotRelics()
    {
        lock (_gate) return GetActiveRelicSet().ToArray();
    }

    public static IReadOnlyCollection<string> SnapshotPotions()
    {
        lock (_gate) return GetActivePotionSet().ToArray();
    }

    public static IReadOnlyCollection<string> SnapshotCards()
    {
        lock (_gate) return GetActiveCardSet().ToArray();
    }

    public static int RelicCount
    {
        get { lock (_gate) return GetActiveRelicSet().Count; }
    }

    public static int PotionCount
    {
        get { lock (_gate) return GetActivePotionSet().Count; }
    }

    public static int CardCount
    {
        get { lock (_gate) return GetActiveCardSet().Count; }
    }

    /// <summary>全局层遗物 ban 数。</summary>
    public static int GlobalRelicCount
    {
        get { lock (_gate) return _globalRelics.Count; }
    }

    public static int GlobalPotionCount
    {
        get { lock (_gate) return _globalPotions.Count; }
    }

    public static int GlobalCardCount
    {
        get { lock (_gate) return _globalCards.Count; }
    }

    /// <summary>指定角色层遗物 ban 数。</summary>
    public static int CharacterRelicCount(string charId)
    {
        lock (_gate) return _perCharRelics.TryGetValue(charId, out var s) ? s.Count : 0;
    }

    public static int CharacterPotionCount(string charId)
    {
        lock (_gate) return _perCharPotions.TryGetValue(charId, out var s) ? s.Count : 0;
    }

    public static int CharacterCardCount(string charId)
    {
        lock (_gate) return _perCharCards.TryGetValue(charId, out var s) ? s.Count : 0;
    }

    // ── 内部工具 ─────────────────────────────────────────────────────

    private static HashSet<string> GetActiveRelicSet()
    {
        if (ActiveCategory == GlobalCategory || ActiveCategory == null)
            return _globalRelics;
        if (_perCharRelics.TryGetValue(ActiveCategory, out var set))
            return set;
        // 未知角色 ID → 回退到全局
        return _globalRelics;
    }

    private static HashSet<string> GetActivePotionSet()
    {
        if (ActiveCategory == GlobalCategory || ActiveCategory == null)
            return _globalPotions;
        if (_perCharPotions.TryGetValue(ActiveCategory, out var set))
            return set;
        return _globalPotions;
    }

    private static HashSet<string> GetActiveCardSet()
    {
        if (ActiveCategory == GlobalCategory || ActiveCategory == null)
            return _globalCards;
        if (_perCharCards.TryGetValue(ActiveCategory, out var set))
            return set;
        return _globalCards;
    }

    // ── 持久化 ───────────────────────────────────────────────────────

    private static string GetSettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "SlayTheSpire2", "mods_settings");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "MerchantBlacklist.json");
    }

    public static void LoadFromDisk()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
            {
                MerchantBlacklistLog.Info($"Settings not found, using defaults. Path={path}");
                return;
            }

            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            int schemaVersion = 1;
            if (root.TryGetProperty("schema_version", out var svEl))
                schemaVersion = svEl.GetInt32();

            lock (_gate)
            {
                _globalRelics.Clear();
                _globalPotions.Clear();
                _globalCards.Clear();
                foreach (var charId in CharacterDetector.KnownCharacters)
                {
                    _perCharRelics[charId].Clear();
                    _perCharPotions[charId].Clear();
                    _perCharCards[charId].Clear();
                }

                if (schemaVersion >= 3)
                {
                    LoadV3(root);
                }
                else
                {
                    // v1/v2 迁移：扁平列表 → global
                    MigrateV1ToV3(root);
                    MerchantBlacklistLog.Info($"Migrated schema v{schemaVersion} → v3 (all data → global).");
                }
            }

            // settings（v1/v3 通用）
            if (root.TryGetProperty("settings", out var settingsEl))
            {
                MaxRerollAttempts = settingsEl.TryGetProperty("max_reroll_attempts", out var mra) ? Math.Max(1, mra.GetInt32()) : DefaultMaxRerollAttempts;
                FallbackKeepOriginal = settingsEl.TryGetProperty("fallback_when_pool_drained", out var fb) && fb.GetString() == "keep_original";
                EnableQuickBanInShop = settingsEl.TryGetProperty("enable_quick_ban_in_shop", out var qb) && qb.GetBoolean();
                HoverIdDebug = settingsEl.TryGetProperty("hover_id_debug", out var hd) && hd.GetBoolean();
            }
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Error($"LoadFromDisk failed: {ex.Message}");
        }
    }

    private static void LoadV3(JsonElement root)
    {
        if (root.TryGetProperty("global", out var globalEl))
        {
            if (globalEl.TryGetProperty("relics", out var relicsEl))
                foreach (var id in relicsEl.EnumerateArray())
                    if (!string.IsNullOrEmpty(id.GetString())) _globalRelics.Add(id.GetString());
            if (globalEl.TryGetProperty("potions", out var potionsEl))
                foreach (var id in potionsEl.EnumerateArray())
                    if (!string.IsNullOrEmpty(id.GetString())) _globalPotions.Add(id.GetString());
            if (globalEl.TryGetProperty("cards", out var cardsEl))
                foreach (var id in cardsEl.EnumerateArray())
                    if (!string.IsNullOrEmpty(id.GetString())) _globalCards.Add(id.GetString());
        }

        if (root.TryGetProperty("perCharacter", out var perCharEl))
        {
            foreach (var charId in CharacterDetector.KnownCharacters)
            {
                if (perCharEl.TryGetProperty(charId, out var charEl))
                {
                    if (charEl.TryGetProperty("relics", out var relicsEl))
                        foreach (var id in relicsEl.EnumerateArray())
                            if (!string.IsNullOrEmpty(id.GetString())) _perCharRelics[charId].Add(id.GetString());
                    if (charEl.TryGetProperty("potions", out var potionsEl))
                        foreach (var id in potionsEl.EnumerateArray())
                            if (!string.IsNullOrEmpty(id.GetString())) _perCharPotions[charId].Add(id.GetString());
                    if (charEl.TryGetProperty("cards", out var cardsEl))
                        foreach (var id in cardsEl.EnumerateArray())
                            if (!string.IsNullOrEmpty(id.GetString())) _perCharCards[charId].Add(id.GetString());
                }
            }
        }
    }

    private static void MigrateV1ToV3(JsonElement root)
    {
        if (root.TryGetProperty("blacklisted_relics", out var relicsEl))
            foreach (var id in relicsEl.EnumerateArray())
                if (!string.IsNullOrEmpty(id.GetString())) _globalRelics.Add(id.GetString());

        if (root.TryGetProperty("blacklisted_potions", out var potionsEl))
            foreach (var id in potionsEl.EnumerateArray())
                if (!string.IsNullOrEmpty(id.GetString())) _globalPotions.Add(id.GetString());
    }

    public static void SaveToDisk()
    {
        try
        {
            var path = GetSettingsPath();
            lock (_gate)
            {
                var dto = new BlacklistFileDto
                {
                    SchemaVersion = CurrentSchemaVersion,
                    Global = new CategoryDto
                    {
                        Relics = _globalRelics.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                        Potions = _globalPotions.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                        Cards = _globalCards.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    },
                    PerCharacter = new Dictionary<string, CategoryDto>(StringComparer.Ordinal),
                    Settings = new BlacklistSettingsDto
                    {
                        MaxRerollAttempts = MaxRerollAttempts,
                        FallbackWhenPoolDrained = FallbackKeepOriginal ? "keep_original" : "leave_empty",
                        EnableQuickBanInShop = EnableQuickBanInShop,
                        HoverIdDebug = HoverIdDebug,
                    },
                };

                foreach (var charId in CharacterDetector.KnownCharacters)
                {
                    dto.PerCharacter[charId] = new CategoryDto
                    {
                        Relics = _perCharRelics[charId].OrderBy(x => x, StringComparer.Ordinal).ToList(),
                        Potions = _perCharPotions[charId].OrderBy(x => x, StringComparer.Ordinal).ToList(),
                        Cards = _perCharCards[charId].OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    };
                }

                var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Error($"SaveToDisk failed: {ex.Message}");
        }
    }

    // ── DTO ──────────────────────────────────────────────────────────

    private sealed class BlacklistFileDto
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("global")]
        public CategoryDto Global { get; set; }

        [JsonPropertyName("perCharacter")]
        public Dictionary<string, CategoryDto> PerCharacter { get; set; }

        [JsonPropertyName("settings")]
        public BlacklistSettingsDto Settings { get; set; }
    }

    private sealed class CategoryDto
    {
        [JsonPropertyName("relics")]
        public List<string> Relics { get; set; }

        [JsonPropertyName("potions")]
        public List<string> Potions { get; set; }

        [JsonPropertyName("cards")]
        public List<string> Cards { get; set; }
    }

    private sealed class BlacklistSettingsDto
    {
        [JsonPropertyName("max_reroll_attempts")]
        public int MaxRerollAttempts { get; set; } = DefaultMaxRerollAttempts;

        [JsonPropertyName("fallback_when_pool_drained")]
        public string FallbackWhenPoolDrained { get; set; } = "keep_original";

        [JsonPropertyName("enable_quick_ban_in_shop")]
        public bool EnableQuickBanInShop { get; set; } = false;

        [JsonPropertyName("hover_id_debug")]
        public bool HoverIdDebug { get; set; } = false;
    }
}