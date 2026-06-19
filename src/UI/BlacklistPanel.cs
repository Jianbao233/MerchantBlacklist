using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MerchantBlacklist.Core;

namespace MerchantBlacklist.UI;

/// <summary>
/// 商店黑名单面板（方案 B：仿《Compendium · Relic Collection》视觉的自建窗口）。
///
/// 设计要点：
/// 1. 不依赖 IRunState/UnlockState，避免主菜单/战斗外被原生面板拒绝实例化；
/// 2. 按稀有度分组：遗物 Common / Uncommon / Rare / Shop，药水 Common / Uncommon / Rare；
/// 3. 颜色对齐游戏内 StsColors（cream/blue/gold/blue），配色采用羊皮纸暖棕；
/// 4. 文案根据 LocManager.Instance.Language 在中文 / 英文之间切换；
/// 5. 拖拽 + 边缘缩放（与早期版本一致）。
/// </summary>
public partial class BlacklistPanel : CanvasLayer
{
    private const float MinWidth = 560f;
    private const float MinHeight = 420f;
    private const float MaxWidth = 1400f;
    private const float MaxHeight = 1000f;
    private const float TitleBarHeight = 36f;
    private const float EdgeThreshold = 8f;
    private const int IconSize = 72;
    private const int CellPadding = 14;
    private const int CellHeightExtra = 26;
    private const int GridColumns = 8;

    private static readonly string[] RelicGroups = { "Common", "Uncommon", "Rare", "Shop" };
    private static readonly string[] PotionGroups = { "Common", "Uncommon", "Rare" };

    [Flags]
    private enum ResizeEdge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    private enum Tab { Relics, Potions }

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

    private Panel _window;
    private VBoxContainer _vbox;
    private PanelContainer _titleBar;
    private Label _titleLabel;
    private Label _hintLabel;
    private VBoxContainer _content;
    private ScrollContainer _scroll;
    private Button _relicTabBtn;
    private Button _potionTabBtn;
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
        _RefreshContent();
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

        var screenW = (float)DisplayServer.WindowGetSize().X;
        var screenH = (float)DisplayServer.WindowGetSize().Y;
        var w = MathF.Min(900f, screenW * 0.7f);
        var h = MathF.Min(700f, screenH * 0.78f);

        _window = new Panel
        {
            Name = "BlacklistWindow",
            CustomMinimumSize = new Vector2(MinWidth, MinHeight),
            Size = new Vector2(w, h),
            Position = new Vector2(MathF.Max(40f, (screenW - w) / 2f), MathF.Max(40f, (screenH - h) / 2f)),
            ClipContents = true,
        };

        var winStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.08f, 0.06f, 0.97f),
            BorderColor = new Color(0.55f, 0.42f, 0.22f, 1f),
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10,
        };
        _window.AddThemeStyleboxOverride("panel", winStyle);

        _vbox = new VBoxContainer { Name = "VBox", MouseFilter = Control.MouseFilterEnum.Ignore };
        _vbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _vbox.OffsetLeft = 10;
        _vbox.OffsetTop = 10;
        _vbox.OffsetRight = -10;
        _vbox.OffsetBottom = -10;
        _vbox.AddThemeConstantOverride("separation", 6);
        _window.AddChild(_vbox);

        _BuildTitleBar();
        _BuildTabBar();
        _BuildScroll();
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
            BgColor = new Color(0.18f, 0.13f, 0.08f, 1f),
            BorderColor = new Color(0.55f, 0.42f, 0.22f, 1f),
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
            Text = _Loc("  商店黑名单", "  Shop Blacklist"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            TooltipText = _Loc("拖拽标题栏移动；拖拽边缘缩放", "Drag title to move; drag edges to resize"),
            MouseDefaultCursorShape = Control.CursorShape.Move,
        };
        _titleLabel.AddThemeColorOverride("font_color", new Color(1f, 0.92f, 0.78f, 1f));
        _titleLabel.AddThemeFontSizeOverride("font_size", 18);
        row.AddChild(_titleLabel);

        var clearBtn = new Button
        {
            Text = _Loc("清空", "Clear"),
            Flat = true,
            CustomMinimumSize = new Vector2(64, TitleBarHeight),
            TooltipText = _Loc("清空当前页签的所有黑名单", "Clear blacklist for current tab"),
        };
        clearBtn.AddThemeColorOverride("font_color", new Color(0.85f, 0.75f, 0.55f, 1f));
        clearBtn.AddThemeColorOverride("font_hover_color", new Color(1f, 0.55f, 0.25f, 1f));
        clearBtn.AddThemeFontSizeOverride("font_size", 14);
        clearBtn.Pressed += _OnClearPressed;
        row.AddChild(clearBtn);

        var closeBtn = new Button
        {
            Text = "✕",
            Flat = true,
            CustomMinimumSize = new Vector2(36, TitleBarHeight),
            TooltipText = _Loc("关闭（F10）", "Close (F10)"),
        };
        closeBtn.AddThemeColorOverride("font_color", new Color(0.85f, 0.75f, 0.55f, 1f));
        closeBtn.AddThemeColorOverride("font_hover_color", new Color(1f, 0.35f, 0.35f, 1f));
        closeBtn.AddThemeFontSizeOverride("font_size", 16);
        closeBtn.Pressed += _HideSelf;
        row.AddChild(closeBtn);
    }

    private void _BuildTabBar()
    {
        var tabs = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        tabs.AddThemeConstantOverride("separation", 6);
        _vbox.AddChild(tabs);

        _relicTabBtn = _MakeTabButton(_Loc("遗物", "Relics"), _currentTab == Tab.Relics);
        _potionTabBtn = _MakeTabButton(_Loc("药水", "Potions"), _currentTab == Tab.Potions);

        _relicTabBtn.Pressed += () =>
        {
            _currentTab = Tab.Relics;
            _relicTabBtn.ButtonPressed = true;
            _potionTabBtn.ButtonPressed = false;
            _RefreshContent();
        };
        _potionTabBtn.Pressed += () =>
        {
            _currentTab = Tab.Potions;
            _relicTabBtn.ButtonPressed = false;
            _potionTabBtn.ButtonPressed = true;
            _RefreshContent();
        };

        tabs.AddChild(_relicTabBtn);
        tabs.AddChild(_potionTabBtn);
    }

    private Button _MakeTabButton(string text, bool active)
    {
        var btn = new Button
        {
            Text = text,
            ToggleMode = true,
            ButtonPressed = active,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 34),
        };
        btn.AddThemeFontSizeOverride("font_size", 15);
        btn.AddThemeColorOverride("font_color", new Color(0.75f, 0.65f, 0.45f, 1f));
        btn.AddThemeColorOverride("font_pressed_color", new Color(1f, 0.92f, 0.6f, 1f));
        btn.AddThemeColorOverride("font_hover_color", new Color(1f, 0.85f, 0.5f, 1f));
        return btn;
    }

    private void _BuildScroll()
    {
        _scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _vbox.AddChild(_scroll);

        _content = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _content.AddThemeConstantOverride("separation", 16);
        _scroll.AddChild(_content);
    }

    private void _BuildHintBar()
    {
        _hintLabel = new Label
        {
            Text = _Loc("  点击图标 → 加入/移除黑名单；F10 切换面板",
                        "  Click icon → ban/unban; F10 toggles this panel"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkEnd,
        };
        _hintLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.55f, 0.42f, 1f));
        _hintLabel.AddThemeFontSizeOverride("font_size", 12);
        _vbox.AddChild(_hintLabel);
    }

    // ── 内容刷新 ────────────────────────────────────────────────────────
    private void _RefreshContent()
    {
        if (_content == null) return;

        foreach (var child in _content.GetChildren())
        {
            child.QueueFree();
        }

        ModelCatalog.EnsureBuilt();
        var entries = _currentTab == Tab.Relics ? ModelCatalog.Relics : ModelCatalog.Potions;
        var groups = _currentTab == Tab.Relics ? RelicGroups : PotionGroups;

        var grouped = new Dictionary<string, List<ModelCatalog.Entry>>(StringComparer.Ordinal);
        foreach (var g in groups) grouped[g] = new List<ModelCatalog.Entry>();
        foreach (var e in entries)
        {
            if (grouped.TryGetValue(e.Rarity, out var list)) list.Add(e);
        }

        int totalBanned = 0;
        foreach (var group in groups)
        {
            var list = grouped[group];
            if (list.Count == 0) continue;
            int bannedHere = 0;
            foreach (var e in list)
            {
                if (_IsBanned(e.Id)) bannedHere++;
            }
            totalBanned += bannedHere;
            _content.AddChild(_BuildGroupSection(group, list, bannedHere));
        }

        _UpdateTitleAndHint(totalBanned, entries.Count);
    }

    private Control _BuildGroupSection(string rarity, List<ModelCatalog.Entry> list, int bannedHere)
    {
        var section = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        section.AddThemeConstantOverride("separation", 8);

        var headerRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        headerRow.AddThemeConstantOverride("separation", 10);
        section.AddChild(headerRow);

        var header = new Label
        {
            Text = $"{_LocRarity(rarity)}   ·   {bannedHere}/{list.Count}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        header.AddThemeColorOverride("font_color", _RarityColor(rarity));
        header.AddThemeFontSizeOverride("font_size", 20);
        headerRow.AddChild(header);

        var allowAllBtn = _MakeGroupActionButton(_Loc("全启用", "Allow all"), new Color(0.45f, 0.85f, 0.55f, 1f));
        allowAllBtn.Pressed += () => _OnGroupBulk(list, ban: false);
        headerRow.AddChild(allowAllBtn);

        var banAllBtn = _MakeGroupActionButton(_Loc("全 ban", "Ban all"), new Color(1f, 0.45f, 0.45f, 1f));
        banAllBtn.Pressed += () => _OnGroupBulk(list, ban: true);
        headerRow.AddChild(banAllBtn);

        var divider = new ColorRect
        {
            Color = new Color(0.55f, 0.42f, 0.22f, 0.4f),
            CustomMinimumSize = new Vector2(0, 1),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        section.AddChild(divider);

        var grid = new GridContainer
        {
            Columns = GridColumns,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 12);
        foreach (var entry in list)
        {
            grid.AddChild(_MakeCell(entry, _IsBanned(entry.Id)));
        }
        section.AddChild(grid);

        return section;
    }

    private Button _MakeGroupActionButton(string text, Color tint)
    {
        var btn = new Button
        {
            Text = text,
            Flat = true,
            CustomMinimumSize = new Vector2(72, 26),
            FocusMode = Control.FocusModeEnum.None,
        };
        btn.AddThemeColorOverride("font_color", new Color(0.85f, 0.75f, 0.55f, 1f));
        btn.AddThemeColorOverride("font_hover_color", tint);
        btn.AddThemeFontSizeOverride("font_size", 13);
        return btn;
    }

    private void _OnGroupBulk(List<ModelCatalog.Entry> list, bool ban)
    {
        bool changed = false;
        if (_currentTab == Tab.Relics)
        {
            foreach (var e in list)
            {
                if (ban) changed |= BlacklistStore.AddRelic(e.Id);
                else changed |= _RemoveRelic(e.Id);
            }
        }
        else
        {
            foreach (var e in list)
            {
                if (ban) changed |= BlacklistStore.AddPotion(e.Id);
                else changed |= _RemovePotion(e.Id);
            }
        }
        if (changed) _RefreshContent();
    }

    private static bool _RemoveRelic(string id)
    {
        if (!BlacklistStore.IsRelicBanned(id)) return false;
        BlacklistStore.ToggleRelic(id);
        return true;
    }

    private static bool _RemovePotion(string id)
    {
        if (!BlacklistStore.IsPotionBanned(id)) return false;
        BlacklistStore.TogglePotion(id);
        return true;
    }

    private Control _MakeCell(ModelCatalog.Entry entry, bool banned)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(IconSize + CellPadding, IconSize + CellPadding + CellHeightExtra),
            Flat = true,
            FocusMode = Control.FocusModeEnum.None,
            ToggleMode = false,
            ClipContents = false,
        };

        var bg = new StyleBoxFlat
        {
            BgColor = banned ? new Color(0.45f, 0.10f, 0.10f, 0.55f) : new Color(0.16f, 0.13f, 0.10f, 0.7f),
            BorderColor = banned ? new Color(1f, 0.35f, 0.35f, 1f) : new Color(0.55f, 0.42f, 0.22f, 0.6f),
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

        Control iconHost = _MakeIconHost(entry, banned);
        if (iconHost != null) btn.AddChild(iconHost);

        var nameLabel = new Label
        {
            Text = entry.Title ?? entry.Id,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ClipText = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(IconSize + CellPadding, CellHeightExtra),
        };
        nameLabel.AddThemeFontSizeOverride("font_size", 11);
        nameLabel.AddThemeColorOverride("font_color",
            banned ? new Color(0.85f, 0.55f, 0.55f, 1f) : new Color(0.95f, 0.88f, 0.72f, 1f));
        nameLabel.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        nameLabel.OffsetTop = -CellHeightExtra;
        nameLabel.OffsetBottom = -2;
        nameLabel.OffsetLeft = 2;
        nameLabel.OffsetRight = -2;
        btn.AddChild(nameLabel);

        if (banned)
        {
            var x = new Label
            {
                Text = "✕",
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            x.AddThemeColorOverride("font_color", new Color(1f, 0.45f, 0.45f, 1f));
            x.AddThemeFontSizeOverride("font_size", 26);
            x.SetAnchorsPreset(Control.LayoutPreset.TopRight);
            x.OffsetLeft = -24;
            x.OffsetTop = -2;
            btn.AddChild(x);
        }

        // 原生 hover tip：进入时挂、退出时销毁。
        IDisposable hoverHandle = null;
        btn.MouseEntered += () =>
        {
            hoverHandle?.Dispose();
            hoverHandle = NativeNodeFactory.ShowHoverTip(btn, entry.RawModel, _currentTab == Tab.Relics);
        };
        btn.MouseExited += () =>
        {
            hoverHandle?.Dispose();
            hoverHandle = null;
        };
        btn.TreeExiting += () =>
        {
            hoverHandle?.Dispose();
            hoverHandle = null;
        };

        btn.Pressed += () => _OnCellClicked(entry);
        return btn;
    }

    private Control _MakeIconHost(ModelCatalog.Entry entry, bool banned)
    {
        var host = new Control
        {
            CustomMinimumSize = new Vector2(IconSize, IconSize),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipContents = false,
        };
        host.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        host.OffsetLeft = (CellPadding) / 2f;
        host.OffsetRight = -(CellPadding) / 2f;
        host.OffsetTop = 4;
        host.OffsetBottom = 4 + IconSize;

        Node nativeNode = _currentTab == Tab.Relics
            ? NativeNodeFactory.TryCreateRelic(entry.RawModel, large: false)
            : NativeNodeFactory.TryCreatePotion(entry.RawModel);

        if (nativeNode is Control nativeCtrl)
        {
            nativeCtrl.MouseFilter = Control.MouseFilterEnum.Ignore;
            nativeCtrl.SetAnchorsPreset(Control.LayoutPreset.Center);
            nativeCtrl.OffsetLeft = -IconSize / 2f;
            nativeCtrl.OffsetTop = -IconSize / 2f;
            nativeCtrl.OffsetRight = IconSize / 2f;
            nativeCtrl.OffsetBottom = IconSize / 2f;
            nativeCtrl.Modulate = banned ? new Color(0.45f, 0.45f, 0.45f, 1f) : Colors.White;
            host.AddChild(nativeCtrl);
            return host;
        }

        // 原生节点创建失败时回退到 TextureRect。
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
            host.AddChild(rect);
        }
        else
        {
            var fallback = new Label
            {
                Text = entry.Id,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            fallback.AddThemeFontSizeOverride("font_size", 10);
            fallback.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            host.AddChild(fallback);
        }
        return host;
    }

    private bool _IsBanned(string id) =>
        _currentTab == Tab.Relics ? BlacklistStore.IsRelicBanned(id) : BlacklistStore.IsPotionBanned(id);

    private void _OnCellClicked(ModelCatalog.Entry entry)
    {
        if (_currentTab == Tab.Relics) BlacklistStore.ToggleRelic(entry.Id);
        else BlacklistStore.TogglePotion(entry.Id);
        _RefreshContent();
    }

    private void _OnClearPressed()
    {
        if (_currentTab == Tab.Relics) BlacklistStore.ClearRelics();
        else BlacklistStore.ClearPotions();
        _RefreshContent();
    }

    private void _UpdateTitleAndHint(int bannedInTab, int totalInTab)
    {
        var title = _Loc("商店黑名单", "Shop Blacklist");
        var relics = _Loc("遗物", "Relics");
        var potions = _Loc("药水", "Potions");
        _titleLabel.Text = $"  {title}  ·  {relics} {BlacklistStore.RelicCount}/{ModelCatalog.Relics.Count}  {potions} {BlacklistStore.PotionCount}/{ModelCatalog.Potions.Count}";
        _hintLabel.Text = _Loc(
            $"  当前页 {bannedInTab}/{totalInTab} 已 ban；F10 切换面板",
            $"  This tab: {bannedInTab}/{totalInTab} banned; F10 toggles panel");
    }

    // ── 本地化 ────────────────────────────────────────────────────────
    private static bool? _isChineseCache;

    private static string _Loc(string zh, string en) => _IsChinese() ? zh : en;

    private static string _LocRarity(string rarity)
    {
        if (!_IsChinese()) return rarity;
        return rarity switch
        {
            "Common" => "普通",
            "Uncommon" => "罕见",
            "Rare" => "稀有",
            "Shop" => "商店",
            _ => rarity,
        };
    }

    private static Color _RarityColor(string rarity) => rarity switch
    {
        "Common" => new Color(1f, 0.92f, 0.78f, 1f),
        "Uncommon" => new Color(0.55f, 0.78f, 1f, 1f),
        "Rare" => new Color(1f, 0.84f, 0.36f, 1f),
        "Shop" => new Color(0.55f, 0.78f, 1f, 1f),
        _ => new Color(1f, 0.92f, 0.78f, 1f),
    };

    private static bool _IsChinese()
    {
        if (_isChineseCache.HasValue) return _isChineseCache.Value;
        try
        {
            var locType = HarmonyLib.AccessTools.TypeByName("MegaCrit.Sts2.Core.Localization.LocManager");
            var instance = locType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var lang = locType?.GetProperty("Language")?.GetValue(instance) as string;
            _isChineseCache = !string.IsNullOrEmpty(lang) && lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            _isChineseCache = false;
        }
        return _isChineseCache.Value;
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