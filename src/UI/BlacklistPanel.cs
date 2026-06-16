using System;
using System.Collections.Generic;
using Godot;
using MerchantBlacklist.Core;

namespace MerchantBlacklist.UI;

/// <summary>
/// 商店黑名单设置面板（独立 CanvasLayer，按需延迟创建）。
/// 不依赖游戏 IRunState/UnlockState，可在主菜单 / 战斗外随时呼出。
///
/// 结构：
///   CanvasLayer
///     └─ Panel _window  (640x520, 拖拽 + 缩放，仿 NoClientCheats.CheatHistoryPanel)
///         └─ VBoxContainer
///             ├─ TitleBar (拖拽区 + 关闭/居中/清空)
///             ├─ Tab 切换条 (遗物 / 药水)
///             ├─ ScrollContainer
///             │   └─ GridContainer  (TextureRect 网格)
///             └─ HintBar (统计 + 快捷键)
/// </summary>
public partial class BlacklistPanel : CanvasLayer
{
    private const float MinWidth = 480f;
    private const float MinHeight = 360f;
    private const float MaxWidth = 1200f;
    private const float MaxHeight = 900f;
    private const float TitleBarHeight = 32f;
    private const float EdgeThreshold = 8f;
    private const int IconSize = 64;
    private const int GridColumns = 8;

    [Flags]
    private enum ResizeEdge
    {
        None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8,
    }

    private enum Tab { Relics, Potions }

    // ── 单例（按需创建）──
    private static BlacklistPanel _instance;

    public static BlacklistPanel EnsureCreated()
    {
        if (GodotObject.IsInstanceValid(_instance)) return _instance;

        _instance = new BlacklistPanel();
        var tree = Engine.GetMainLoop() as SceneTree;
        var root = tree?.Root;
        if (root != null)
        {
            root.CallDeferred(Node.MethodName.AddChild, _instance);
        }
        return _instance;
    }

    public static void Toggle()
    {
        var p = EnsureCreated();
        p?.CallDeferred(nameof(_ToggleSelf));
    }

    public static void ShowPanel()
    {
        var p = EnsureCreated();
        p?.CallDeferred(nameof(_ShowSelf));
    }

    public static void HidePanel()
    {
        if (!GodotObject.IsInstanceValid(_instance)) return;
        _instance.CallDeferred(nameof(_HideSelf));
    }

    // ── 实例字段 ──
    private Panel _window;
    private VBoxContainer _vbox;
    private PanelContainer _titleBar;
    private Label _titleLabel;
    private Label _hintLabel;
    private GridContainer _grid;
    private ScrollContainer _scroll;
    private Tab _currentTab = Tab.Relics;
    private bool _builtOnce;
    private bool _isVisible;

    private bool _isDragging;
    private Vector2 _dragOffset;
    private bool _titleDragPending;
    private Vector2 _titleDragStart;

    private bool _isResizing;
    private ResizeEdge _resizeEdges;
    private Vector2 _resizeStartPos;
    private Vector2 _resizeStartPanelPos;
    private float _resizeStartWidth;
    private float _resizeStartHeight;

    public BlacklistPanel()
    {
        Name = "MerchantBlacklistPanel";
        Layer = 100;
        ProcessMode = Node.ProcessModeEnum.Always;
    }

    public override void _Ready()
    {
        _BuildUI();
        if (_window != null) _window.Visible = false;
    }

    private void _ToggleSelf()
    {
        if (_isVisible) _HideSelf();
        else _ShowSelf();
    }

    private void _ShowSelf()
    {
        if (_window == null || !GodotObject.IsInstanceValid(_window)) _BuildUI();
        if (_window == null) return;
        _window.Visible = true;
        _isVisible = true;
        _RefreshGrid();
    }

    private void _HideSelf()
    {
        if (_window == null) return;
        _window.Visible = false;
        _isVisible = false;
    }

    public override void _Input(InputEvent ev)
    {
        if (_window == null || !GodotObject.IsInstanceValid(_window) || !_window.Visible) return;

        if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                var mousePos = mb.GlobalPosition;
                if (_IsOverTitleBar(mousePos))
                {
                    _titleDragPending = true;
                    _titleDragStart = mousePos;
                }
                else
                {
                    var local = _window.GetLocalMousePosition();
                    var edge = _DetectEdges(local);
                    if (edge != ResizeEdge.None)
                    {
                        _isResizing = true;
                        _resizeEdges = edge;
                        _resizeStartPos = mousePos;
                        _resizeStartPanelPos = _window.Position;
                        _resizeStartWidth = _window.Size.X;
                        _resizeStartHeight = _window.Size.Y;
                    }
                }
            }
            else
            {
                _isResizing = false;
                _resizeEdges = ResizeEdge.None;
                _isDragging = false;
                _titleDragPending = false;
            }
            return;
        }

        if (ev is InputEventMouseMotion mm)
        {
            var mousePos = mm.GlobalPosition;
            if (_isResizing)
            {
                _ApplyResize(mousePos);
                return;
            }
            if (_titleDragPending)
            {
                if (!_isDragging && _titleDragStart.DistanceTo(mousePos) > 4f)
                {
                    _isDragging = true;
                    _dragOffset = _window.Position - mousePos;
                }
                if (_isDragging)
                {
                    _window.Position = mousePos + _dragOffset;
                }
                return;
            }
            var localPos = _window.GetLocalMousePosition();
            if (localPos.Y >= 0 && localPos.Y < TitleBarHeight)
            {
                _window.MouseDefaultCursorShape = Control.CursorShape.Move;
            }
            else
            {
                var edge = _DetectEdges(localPos);
                _window.MouseDefaultCursorShape = edge != ResizeEdge.None
                    ? _GetResizeCursor(edge)
                    : Control.CursorShape.Arrow;
            }
        }
    }

    // ── UI 构建 ────────────────────────────────────────────────────────
    private void _BuildUI()
    {
        if (_builtOnce && GodotObject.IsInstanceValid(_window)) return;
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree?.Root == null)
        {
            CallDeferred(nameof(_BuildUI));
            return;
        }

        var screen = (float)DisplayServer.WindowGetSize().Y;
        var screenW = (float)DisplayServer.WindowGetSize().X;

        _window = new Panel
        {
            Name = "BlacklistWindow",
            CustomMinimumSize = new Vector2(MinWidth, MinHeight),
            Size = new Vector2(720f, 560f),
            Position = new Vector2(MathF.Max(40f, (screenW - 720f) / 2f), MathF.Max(40f, (screen - 560f) / 2f)),
            ClipContents = true,
        };

        var winStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.06f, 0.08f, 0.96f),
            BorderColor = new Color(0.2f, 0.2f, 0.25f, 1f),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
        };
        _window.AddThemeStyleboxOverride("panel", winStyle);

        _vbox = new VBoxContainer { Name = "VBox", MouseFilter = Control.MouseFilterEnum.Ignore };
        _vbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _vbox.OffsetLeft = 8;
        _vbox.OffsetTop = 8;
        _vbox.OffsetRight = -8;
        _vbox.OffsetBottom = -8;
        _vbox.AddThemeConstantOverride("separation", 6);
        _window.AddChild(_vbox);

        _BuildTitleBar();
        _BuildTabBar();
        _BuildScrollGrid();
        _BuildHintBar();

        AddChild(_window);
        _builtOnce = true;
    }

    private void _BuildTitleBar()
    {
        _titleBar = new PanelContainer
        {
            Name = "TitleBar",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, TitleBarHeight),
        };
        var titleStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.12f, 0.16f, 1f),
            BorderColor = new Color(0.2f, 0.2f, 0.25f, 1f),
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
        };
        _titleBar.AddThemeStyleboxOverride("panel", titleStyle);
        _vbox.AddChild(_titleBar);

        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _titleBar.AddChild(row);

        _titleLabel = new Label
        {
            Text = "  商店黑名单",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            TooltipText = "拖拽移动窗口；拖拽边缘缩放",
            MouseDefaultCursorShape = Control.CursorShape.Move,
        };
        _titleLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.9f, 1f));
        _titleLabel.AddThemeFontSizeOverride("font_size", 14);
        row.AddChild(_titleLabel);

        var clearBtn = new Button
        {
            Text = "清空",
            Flat = true,
            CustomMinimumSize = new Vector2(60, TitleBarHeight),
            TooltipText = "清空当前页签的所有黑名单",
        };
        clearBtn.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.65f, 1f));
        clearBtn.AddThemeColorOverride("font_hover_color", new Color(1f, 0.5f, 0.2f, 1f));
        clearBtn.Pressed += _OnClearPressed;
        row.AddChild(clearBtn);

        var closeBtn = new Button
        {
            Text = "✕",
            Flat = true,
            CustomMinimumSize = new Vector2(36, TitleBarHeight),
            TooltipText = "关闭（F10）",
        };
        closeBtn.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.65f, 1f));
        closeBtn.AddThemeColorOverride("font_hover_color", new Color(1f, 0.3f, 0.3f, 1f));
        closeBtn.Pressed += _HideSelf;
        row.AddChild(closeBtn);
    }

    private void _BuildTabBar()
    {
        var tabs = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        tabs.AddThemeConstantOverride("separation", 4);
        _vbox.AddChild(tabs);

        var relicBtn = new Button
        {
            Text = "遗物",
            ToggleMode = true,
            ButtonPressed = _currentTab == Tab.Relics,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 30),
        };
        var potionBtn = new Button
        {
            Text = "药水",
            ToggleMode = true,
            ButtonPressed = _currentTab == Tab.Potions,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 30),
        };

        relicBtn.Pressed += () =>
        {
            _currentTab = Tab.Relics;
            relicBtn.ButtonPressed = true;
            potionBtn.ButtonPressed = false;
            _RefreshGrid();
        };
        potionBtn.Pressed += () =>
        {
            _currentTab = Tab.Potions;
            relicBtn.ButtonPressed = false;
            potionBtn.ButtonPressed = true;
            _RefreshGrid();
        };

        tabs.AddChild(relicBtn);
        tabs.AddChild(potionBtn);
    }

    private void _BuildScrollGrid()
    {
        _scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _vbox.AddChild(_scroll);

        _grid = new GridContainer
        {
            Columns = GridColumns,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _grid.AddThemeConstantOverride("h_separation", 8);
        _grid.AddThemeConstantOverride("v_separation", 8);
        _scroll.AddChild(_grid);
    }

    private void _BuildHintBar()
    {
        _hintLabel = new Label
        {
            Text = "  点击图标 → 加入/移除黑名单；F10 切换面板",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkEnd,
        };
        _hintLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.6f, 1f));
        _hintLabel.AddThemeFontSizeOverride("font_size", 12);
        _vbox.AddChild(_hintLabel);
    }

    // ── 网格刷新 ────────────────────────────────────────────────────────
    private void _RefreshGrid()
    {
        if (_grid == null) return;

        foreach (var child in _grid.GetChildren())
        {
            child.QueueFree();
        }

        ModelCatalog.EnsureBuilt();
        IReadOnlyList<ModelCatalog.Entry> entries = _currentTab == Tab.Relics
            ? ModelCatalog.Relics
            : ModelCatalog.Potions;

        int bannedCount = 0;
        foreach (var entry in entries)
        {
            bool banned = _currentTab == Tab.Relics
                ? BlacklistStore.IsRelicBanned(entry.Id)
                : BlacklistStore.IsPotionBanned(entry.Id);
            if (banned) bannedCount++;

            _grid.AddChild(_MakeCell(entry, banned));
        }

        int totalRelics = ModelCatalog.Relics.Count;
        int totalPotions = ModelCatalog.Potions.Count;
        int relicBan = BlacklistStore.RelicCount;
        int potionBan = BlacklistStore.PotionCount;
        _titleLabel.Text = _currentTab == Tab.Relics
            ? $"  商店黑名单  ·  遗物 {relicBan}/{totalRelics}  药水 {potionBan}/{totalPotions}"
            : $"  商店黑名单  ·  遗物 {relicBan}/{totalRelics}  药水 {potionBan}/{totalPotions}";
        _hintLabel.Text = $"  点击图标切换 ban；当前页 {bannedCount}/{entries.Count} 已 ban；F10 切换面板";
    }

    private Control _MakeCell(ModelCatalog.Entry entry, bool banned)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(IconSize + 12, IconSize + 12),
            Flat = true,
            FocusMode = Control.FocusModeEnum.None,
            ToggleMode = false,
            TooltipText = $"{entry.Title}\n{entry.Rarity}\nid: {entry.Id}\n{(banned ? "已 ban，点击移除" : "点击加入黑名单")}",
        };

        var bg = new StyleBoxFlat
        {
            BgColor = banned ? new Color(0.5f, 0.1f, 0.1f, 0.5f) : new Color(0.15f, 0.15f, 0.18f, 0.7f),
            BorderColor = banned ? new Color(1f, 0.3f, 0.3f, 1f) : new Color(0.3f, 0.3f, 0.35f, 1f),
            BorderWidthLeft = banned ? 2 : 1,
            BorderWidthRight = banned ? 2 : 1,
            BorderWidthTop = banned ? 2 : 1,
            BorderWidthBottom = banned ? 2 : 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        btn.AddThemeStyleboxOverride("normal", bg);
        btn.AddThemeStyleboxOverride("hover", bg);
        btn.AddThemeStyleboxOverride("pressed", bg);

        if (entry.Icon != null)
        {
            var rect = new TextureRect
            {
                Texture = entry.Icon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(IconSize, IconSize),
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Modulate = banned ? new Color(0.45f, 0.45f, 0.45f, 1f) : Colors.White,
            };
            rect.SetAnchorsPreset(Control.LayoutPreset.Center);
            rect.OffsetLeft = -IconSize / 2f;
            rect.OffsetTop = -IconSize / 2f;
            rect.OffsetRight = IconSize / 2f;
            rect.OffsetBottom = IconSize / 2f;
            btn.AddChild(rect);
        }
        else
        {
            var label = new Label
            {
                Text = entry.Id,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            label.AddThemeFontSizeOverride("font_size", 10);
            label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            btn.AddChild(label);
        }

        if (banned)
        {
            var x = new Label
            {
                Text = "✕",
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            x.AddThemeColorOverride("font_color", new Color(1f, 0.4f, 0.4f, 1f));
            x.AddThemeFontSizeOverride("font_size", 24);
            x.SetAnchorsPreset(Control.LayoutPreset.TopRight);
            x.OffsetLeft = -22;
            x.OffsetTop = -2;
            btn.AddChild(x);
        }

        btn.Pressed += () => _OnCellClicked(entry);
        return btn;
    }

    private void _OnCellClicked(ModelCatalog.Entry entry)
    {
        if (_currentTab == Tab.Relics)
            BlacklistStore.ToggleRelic(entry.Id);
        else
            BlacklistStore.TogglePotion(entry.Id);
        _RefreshGrid();
    }

    private void _OnClearPressed()
    {
        if (_currentTab == Tab.Relics) BlacklistStore.ClearRelics();
        else BlacklistStore.ClearPotions();
        _RefreshGrid();
    }

    // ── 拖拽 / 缩放工具 ────────────────────────────────────────────────
    private bool _IsOverTitleBar(Vector2 globalPos)
    {
        if (_titleBar == null || !GodotObject.IsInstanceValid(_titleBar)) return false;
        var titleY = _titleBar.GetGlobalPosition().Y;
        return globalPos.Y >= titleY && globalPos.Y < titleY + TitleBarHeight;
    }

    private ResizeEdge _DetectEdges(Vector2 localPos)
    {
        if (_window == null) return ResizeEdge.None;
        var size = _window.Size;
        var e = ResizeEdge.None;
        if (localPos.X < EdgeThreshold) e |= ResizeEdge.Left;
        else if (localPos.X > size.X - EdgeThreshold) e |= ResizeEdge.Right;
        if (localPos.Y < EdgeThreshold) e |= ResizeEdge.Top;
        else if (localPos.Y > size.Y - EdgeThreshold) e |= ResizeEdge.Bottom;
        return e;
    }

    private static Control.CursorShape _GetResizeCursor(ResizeEdge edges)
    {
        bool h = (edges & (ResizeEdge.Left | ResizeEdge.Right)) != ResizeEdge.None;
        bool v = (edges & (ResizeEdge.Top | ResizeEdge.Bottom)) != ResizeEdge.None;
        if (h && v)
        {
            return ((edges & ResizeEdge.Left) != 0 && (edges & ResizeEdge.Top) != 0)
                || ((edges & ResizeEdge.Right) != 0 && (edges & ResizeEdge.Bottom) != 0)
                ? Control.CursorShape.Fdiagsize
                : Control.CursorShape.Bdiagsize;
        }
        if (h) return Control.CursorShape.Hsize;
        if (v) return Control.CursorShape.Vsize;
        return Control.CursorShape.Arrow;
    }

    private void _ApplyResize(Vector2 globalPos)
    {
        if (_window == null) return;
        var delta = globalPos - _resizeStartPos;
        var pos = _resizeStartPanelPos;
        var w = _resizeStartWidth;
        var h = _resizeStartHeight;
        if ((_resizeEdges & ResizeEdge.Right) != 0) w = _resizeStartWidth + delta.X;
        if ((_resizeEdges & ResizeEdge.Left) != 0) { w = _resizeStartWidth - delta.X; pos.X = _resizeStartPanelPos.X + delta.X; }
        if ((_resizeEdges & ResizeEdge.Bottom) != 0) h = _resizeStartHeight + delta.Y;
        if ((_resizeEdges & ResizeEdge.Top) != 0) { h = _resizeStartHeight - delta.Y; pos.Y = _resizeStartPanelPos.Y + delta.Y; }
        w = Math.Clamp(w, MinWidth, MaxWidth);
        h = Math.Clamp(h, MinHeight, MaxHeight);
        if ((_resizeEdges & ResizeEdge.Left) != 0) pos.X = _resizeStartPanelPos.X + (_resizeStartWidth - w);
        if ((_resizeEdges & ResizeEdge.Top) != 0) pos.Y = _resizeStartPanelPos.Y + (_resizeStartHeight - h);
        _window.Size = new Vector2(w, h);
        _window.Position = pos;
    }
}