using System;
using System.Collections;
using System.Reflection;
using Godot;

namespace MerchantBlacklist.UI;

/// <summary>
/// 反射封装原生节点工厂与 hover tip 系统。
/// - NRelic.Create(RelicModel, NRelic.IconSize)
/// - NPotion.Create(PotionModel)
/// - NHoverTipSet.CreateAndShow(Control, IEnumerable&lt;IHoverTip&gt;, HoverTipAlignment)
/// 失败时返回 null，调用方负责回退到 TextureRect 显示。
/// </summary>
internal static class NativeNodeFactory
{
    private static Type _nrelicType;
    private static Type _nrelicIconSizeType;
    private static Type _npotionType;
    private static Type _nhoverTipSetType;
    private static Type _hoverTipAlignmentType;

    private static MethodInfo _nrelicCreate;
    private static MethodInfo _npotionCreate;
    private static MethodInfo _hoverTipCreateAndShow;
    private static MethodInfo _hoverTipRemove;
    private static PropertyInfo _relicHoverTipsProp;
    private static PropertyInfo _potionHoverTipsProp;

    private static bool _resolveAttempted;
    private static bool _ready;

    private static bool EnsureResolved()
    {
        if (_resolveAttempted) return _ready;
        _resolveAttempted = true;

        try
        {
            _nrelicType = FindType("MegaCrit.Sts2.Core.Nodes.Relics.NRelic");
            _npotionType = FindType("MegaCrit.Sts2.Core.Nodes.Potions.NPotion");
            _nhoverTipSetType = FindType("MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet");
            _hoverTipAlignmentType = FindType("MegaCrit.Sts2.Core.HoverTips.HoverTipAlignment");

            _nrelicIconSizeType = _nrelicType?.GetNestedType("IconSize");

            if (_nrelicType != null && _nrelicIconSizeType != null)
            {
                _nrelicCreate = _nrelicType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
            }
            if (_npotionType != null)
            {
                _npotionCreate = _npotionType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
            }

            if (_nhoverTipSetType != null)
            {
                foreach (var m in _nhoverTipSetType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "CreateAndShow") continue;
                    var p = m.GetParameters();
                    if (p.Length == 3 && typeof(IEnumerable).IsAssignableFrom(p[1].ParameterType))
                    {
                        _hoverTipCreateAndShow = m;
                        break;
                    }
                }
                _hoverTipRemove = _nhoverTipSetType.GetMethod(
                    "Remove", BindingFlags.Public | BindingFlags.Static);
            }

            var relicModelType = FindType("MegaCrit.Sts2.Core.Models.RelicModel");
            var potionModelType = FindType("MegaCrit.Sts2.Core.Models.PotionModel");
            _relicHoverTipsProp = relicModelType?.GetProperty("HoverTips");
            _potionHoverTipsProp = potionModelType?.GetProperty("HoverTips");

            _ready = _nrelicCreate != null || _npotionCreate != null;
            return _ready;
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"NativeNodeFactory resolve failed: {ex.Message}");
            return false;
        }
    }

    public static Control TryCreateRelic(object relicModel, bool large)
    {
        if (!EnsureResolved() || _nrelicCreate == null || relicModel == null) return null;
        try
        {
            var iconSize = Enum.Parse(_nrelicIconSizeType, large ? "Large" : "Small");
            return _nrelicCreate.Invoke(null, new[] { relicModel, iconSize }) as Control;
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"NRelic.Create failed: {ex.Message}");
            return null;
        }
    }

    public static Control TryCreatePotion(object potionModel)
    {
        if (!EnsureResolved() || _npotionCreate == null || potionModel == null) return null;
        try
        {
            return _npotionCreate.Invoke(null, new[] { potionModel }) as Control;
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"NPotion.Create failed: {ex.Message}");
            return null;
        }
    }

    public static IDisposable ShowHoverTip(Control owner, object model, bool isRelic)
    {
        if (!EnsureResolved() || _hoverTipCreateAndShow == null || owner == null || model == null) return null;
        try
        {
            // 同一 owner 必须先释放上一次注册，否则原生 _activeHoverTips 字典 Add 会抛
            // ArgumentException，导致从第二次 hover 开始全部静默失败。
            RemoveActiveHoverTip(owner);

            var hoverTipsProp = isRelic ? _relicHoverTipsProp : _potionHoverTipsProp;
            var hoverTips = hoverTipsProp?.GetValue(model);
            if (hoverTips == null) return null;

            // 原生商店 NMerchantSlot 用 Right 对齐：tip 贴在 owner 右侧，
            // 越界由 CorrectHorizontalOverflow 自动翻转。None 等于 (0,0)→屏幕左上。
            var alignment = Enum.Parse(_hoverTipAlignmentType, "Right");
            var tipObj = _hoverTipCreateAndShow.Invoke(null, new[] { owner, hoverTips, alignment });

            // 原生 CreateAndShow 把 tip 挂在 NGame.HoverTipsContainer（root 普通节点），
            // 我们的 BlacklistPanel 是 CanvasLayer.Layer=100，渲染顺序永远高于 root，
            // 因此 tip 必然被面板遮挡。reparent 到 owner 所在 CanvasLayer 即可同层渲染，
            // ZIndex 跨 CanvasLayer 无效，必须用 reparent。
            // 商店内 owner 不在自定义 CanvasLayer 里，找不到时不动，保持原生行为。
            ReparentTipToOwnerLayer(tipObj as Control, owner);

            return new HoverTipHandle(owner);
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"ShowHoverTip failed: {ex.Message}");
            return null;
        }
    }

    private static void ReparentTipToOwnerLayer(Control tip, Control owner)
    {
        if (tip == null || !GodotObject.IsInstanceValid(tip)) return;
        if (owner == null || !GodotObject.IsInstanceValid(owner)) return;

        CanvasLayer host = null;
        Node n = owner;
        while (n != null)
        {
            if (n is CanvasLayer cl) { host = cl; break; }
            n = n.GetParent();
        }
        if (host == null)
        {
            // owner 不在自定义 CanvasLayer 内（例如商店真实场景），原生行为已正确，
            // 只额外加 ZIndex 保险即可。
            BoostZ(tip);
            return;
        }

        var parent = tip.GetParent();
        if (parent != host)
        {
            parent?.RemoveChild(tip);
            host.AddChild(tip);
        }
        // reparent 完成后再加 Z 顺序保险：同 CanvasLayer 内 tip 永远盖过面板。
        BoostZ(tip);
    }

    private static void BoostZ(Control tip)
    {
        try
        {
            tip.TopLevel = true;
            tip.ZAsRelative = false;
            tip.ZIndex = 4096;
            foreach (var child in tip.GetChildren())
            {
                if (child is CanvasItem ci)
                {
                    ci.ZAsRelative = false;
                    ci.ZIndex = 4096;
                }
            }
        }
        catch { /* 安全保险，忽略 */ }
    }

    private static void RemoveActiveHoverTip(Control owner)
    {
        if (_hoverTipRemove == null || owner == null) return;
        try { _hoverTipRemove.Invoke(null, new object[] { owner }); }
        catch { /* 安全清理，忽略 */ }
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

    private sealed class HoverTipHandle : IDisposable
    {
        private Control _owner;
        public HoverTipHandle(Control owner) { _owner = owner; }
        public void Dispose()
        {
            if (_owner != null && GodotObject.IsInstanceValid(_owner))
            {
                RemoveActiveHoverTip(_owner);
            }
            _owner = null;
        }
    }
}