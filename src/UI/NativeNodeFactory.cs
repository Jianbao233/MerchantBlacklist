using System;
using System.Collections;
using System.Collections.Generic;
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
    private static PropertyInfo _cardHoverTipsProp;
    private static MethodInfo _fromCardMethod;
    private static Type _hoverTipInterfaceType;

    /// <summary>最近一次创建的浮窗节点（自检诊断用；正常逻辑不依赖它）</summary>
    internal static Control LastTipNode;

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

            // 卡牌原生浮窗：HoverTipFactory.FromCard(CardModel, bool) → CardHoverTip
            // （NHoverTipCardContainer 会把它渲染成"卡牌本体"的小浮窗）
            var cardModelType = FindType("MegaCrit.Sts2.Core.Models.CardModel");
            _cardHoverTipsProp = cardModelType?.GetProperty("HoverTips");
            _hoverTipInterfaceType = FindType("MegaCrit.Sts2.Core.HoverTips.IHoverTip");
            var factoryType = FindType("MegaCrit.Sts2.Core.HoverTips.HoverTipFactory");
            if (factoryType != null && cardModelType != null)
            {
                _fromCardMethod = factoryType.GetMethod(
                    "FromCard", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { cardModelType, typeof(bool) }, null);
            }
            if (_fromCardMethod == null)
                MerchantBlacklistLog.Warn("HoverTipFactory.FromCard not found; card hover falls back to custom preview.");

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
            // 我们的 BlacklistPanel 是 CanvasLayer.Layer=1000，渲染顺序永远高于 root，
            // 因此 tip 必然被面板遮挡。reparent 到 owner 所在 CanvasLayer 即可同层渲染，
            // ZIndex 跨 CanvasLayer 无效，必须用 reparent。
            // 商店内 owner 不在自定义 CanvasLayer 里，找不到时不动，保持原生行为。
            ReparentTipToOwnerLayer(tipObj as Control, owner);
            LastTipNode = tipObj as Control;

            return new HoverTipHandle(owner);
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"ShowHoverTip failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 卡牌的**原生浮窗**（显示卡牌本体 + 关键字提示）。
    /// 游戏自己在 <c>NCardHolder</c> 里的用法是
    /// <c>NHoverTipSet.CreateAndShow(this, CardNode.Model.HoverTips)</c>，
    /// 而"把卡牌画出来"的那条 tip 由 <c>HoverTipFactory.FromCard(card)</c> 生成
    /// （<c>CardHoverTip</c> → <c>NHoverTipCardContainer</c> 用 <c>%Card</c> 节点渲染）。
    /// 游戏自己的 <c>FromCardWithCardHoverTips</c> 就是「FromCard(card) + card.HoverTips」的拼接，
    /// 这里照抄同一套组合，只是绕开泛型重载。
    /// 返回 null 表示反射没解析到（调用方应退回自绘预览）。
    /// </summary>
    public static IDisposable ShowCardHoverTip(Control owner, object cardModel, Control anchor = null)
    {
        if (!EnsureResolved() || _hoverTipCreateAndShow == null || owner == null || cardModel == null) return null;
        if (_fromCardMethod == null || _hoverTipInterfaceType == null) return null;
        try
        {
            RemoveActiveHoverTip(owner);

            var tipList = (IList)Activator.CreateInstance(
                typeof(List<>).MakeGenericType(_hoverTipInterfaceType));

            var cardTip = _fromCardMethod.Invoke(null, new[] { cardModel, (object)false });
            if (cardTip != null) tipList.Add(cardTip);

            if (_cardHoverTipsProp?.GetValue(cardModel) is IEnumerable extras)
                foreach (var t in extras) tipList.Add(t);

            if (tipList.Count == 0) return null;

            var alignment = Enum.Parse(_hoverTipAlignmentType, "Right");
            var tipObj = _hoverTipCreateAndShow.Invoke(null, new object[] { owner, tipList, alignment });
            ReparentTipToOwnerLayer(tipObj as Control, owner, anchor);
            LastTipNode = tipObj as Control;
            return new HoverTipHandle(owner);
        }
        catch (Exception ex)
        {
            MerchantBlacklistLog.Warn($"ShowCardHoverTip failed: {ex.Message}");
            return null;
        }
    }

    private static void ReparentTipToOwnerLayer(Control tip, Control owner, Control anchor = null)
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
        EnsureCardSized(tip);
        PlaceTipNearOwner(tip, anchor ?? owner);
    }

    /// <summary>
    /// reparent 之后**必须自己摆位置**：游戏原先按它自己的父节点坐标系算好的位置，
    /// 换父之后会退化成 (0,0)（实测浮窗尺寸 360x116 但画在窗口左上角）。
    /// 这里按 owner 的全局矩形贴到右侧；右侧放不下就翻到左侧；再夹进视口内。
    /// </summary>
    private static void PlaceTipNearOwner(Control tip, Control owner)
    {
        try
        {
            if (tip == null || owner == null || !GodotObject.IsInstanceValid(tip) || !GodotObject.IsInstanceValid(owner)) return;

            // 要摆的是**内容容器**：卡牌浮窗的内容在 cardHoverTipContainer 里，
            // 游戏自己给这些子容器算过相对偏移（实测 221,374 / 1444,374），
            // 所以不能直接设 tip 的位置 —— 否则与子偏移叠加，卡牌会跑到下方偏右。
            // 正确做法：按内容容器**当前的实际全局矩形**反推需要位移多少。
            var content = tip.FindChild("cardHoverTipContainer", recursive: true, owned: false) as Control ?? tip;
            var contentRect = content.GetGlobalRect();
            var ownerRect = owner.GetGlobalRect();
            var viewport = owner.GetViewportRect().Size;

            float w = Mathf.Max(contentRect.Size.X, 1f);
            float h = Mathf.Max(contentRect.Size.Y, 1f);
            const float gap = 10f;

            float x = ownerRect.Position.X + ownerRect.Size.X + gap;
            if (x + w > viewport.X - 6f) x = ownerRect.Position.X - w - gap;   // 右侧放不下就翻到左侧
            float y = ownerRect.Position.Y - 8f;                                // 顶部对齐（比"垂直居中"更稳）

            x = Mathf.Clamp(x, 6f, Mathf.Max(6f, viewport.X - w - 6f));
            y = Mathf.Clamp(y, 6f, Mathf.Max(6f, viewport.Y - h - 6f));

            tip.Position += new Vector2(x - contentRect.Position.X, y - contentRect.Position.Y);
        }
        catch { /* 定位失败就保持原样，不影响功能 */ }
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

    private static Vector2? _ncardDefaultSize;

    /// <summary>
    /// 卡牌浮窗里的 <c>%Card</c> 节点**必须手动给尺寸**。
    /// 依据：<c>scenes/ui/card_hover_tip.tscn</c> 里该节点的 offset 全设在中心点
    /// （<c>offset_left = offset_right = 115</c> / <c>offset_top = offset_bottom = 157</c>，即 size = 0），
    /// 靠 <c>grow_direction = BOTH</c> 在游戏自己的流程里被撑开；经我们 reparent 之后它保持 0×0，
    /// 表现就是"浮窗容器有尺寸、位置也对，但画面上什么都没有"。
    /// 兜底算法：<c>NCard.defaultSize × 节点 scale</c>（场景里 scale = 0.75）。
    /// </summary>
    private static void EnsureCardSized(Control tip)
    {
        try
        {
            if (tip == null || !GodotObject.IsInstanceValid(tip)) return;
            var card = tip.FindChild("Card", recursive: true, owned: false) as Control;
            if (card == null) return;
            if (card.Size.X > 1f && card.Size.Y > 1f) return;   // 已经被撑开就不动

            if (_ncardDefaultSize == null)
            {
                var ncardType = FindType("MegaCrit.Sts2.Core.Nodes.Cards.NCard");
                var field = ncardType?.GetField("defaultSize", BindingFlags.Public | BindingFlags.Static);
                _ncardDefaultSize = field?.GetValue(null) is Vector2 v ? v : new Vector2(300f, 422f);
            }
            var scale = card.Scale;
            card.Size = new Vector2(_ncardDefaultSize.Value.X * scale.X, _ncardDefaultSize.Value.Y * scale.Y);
        }
        catch { /* 兜底失败不影响其它功能 */ }
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