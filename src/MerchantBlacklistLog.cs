using Godot;

namespace MerchantBlacklist;

internal static class MerchantBlacklistLog
{
    private const string Tag = "[MerchantBlacklist]";

    internal static void Info(string message) => GD.Print($"{Tag} {message}");
    internal static void Warn(string message) => GD.PushWarning($"{Tag} {message}");
    internal static void Error(string message) => GD.PushError($"{Tag} {message}");
}