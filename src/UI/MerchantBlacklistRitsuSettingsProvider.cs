using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace MerchantBlacklist.UI;

internal static class MerchantBlacklistRitsuSettingsProvider
{
    private static readonly SettingPageDefinition[] Pages =
    {
        new(
            MerchantBlacklistMod.ModId,
            Texts.SettingsPageTitle,
            Texts.SettingsPageDescription,
            new[]
            {
                new SettingSectionDefinition(
                    "controls",
                    Texts.ControlsSectionTitle,
                    new[]
                    {
                        new SettingEntryDefinition(
                            HotkeyService.ModConfigKey,
                            "key-binding",
                            HotkeyService.ModConfigKey,
                            Texts.ToggleHotkeyLabel,
                            Texts.ToggleHotkeyDescription,
                            DefaultValue: HotkeyService.DefaultToggleBinding,
                            AllowModifierCombos: true,
                            AllowModifierOnly: false),
                        new SettingEntryDefinition(
                            HotkeyService.ModConfigRightClickModifierKey,
                            "choice",
                            HotkeyService.ModConfigRightClickModifierKey,
                            Texts.RightClickModifierLabel,
                            Texts.RightClickModifierDescription,
                            DefaultValue: HotkeyService.DefaultRightClickModifier,
                            Presentation: "dropdown",
                            Options: new[] { "None", "Shift", "Ctrl", "Alt" })
                    })
            })
    };

    public static object CreateRitsuLibSettingsSchema()
    {
        return new
        {
            modId = MerchantBlacklistMod.ModId,
            modDisplayName = Texts.ModDisplayName.ToSchema(),
            pages = Pages.Select(page => page.ToSchema()).ToArray()
        };
    }

    public static object GetRitsuLibSettingValue(string key)
    {
        return GetRitsuLibSettingString(key);
    }

    public static void SetRitsuLibSettingValue(string key, object value)
    {
        SetRitsuLibSettingString(key, value?.ToString() ?? "");
    }

    public static string GetRitsuLibSettingString(string key)
    {
        return key switch
        {
            HotkeyService.ModConfigKey => HotkeyService.ToggleBindingText,
            HotkeyService.ModConfigRightClickModifierKey => HotkeyService.RightClickModifier.ToString(),
            _ => ""
        };
    }

    public static void SetRitsuLibSettingString(string key, string value)
    {
        switch (key)
        {
            case HotkeyService.ModConfigKey:
                HotkeyService.SetToggleBindingFromRitsuLib(value);
                break;
            case HotkeyService.ModConfigRightClickModifierKey:
                HotkeyService.SetRightClickModifierFromRitsuLib(value);
                break;
        }
    }

    public static void SaveRitsuLibSettings()
    {
    }

    private static class Texts
    {
        public static readonly LocalizedText ModDisplayName = new("Shop Blacklist", "商店黑名单");
        public static readonly LocalizedText SettingsPageTitle = new("Settings", "设置");
        public static readonly LocalizedText SettingsPageDescription = new(
            "Configure shop blacklist controls.",
            "配置商店黑名单控制项。");
        public static readonly LocalizedText ControlsSectionTitle = new("Controls", "控制");
        public static readonly LocalizedText ToggleHotkeyLabel = new(
            "Toggle Shop Blacklist",
            "切换商店黑名单",
            "切換商店黑名單");

        public static readonly LocalizedText ToggleHotkeyDescription = new(
            "Hotkey to open/close the shop blacklist panel.",
            "用于打开 / 关闭商店黑名单面板的热键。",
            "用於開啟 / 關閉商店黑名單面板的熱鍵。");

        public static readonly LocalizedText RightClickModifierLabel = new(
            "Shop right-click ban modifier",
            "商店右键拉黑 修饰键",
            "商店右鍵拉黑 修飾鍵");

        public static readonly LocalizedText RightClickModifierDescription = new(
            "Hold this modifier with right-click to ban a relic/potion in shop. Set to None to ban with bare right-click.",
            "在商店内按住此修饰键 + 右键即可拉黑遗物/药水。设为 None 则裸右键即拉黑。",
            "在商店內按住此修飾鍵 + 右鍵即可拉黑遺物/藥水。設為 None 則裸右鍵即拉黑。");
    }

    private sealed record LocalizedText(string En, string Zhs, string Zht = null)
    {
        public object ToSchema()
        {
            var langMap = new Hashtable
            {
                ["en"] = En,
                ["zhs"] = Zhs,
                ["zh"] = Zhs
            };
            if (!string.IsNullOrWhiteSpace(Zht))
                langMap["zht"] = Zht;

            return new Hashtable
            {
                ["langMap"] = langMap,
                ["fallback"] = En
            };
        }
    }

    private sealed record SettingPageDefinition(
        string PageId,
        LocalizedText Title,
        LocalizedText Description,
        IReadOnlyList<SettingSectionDefinition> Sections)
    {
        public object ToSchema()
        {
            return new
            {
                pageId = PageId,
                title = Title.ToSchema(),
                description = Description.ToSchema(),
                sections = Sections.Select(section => section.ToSchema()).ToArray()
            };
        }
    }

    private sealed record SettingSectionDefinition(
        string Id,
        LocalizedText Title,
        IReadOnlyList<SettingEntryDefinition> Entries)
    {
        public object ToSchema()
        {
            return new
            {
                id = Id,
                title = Title.ToSchema(),
                entries = Entries.Select(entry => entry.ToSchema()).ToArray()
            };
        }
    }

    private sealed record SettingEntryDefinition(
        string Id,
        string Type,
        string Key,
        LocalizedText Label,
        LocalizedText Description,
        object DefaultValue = null,
        string Presentation = null,
        IReadOnlyList<string> Options = null,
        bool? AllowModifierCombos = null,
        bool? AllowModifierOnly = null)
    {
        public object ToSchema()
        {
            return new
            {
                id = Id,
                type = Type,
                key = Key,
                label = Label.ToSchema(),
                description = Description.ToSchema(),
                defaultValue = DefaultValue,
                presentation = Presentation,
                options = Options?.ToArray(),
                allowModifierCombos = AllowModifierCombos,
                allowModifierOnly = AllowModifierOnly
            };
        }
    }
}
