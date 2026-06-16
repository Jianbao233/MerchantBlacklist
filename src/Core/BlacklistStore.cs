using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MerchantBlacklist.Core;

/// <summary>
/// 黑名单持久化存储：JSON 落盘 + 内存 HashSet。
/// 路径：%APPDATA%\SlayTheSpire2\mods_settings\MerchantBlacklist.json
/// </summary>
internal static class BlacklistStore
{
    private const int CurrentSchemaVersion = 1;
    private const int DefaultMaxRerollAttempts = 12;

    private static readonly object _gate = new();
    private static readonly HashSet<string> _relicBlacklist = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _potionBlacklist = new(StringComparer.Ordinal);

    public static int MaxRerollAttempts { get; private set; } = DefaultMaxRerollAttempts;
    public static bool FallbackKeepOriginal { get; private set; } = true;
    public static bool EnableQuickBanInShop { get; private set; } = false;
    public static bool HoverIdDebug { get; private set; } = false;

    public static int RelicCount { get { lock (_gate) return _relicBlacklist.Count; } }
    public static int PotionCount { get { lock (_gate) return _potionBlacklist.Count; } }

    public static bool IsRelicBanned(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        lock (_gate) return _relicBlacklist.Contains(entryId);
    }

    public static bool IsPotionBanned(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        lock (_gate) return _potionBlacklist.Contains(entryId);
    }

    public static IReadOnlyCollection<string> SnapshotRelics()
    {
        lock (_gate) return _relicBlacklist.ToArray();
    }

    public static IReadOnlyCollection<string> SnapshotPotions()
    {
        lock (_gate) return _potionBlacklist.ToArray();
    }

    public static bool ToggleRelic(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        bool nowBanned;
        lock (_gate)
        {
            nowBanned = _relicBlacklist.Add(entryId);
            if (!nowBanned) _relicBlacklist.Remove(entryId);
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
            nowBanned = _potionBlacklist.Add(entryId);
            if (!nowBanned) _potionBlacklist.Remove(entryId);
        }
        SaveToDisk();
        return nowBanned;
    }

    public static bool AddRelic(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        bool added;
        lock (_gate) added = _relicBlacklist.Add(entryId);
        if (added) SaveToDisk();
        return added;
    }

    public static bool AddPotion(string entryId)
    {
        if (string.IsNullOrEmpty(entryId)) return false;
        bool added;
        lock (_gate) added = _potionBlacklist.Add(entryId);
        if (added) SaveToDisk();
        return added;
    }

    public static void SetRelics(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            _relicBlacklist.Clear();
            foreach (var id in ids ?? Enumerable.Empty<string>())
                if (!string.IsNullOrEmpty(id)) _relicBlacklist.Add(id);
        }
        SaveToDisk();
    }

    public static void SetPotions(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            _potionBlacklist.Clear();
            foreach (var id in ids ?? Enumerable.Empty<string>())
                if (!string.IsNullOrEmpty(id)) _potionBlacklist.Add(id);
        }
        SaveToDisk();
    }

    public static void ClearRelics()
    {
        lock (_gate) _relicBlacklist.Clear();
        SaveToDisk();
    }

    public static void ClearPotions()
    {
        lock (_gate) _potionBlacklist.Clear();
        SaveToDisk();
    }

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
            var dto = JsonSerializer.Deserialize<BlacklistFileDto>(json);
            if (dto == null) return;

            lock (_gate)
            {
                _relicBlacklist.Clear();
                _potionBlacklist.Clear();
                if (dto.BlacklistedRelics != null)
                    foreach (var id in dto.BlacklistedRelics)
                        if (!string.IsNullOrEmpty(id)) _relicBlacklist.Add(id);
                if (dto.BlacklistedPotions != null)
                    foreach (var id in dto.BlacklistedPotions)
                        if (!string.IsNullOrEmpty(id)) _potionBlacklist.Add(id);
            }

            if (dto.Settings != null)
            {
                MaxRerollAttempts = Math.Max(1, dto.Settings.MaxRerollAttempts);
                FallbackKeepOriginal = string.Equals(
                    dto.Settings.FallbackWhenPoolDrained, "keep_original",
                    StringComparison.OrdinalIgnoreCase);
                EnableQuickBanInShop = dto.Settings.EnableQuickBanInShop;
                HoverIdDebug = dto.Settings.HoverIdDebug;
            }
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Error($"LoadFromDisk failed: {ex.Message}");
        }
    }

    public static void SaveToDisk()
    {
        try
        {
            var path = GetSettingsPath();
            BlacklistFileDto dto;
            lock (_gate)
            {
                dto = new BlacklistFileDto
                {
                    SchemaVersion = CurrentSchemaVersion,
                    BlacklistedRelics = _relicBlacklist.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    BlacklistedPotions = _potionBlacklist.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    Settings = new BlacklistSettingsDto
                    {
                        MaxRerollAttempts = MaxRerollAttempts,
                        FallbackWhenPoolDrained = FallbackKeepOriginal ? "keep_original" : "leave_empty",
                        EnableQuickBanInShop = EnableQuickBanInShop,
                        HoverIdDebug = HoverIdDebug,
                    },
                };
            }

            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Error($"SaveToDisk failed: {ex.Message}");
        }
    }

    private sealed class BlacklistFileDto
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("blacklisted_relics")]
        public List<string> BlacklistedRelics { get; set; }

        [JsonPropertyName("blacklisted_potions")]
        public List<string> BlacklistedPotions { get; set; }

        [JsonPropertyName("settings")]
        public BlacklistSettingsDto Settings { get; set; }
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