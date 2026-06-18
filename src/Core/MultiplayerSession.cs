using System;
using System.Reflection;

namespace MerchantBlacklist.Core;

/// <summary>
/// 客机/主机/单机会话判定，全部反射，不写死 STS2 类型。
///
/// 适配 STS2 0.107 新架构：
///   MegaCrit.Sts2.Core.Runs.RunManager.Instance.NetService (INetGameService)
///       .Type == MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType
///                { None, Singleplayer, Host, Client, Replay }
///
/// 旧版（&lt;= 0.106）的 NetSyncManager.IsHost 已不存在，本辅助类不再向后兼容。
/// </summary>
internal static class MultiplayerSession
{
    private const string RunManagerTypeName = "MegaCrit.Sts2.Core.Runs.RunManager";
    private const string NetGameTypeTypeName = "MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType";

    public enum Mode
    {
        Unknown,
        Singleplayer,
        Host,
        Client,
        Replay,
    }

    /// <summary>客机时返回 true：这是我们要避免过滤商店库存的唯一情况。</summary>
    public static bool IsClient => Current == Mode.Client;

    /// <summary>当前会话语义角色。任何反射失败都回 Unknown，调用方按"安全侧"处理。</summary>
    public static Mode Current
    {
        get
        {
            try
            {
                var runManager = ResolveType(RunManagerTypeName);
                if (runManager == null) return Mode.Unknown;

                var instance = runManager
                    .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
                if (instance == null) return Mode.Unknown;

                var netService = runManager
                    .GetProperty("NetService", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(instance);
                if (netService == null) return Mode.Unknown;

                var typeProp = netService.GetType().GetProperty("Type");
                var rawType = typeProp?.GetValue(netService);
                if (rawType == null) return Mode.Unknown;

                return rawType.ToString() switch
                {
                    "Singleplayer" => Mode.Singleplayer,
                    "Host"         => Mode.Host,
                    "Client"       => Mode.Client,
                    "Replay"       => Mode.Replay,
                    _              => Mode.Unknown,
                };
            }
            catch (Exception ex)
            {
                MerchantBlacklistLog.Warn($"MultiplayerSession.Current resolution failed: {ex.Message}");
                return Mode.Unknown;
            }
        }
    }

    private static Type ResolveType(string fullName)
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