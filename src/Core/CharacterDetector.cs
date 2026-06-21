using System;
using System.Reflection;

namespace MerchantBlacklist.Core;

/// <summary>
/// 反射获取当前游戏内角色 ID（如 "IRONCLAD" / "SILENT" 等）。
/// 非游戏内（主菜单等）返回 null。
/// </summary>
internal static class CharacterDetector
{
    public static readonly string[] KnownCharacters =
        { "IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT" };

    private static Type _runManagerType;
    private static Type _localContextType;
    private static Type _playerType;
    private static Type _characterModelType;
    private static Type _modelIdType;
    private static PropertyInfo _instanceProp;
    private static MethodInfo _getRunStateMethod;
    private static PropertyInfo _playersProp;
    private static MethodInfo _getMeMethod;
    private static PropertyInfo _characterProp;
    private static PropertyInfo _idProp;
    private static PropertyInfo _entryProp;

    private static bool _init;
    private static bool _initFailed;

    private static void EnsureInit()
    {
        if (_init || _initFailed) return;
        _init = true;

        try
        {
            _runManagerType = FindType("MegaCrit.Sts2.Core.Runs.RunManager");
            _localContextType = FindType("MegaCrit.Sts2.Core.Context.LocalContext");
            _playerType = FindType("MegaCrit.Sts2.Core.Entities.Players.Player");
            _characterModelType = FindType("MegaCrit.Sts2.Core.Models.CharacterModel");
            _modelIdType = FindType("MegaCrit.Sts2.Core.Models.ModelId");

            if (_runManagerType == null || _localContextType == null || _playerType == null
                || _characterModelType == null || _modelIdType == null)
            {
                _initFailed = true;
                return;
            }

            _instanceProp = _runManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);

            // RunManager.State 是 private 字段，用 DebugOnlyGetState() 方法获取
            _getRunStateMethod = _runManagerType.GetMethod("DebugOnlyGetState", BindingFlags.Public | BindingFlags.Instance);

            // IRunState.Players（通过接口获取）
            var iRunStateType = FindType("MegaCrit.Sts2.Core.Runs.IRunState");
            _playersProp = iRunStateType?.GetProperty("Players");

            // LocalContext.GetMe(IEnumerable<Player>)
            foreach (var m in _localContextType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "GetMe") continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(_playersProp?.PropertyType ?? typeof(object)))
                {
                    _getMeMethod = m;
                    break;
                }
            }

            _characterProp = _playerType.GetProperty("Character");
            _idProp = FindAbstractProp(_characterModelType, "Id") ?? _characterModelType.GetProperty("Id");
            _entryProp = _modelIdType.GetProperty("Entry");
        }
        catch
        {
            _initFailed = true;
        }
    }

    /// <summary>
    /// 获取当前角色 ID（如 "IRONCLAD"）。非游戏内返回 null。
    /// </summary>
    public static string GetCurrentCharacterId()
    {
        EnsureInit();
        if (_initFailed) return null;

        try
        {
            var runManager = _instanceProp?.GetValue(null);
            if (runManager == null) return null;

            var runState = _getRunStateMethod?.Invoke(runManager, null);
            if (runState == null) return null;

            var players = _playersProp?.GetValue(runState);
            if (players == null)
            {
                var directPlayersProp = runState.GetType().GetProperty("Players");
                players = directPlayersProp?.GetValue(runState);
            }
            if (players == null) return null;

            object me = null;
            if (_getMeMethod != null)
            {
                me = _getMeMethod.Invoke(null, new[] { players });
            }
            else
            {
                foreach (var m in _localContextType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "GetMe") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1)
                    {
                        try
                        {
                            me = m.Invoke(null, new[] { players });
                            if (me != null) break;
                        }
                        catch { }
                    }
                }
            }
            if (me == null) return null;

            var character = _characterProp?.GetValue(me);
            if (character == null) return null;

            var id = _idProp?.GetValue(character);
            if (id == null) return null;

            var entry = _entryProp?.GetValue(id) as string;
            return entry;
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"CharacterDetector exception: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取角色中文名（用于面板显示）。
    /// </summary>
    public static string GetCharacterDisplayName(string charId)
    {
        return charId switch
        {
            "IRONCLAD" => "铁甲战士",
            "SILENT" => "静默猎手",
            "DEFECT" => "故障机器人",
            "NECROBINDER" => "亡灵契约师",
            "REGENT" => "储君",
            _ => charId,
        };
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