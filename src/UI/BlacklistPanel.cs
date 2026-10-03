using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MerchantBlacklist.Core;

namespace MerchantBlacklist.UI;

/// <summary>
/// 商店黑名单面板（浮动窗）。
///
/// 形态（按用户确认的浏览器原型）：
///   1. 可拖拽 + 八向缩放的浮动窗，默认居中，尺寸受视口夹紧；
///   2. 遗物 / 药水用图标网格，卡牌用**文字列表**；
///   3. 悬停显示详情：遗物/药水走原生 tooltip，卡牌走自建**卡牌预览**；
///   4. 全局 + 各角色两层，角色层里能区分"本层 ban"与"继承自全局的 ban"。
///
/// 渲染架构（这是"不卡"的关键，也是本次重写的重点）：
///   - **节点只建一次**：所有格子/行在首次打开时建好并缓存，之后切页签、改筛选、
///     点 ban 都只改 <c>Visible</c> / stylebox / modulate，不再销毁重建（旧实现每次操作
///     都清空 <c>_content</c> 重建整表）；
///   - **分帧构建**：首次填充按帧分批插入，避免一次性几百个节点造成长卡顿；
///   - **卡牌列表虚拟滚动**：只实例化可视区附近的行，行节点复用；
///   - **搜索防抖**：输入停顿 120ms 才过滤一次；
///   - **索引**：id → 节点 / id → 数据 两张字典，查询 O(1)。
/// </summary>
public partial class BlacklistPanel : CanvasLayer
{
    // ── 尺寸 ────────────────────────────────────────────────────────────
    private const float MinWidth = 620f;
    private const float MinHeight = 460f;
    private const float MaxWidth = 1500f;
    private const float MaxHeight = 1100f;
    private const float TitleBarHeight = 38f;
    private const float EdgeThreshold = 8f;
    private const int IconSize = 72;
    private const int CellPadding = 14;
    private const int CellHeightExtra = 26;
    private const int CardRowHeight = 30;
    private const int CardHeaderHeight = 28;
    private const int CardRowBuffer = 6;          // 可视区上下各多留几行，滚动更顺
    private const double SearchDebounceSeconds = 0.12;
    private const int BuildChunkPerFrame = 40;    // 分帧构建：每帧最多插这么多节点

    // ── 配色（与原型一致：黑底金边）─────────────────────────────────────
    private static readonly Color ColGold = new(0.55f, 0.42f, 0.22f);
    private static readonly Color ColGoldBright = new(0.79f, 0.64f, 0.36f);
    private static readonly Color ColText = new(0.95f, 0.88f, 0.72f);
    private static readonly Color ColTextDim = new(0.66f, 0.60f, 0.47f);
    private static readonly Color ColTextMute = new(0.44f, 0.40f, 0.32f);
    private static readonly Color ColBan = new(1f, 0.35f, 0.35f);
    private static readonly Color ColOk = new(0.44f, 0.82f, 0.55f);
    private static readonly Color ColWindowBg = new(0.11f, 0.09f, 0.06f, 0.97f);
    private static readonly Color ColBarBg = new(0.17f, 0.13f, 0.08f, 0.97f);
    private static readonly Color ColCellBg = new(0.16f, 0.13f, 0.10f, 0.70f);
    private static readonly Color ColCellBgBanned = new(0.45f, 0.10f, 0.10f, 0.55f);
    private static readonly Color ColRowHover = new(0.55f, 0.42f, 0.22f, 0.13f);

    private static readonly string[] RelicGroups = { "Common", "Uncommon", "Rare", "Shop" };
    private static readonly string[] PotionGroups = { "Common", "Uncommon", "Rare" };
    private static readonly string[] CardRarityGroups = { "Common", "Uncommon", "Rare", "Shop" };
    private static readonly string[] ExcludedPoolTokens =
        { "Curse", "Event", "Status", "Token", "Quest", "Deprecated" };

    [Flags]
    private enum ResizeEdge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    private enum Tab { Relics = 0, Potions = 1, Cards = 2 }

    private enum Kind { Relic = 0, Potion = 1, Card = 2 }

    // ── 单例 ────────────────────────────────────────────────────────────
    private static BlacklistPanel _instance;

    public static BlacklistPanel EnsureCreated()
    {
        if (GodotObject.IsInstanceValid(_instance)) return _instance;
        _instance = new BlacklistPanel();
        var tree = Engine.GetMainLoop() as SceneTree;
        tree?.Root?.CallDeferred(Node.MethodName.AddChild, _instance);
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

    // ── 控件 ────────────────────────────────────────────────────────────
    private Panel _window;
    private Label _titleLabel;
    private Label _categoryLabel;
    private Label _banBadge;
    private Button _globalCatBtn;
    private OptionButton _charCatBtn;
    private Label _catNote;
    private readonly Button[] _tabButtons = new Button[3];
    private readonly Label[] _tabCounts = new Label[3];

    private Control _gridToolbar;
    private Control _cardToolbar;
    private LineEdit _gridSearch;
    private LineEdit _cardSearch;
    private readonly Dictionary<string, Button> _rarityChips = new();
    private readonly Dictionary<string, Button> _typeChips = new();
    private readonly Dictionary<string, Button> _cardRarityChips = new();
    private Button _gridAllowAll;
    private Button _gridBanAll;
    private Button _cardAllowAll;
    private Button _cardBanAll;

    private Control _relicPane;
    private Control _potionPane;
    private Control _cardPane;
    private ScrollContainer _relicScroll;
    private ScrollContainer _potionScroll;
    private ScrollContainer _cardScroll;
    private VBoxContainer _relicGroups;
    private VBoxContainer _potionGroups;
    private Control _cardSizer;
    private Label _relicEmpty;
    private Label _potionEmpty;
    private Label _cardEmpty;

    private Label _footerRelics;
    private Label _footerPotions;
    private Label _footerCards;
    private Label _legendInherited;
    private Label _hintLabel;

    private Timer _searchDebounce;
    private Timer _buildTimer;
    private Control _confirmOverlay;
    private Label _confirmText;
    private Action _confirmAction;
    private Label _toastLabel;
    private Timer _toastTimer;

    private PanelContainer _cardPreview;
    private Label _pvCost;
    private Label _pvName;
    private Label _pvMeta;
    private RichTextLabel _pvDesc;
    private Label _pvBanned;

    // ── 状态 ────────────────────────────────────────────────────────────
    private Tab _currentTab = Tab.Relics;
    private bool _builtOnce;
    private bool _isVisible;
    private bool _populating;
    private readonly HashSet<string> _rarityFilter = new();
    private readonly HashSet<string> _typeFilter = new();
    private readonly HashSet<string> _cardRarityFilter = new();
    private string _gridQuery = "";
    private string _cardQuery = "";

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

    private sealed class Cell
    {
        public string Id;
        public Button Node;
        public StyleBoxFlat Bg;
        public Label NameLabel;
        public Label CrossLabel;
        public Label InheritedTag;
        public Control IconHost;
    }

    private sealed class Row
    {
        public Control Node;
        public bool IsHeader;
        public Label HeaderLabel;
        public Label HeaderStat;
        public Label Cost;
        public Label Name;
        public Label Type;
        public Label Rarity;
        public Label Id;
        public Label Mark;
        public string CardId;
    }

    private readonly Dictionary<string, Cell> _relicCells = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Cell> _potionCells = new(StringComparer.Ordinal);
    private readonly List<Cell> _allRelicCells = new();
    private readonly List<Cell> _allPotionCells = new();

    private IReadOnlyList<ModelCatalog.Entry> _relics = Array.Empty<ModelCatalog.Entry>();
    private IReadOnlyList<ModelCatalog.Entry> _potions = Array.Empty<ModelCatalog.Entry>();
    private IReadOnlyList<ModelCatalog.Entry> _cards = Array.Empty<ModelCatalog.Entry>();

    /** 卡牌列表的"扁平行"模型：表头 + 卡牌行，带累计 Y 偏移，供虚拟滚动二分定位 */
    private sealed class FlatRow
    {
        public bool IsHeader;
        public string HeaderText;
        public int Count;
        public int Banned;
        public ModelCatalog.Entry Card;
        public int Offset;
    }

    private readonly List<FlatRow> _cardRows = new();
    private readonly List<FlatRow> _visibleCardRows = new();
    private int _cardContentHeight;
    private readonly Dictionary<int, Row> _rowPool = new();
    private string _cardActiveId;

    /** 撤销栈：只记录批量/清空这类破坏性操作 */
    private sealed class UndoEntry
    {
        public string Layer;
        public Kind Kind;
        public List<string> Ids;
        public bool WasBanned;
        public string Label;
    }

    private readonly List<UndoEntry> _undoStack = new();

    // ── 自检（默认完全惰性，仅 RunSelfTestPass 调用时同步构建）──────────
    internal static bool SelfTestSyncBuild;

    private int _syncBuilt;
    private int _nativeIconCount;
    private int _fallbackIconCount;
    private int _attachedCells;

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

    // ── 显示 / 隐藏 ─────────────────────────────────────────────────────
    private void _ToggleSelf()
    {
        if (_isVisible) _HideSelf();
        else _ShowSelf();
    }

    private void _ShowSelf()
    {
        if (_window == null || !GodotObject.IsInstanceValid(_window)) _BuildUI();
        if (_window == null) return;
        _AutoDetectCharacter();
        _window.Visible = true;
        _isVisible = true;
        _EnsurePopulated();
        _KickBuild();                     // 分帧构建：首次打开时逐帧插入，避免一次性建几百个节点
        _PaintAll();
    }

    private void _HideSelf()
    {
        if (_window == null) return;
        _window.Visible = false;
        _isVisible = false;
        _HideCardPreview();
    }

    private void _AutoDetectCharacter()
    {
        var charId = CharacterDetector.GetCurrentCharacterId();
        BlacklistStore.ActiveCategory = (charId != null && Array.IndexOf(CharacterDetector.KnownCharacters, charId) >= 0)
            ? charId
            : "GLOBAL";
        _UpdateCategoryButtons();
    }

    private void _UpdateCategoryButtons()
    {
        if (_globalCatBtn == null || _charCatBtn == null) return;
        bool isGlobal = BlacklistStore.ActiveCategory == "GLOBAL";
        _globalCatBtn.ButtonPressed = isGlobal;
        if (!isGlobal)
        {
            int idx = Array.IndexOf(CharacterDetector.KnownCharacters, BlacklistStore.ActiveCategory);
            if (idx >= 0 && _charCatBtn.Selected != idx) _charCatBtn.Selected = idx;
        }
        _UpdateCategoryTexts();
    }

    private static string _LayerKey() =>
        BlacklistStore.ActiveCategory == "GLOBAL" || BlacklistStore.ActiveCategory == null
            ? "GLOBAL"
            : BlacklistStore.ActiveCategory;

    /** 当前层自己的集合（点 ban 改的就是它） */
    private static bool _OwnBanned(Kind kind, string id) => kind switch
    {
        Kind.Relic => BlacklistStore.IsRelicBannedInActive(id),
        Kind.Potion => BlacklistStore.IsPotionBannedInActive(id),
        _ => BlacklistStore.IsCardBannedInActive(id),
    };

    /** 生效的 ban（全局 ∪ 当前角色） */
    private static bool _ActiveBanned(Kind kind, string id) => kind switch
    {
        Kind.Relic => BlacklistStore.IsRelicBanned(id),
        Kind.Potion => BlacklistStore.IsPotionBanned(id),
        _ => BlacklistStore.IsCardBanned(id),
    };

    private static bool _IsInherited(Kind kind, string id) =>
        _LayerKey() != "GLOBAL" && _ActiveBanned(kind, id) && !_OwnBanned(kind, id);

    // ── 拖拽 / 缩放（与早期版本一致，保留用户的浮动窗习惯）──────────────
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
            if (_isResizing) { _ApplyResize(mousePos); return; }
            if (_titleDragPending)
            {
                if (!_isDragging && _titleDragStart.DistanceTo(mousePos) > 4f)
                {
                    _isDragging = true;
                    _dragOffset = _window.Position - mousePos;
                }
                if (_isDragging) _window.Position = mousePos + _dragOffset;
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

    private bool _IsOverTitleBar(Vector2 globalPos)
    {
        if (_window == null) return false;
        var local = _window.GetGlobalTransform().AffineInverse() * globalPos;
        return local.X >= 0 && local.X <= _window.Size.X && local.Y >= 0 && local.Y < TitleBarHeight;
    }

    private ResizeEdge _DetectEdges(Vector2 localPos)
    {
        var size = _window.Size;
        var e = ResizeEdge.None;
        if (localPos.X <= EdgeThreshold) e |= ResizeEdge.Left;
        if (localPos.X >= size.X - EdgeThreshold) e |= ResizeEdge.Right;
        if (localPos.Y <= EdgeThreshold) e |= ResizeEdge.Top;
        if (localPos.Y >= size.Y - EdgeThreshold) e |= ResizeEdge.Bottom;
        return e;
    }

    private static Control.CursorShape _GetResizeCursor(ResizeEdge edges)
    {
        bool l = edges.HasFlag(ResizeEdge.Left), r = edges.HasFlag(ResizeEdge.Right);
        bool t = edges.HasFlag(ResizeEdge.Top), b = edges.HasFlag(ResizeEdge.Bottom);
        if ((l && t) || (r && b)) return Control.CursorShape.Fdiagsize;
        if ((r && t) || (l && b)) return Control.CursorShape.Bdiagsize;
        if (l || r) return Control.CursorShape.Hsize;
        if (t || b) return Control.CursorShape.Vsize;
        return Control.CursorShape.Arrow;
    }

    private void _ApplyResize(Vector2 mousePos)
    {
        var delta = mousePos - _resizeStartPos;
        float w = _resizeStartWidth, h = _resizeStartHeight;
        var pos = _resizeStartPanelPos;

        if (_resizeEdges.HasFlag(ResizeEdge.Right)) w = _resizeStartWidth + delta.X;
        if (_resizeEdges.HasFlag(ResizeEdge.Left)) { w = _resizeStartWidth - delta.X; pos.X = _resizeStartPanelPos.X + delta.X; }
        if (_resizeEdges.HasFlag(ResizeEdge.Bottom)) h = _resizeStartHeight + delta.Y;
        if (_resizeEdges.HasFlag(ResizeEdge.Top)) { h = _resizeStartHeight - delta.Y; pos.Y = _resizeStartPanelPos.Y + delta.Y; }

        w = Mathf.Clamp(w, MinWidth, MaxWidth);
        h = Mathf.Clamp(h, MinHeight, MaxHeight);
        _window.Size = new Vector2(w, h);
        _window.Position = pos;
        _ClampWindow();
    }

    private void _ClampWindow()
    {
        if (_window == null) return;
        var viewport = _window.GetViewportRect().Size;
        var size = _window.Size;
        size.X = Mathf.Min(size.X, Mathf.Max(MinWidth, viewport.X - 20f));
        size.Y = Mathf.Min(size.Y, Mathf.Max(MinHeight, viewport.Y - 20f));
        var pos = _window.Position;
        pos.X = Mathf.Clamp(pos.X, 10f, Mathf.Max(10f, viewport.X - size.X - 10f));
        pos.Y = Mathf.Clamp(pos.Y, 10f, Mathf.Max(10f, viewport.Y - size.Y - 10f));
        _window.Size = size;
        _window.Position = pos;
    }

    // ── UI 构建（只做一次）──────────────────────────────────────────────
    private void _BuildUI()
    {
        if (_builtOnce) return;
        _builtOnce = true;

        var viewport = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920f, 1080f);
        float w = Mathf.Clamp(1000f, MinWidth, Mathf.Max(MinWidth, viewport.X - 40f));
        float h = Mathf.Clamp(700f, MinHeight, Mathf.Max(MinHeight, viewport.Y - 40f));

        _window = new Panel
        {
            Name = "Window",
            Size = new Vector2(w, h),
            Position = new Vector2(Mathf.Max(10f, (viewport.X - w) / 2f), Mathf.Max(10f, (viewport.Y - h) / 2f)),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        var bgStyle = new StyleBoxFlat
        {
            BgColor = ColWindowBg,
            BorderColor = ColGold,
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
        };
        _window.AddThemeStyleboxOverride("panel", bgStyle);
        AddChild(_window);

        var root = new VBoxContainer { Name = "Root" };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        _window.AddChild(root);

        _BuildTitleBar(root);
        _BuildCategoryBar(root);
        _BuildTabBar(root);
        _BuildToolbars(root);
        _BuildPanes(root);
        _BuildFooter(root);
        _BuildConfirmOverlay();
        _BuildToast();
        _BuildCardPreview();

        _searchDebounce = new Timer { Name = "SearchDebounce", WaitTime = SearchDebounceSeconds, OneShot = true };
        _searchDebounce.Timeout += _ApplyPendingSearch;
        AddChild(_searchDebounce);

        _buildTimer = new Timer { Name = "BuildTimer", WaitTime = 0.001, OneShot = true };
        _buildTimer.Timeout += _BuildNextChunk;
        AddChild(_buildTimer);

        _ClampWindow();
    }

    private void _BuildTitleBar(Node parent)
    {
        var bar = new PanelContainer { Name = "TitleBar", CustomMinimumSize = new Vector2(0, TitleBarHeight) };
        var style = new StyleBoxFlat
        {
            BgColor = ColBarBg,
            BorderColor = new Color(ColGold, 0.45f),
            BorderWidthBottom = 1,
            ContentMarginLeft = 12, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 4,
        };
        bar.AddThemeStyleboxOverride("panel", style);
        parent.AddChild(bar);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        bar.AddChild(row);

        _titleLabel = new Label { Text = _Loc("商店黑名单", "Shop Blacklist") };
        _titleLabel.AddThemeFontSizeOverride("font_size", 16);
        _titleLabel.AddThemeColorOverride("font_color", ColGoldBright);
        row.AddChild(_titleLabel);

        _categoryLabel = new Label { Text = "" };
        _categoryLabel.AddThemeFontSizeOverride("font_size", 12);
        _categoryLabel.AddThemeColorOverride("font_color", ColTextMute);
        row.AddChild(_categoryLabel);

        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        _banBadge = new Label { Text = "" };
        _banBadge.AddThemeFontSizeOverride("font_size", 12);
        _banBadge.AddThemeColorOverride("font_color", ColTextDim);
        row.AddChild(_banBadge);

        var clearBtn = new Button { Text = _Loc("清空", "Clear"), FocusMode = Control.FocusModeEnum.All };
        clearBtn.TooltipText = _Loc("清空当前层、当前页签的黑名单（可撤销）", "Clear the current layer/tab (undoable)");
        clearBtn.Pressed += () => _OnClearPressed();
        row.AddChild(clearBtn);

        var closeBtn = new Button { Text = "✕", CustomMinimumSize = new Vector2(32, 0) };
        closeBtn.TooltipText = _Loc("关闭（F10）", "Close (F10)");
        closeBtn.Pressed += _HideSelf;
        row.AddChild(closeBtn);
    }

    private void _BuildCategoryBar(Node parent)
    {
        var bar = new PanelContainer { Name = "CategoryBar" };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.18f),
            BorderColor = new Color(ColGold, 0.22f),
            BorderWidthBottom = 1,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 6, ContentMarginBottom = 6,
        };
        bar.AddThemeStyleboxOverride("panel", style);
        parent.AddChild(bar);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        bar.AddChild(row);

        var label = new Label { Text = _Loc("生效范围", "Applies to") };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", ColTextMute);
        row.AddChild(label);

        _globalCatBtn = new Button { Text = _Loc("全局", "Global"), ToggleMode = true, ButtonPressed = true };
        _globalCatBtn.Pressed += () => { BlacklistStore.ActiveCategory = "GLOBAL"; _UpdateCategoryButtons(); _PaintAll(); };
        row.AddChild(_globalCatBtn);

        _charCatBtn = new OptionButton();
        foreach (var c in CharacterDetector.KnownCharacters) _charCatBtn.AddItem(CharacterDetector.GetCharacterDisplayName(c));
        _charCatBtn.ItemSelected += idx =>
        {
            int i = (int)idx;
            if (i < 0 || i >= CharacterDetector.KnownCharacters.Length) return;
            BlacklistStore.ActiveCategory = CharacterDetector.KnownCharacters[i];
            _UpdateCategoryButtons();
            _PaintAll();
        };
        row.AddChild(_charCatBtn);

        _catNote = new Label { Text = "" };
        _catNote.AddThemeFontSizeOverride("font_size", 11);
        _catNote.AddThemeColorOverride("font_color", ColTextMute);
        row.AddChild(_catNote);
    }

    private void _BuildTabBar(Node parent)
    {
        var bar = new HBoxContainer { Name = "TabBar" };
        bar.AddThemeConstantOverride("separation", 4);
        parent.AddChild(bar);

        var defs = new (string zh, string en)[]
        {
            ("遗物", "Relics"), ("药水", "Potions"), ("卡牌", "Cards"),
        };
        for (int i = 0; i < defs.Length; i++)
        {
            int index = i;
            var btn = new Button { Text = _Loc(defs[i].zh, defs[i].en), ToggleMode = true, ButtonPressed = i == 0 };
            btn.Pressed += () => _SwitchTab((Tab)index);
            bar.AddChild(btn);
            _tabButtons[i] = btn;

            var cnt = new Label { Text = "" };
            cnt.AddThemeFontSizeOverride("font_size", 11);
            cnt.AddThemeColorOverride("font_color", ColTextMute);
            row_add_cnt(bar, cnt);
            _tabCounts[i] = cnt;
        }

        static void row_add_cnt(Node parent, Label cnt) => parent.AddChild(cnt);
    }

    private void _BuildToolbars(Node parent)
    {
        // 网格工具条（遗物 / 药水共用）
        _gridToolbar = _BuildToolbarShell(parent, out var gridRow);
        _gridSearch = _MakeSearchBox(gridRow, _Loc("搜索名称或 ID", "Search name or id"), txt =>
        {
            _gridQuery = txt;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        });

        foreach (var r in new[] { "Common", "Uncommon", "Rare", "Shop" })
        {
            var chip = _MakeChip(_LocRarity(r), _RarityColor(r), out var dot);
            _ = dot;
            chip.Pressed += () => { _ToggleFilter(_rarityFilter, r, chip); _PaintGrids(); };
            gridRow.AddChild(chip);
            _rarityChips[r] = chip;
        }

        gridRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _gridAllowAll = _MakeActionButton(gridRow, _Loc("全启用", "Allow all"), ColOk);
        _gridAllowAll.Pressed += () => _Bulk(_CurrentKind(), ban: false);
        _gridBanAll = _MakeActionButton(gridRow, _Loc("全 ban", "Ban all"), ColBan);
        _gridBanAll.Pressed += () => _Bulk(_CurrentKind(), ban: true);

        // 卡牌工具条
        _cardToolbar = _BuildToolbarShell(parent, out var cardRow);
        _cardSearch = _MakeSearchBox(cardRow, _Loc("搜索卡牌名称、ID 或描述", "Search card name, id or text"), txt =>
        {
            _cardQuery = txt;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        });

        foreach (var t in new[] { "Attack", "Skill", "Power" })
        {
            var chip = _MakeChip(_LocCardType(t), _CardTypeColor(t), out _);
            chip.Pressed += () => { _ToggleFilter(_typeFilter, t, chip); _RebuildCardRows(); };
            cardRow.AddChild(chip);
            _typeChips[t] = chip;
        }
        foreach (var r in new[] { "Common", "Uncommon", "Rare" })
        {
            var chip = _MakeChip(_LocRarity(r), _RarityColor(r), out _);
            chip.Pressed += () => { _ToggleFilter(_cardRarityFilter, r, chip); _RebuildCardRows(); };
            cardRow.AddChild(chip);
            _cardRarityChips[r] = chip;
        }

        cardRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _cardAllowAll = _MakeActionButton(cardRow, _Loc("全启用", "Allow all"), ColOk);
        _cardAllowAll.Pressed += () => _Bulk(Kind.Card, ban: false);
        _cardBanAll = _MakeActionButton(cardRow, _Loc("全 ban", "Ban all"), ColBan);
        _cardBanAll.Pressed += () => _Bulk(Kind.Card, ban: true);

        _cardToolbar.Visible = false;
    }

    private Control _BuildToolbarShell(Node parent, out HBoxContainer row)
    {
        var bar = new PanelContainer();
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.14f),
            BorderColor = new Color(ColGold, 0.18f),
            BorderWidthBottom = 1,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 6, ContentMarginBottom = 6,
        };
        bar.AddThemeStyleboxOverride("panel", style);
        parent.AddChild(bar);
        row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        bar.AddChild(row);
        return bar;
    }

    private LineEdit _MakeSearchBox(Node parent, string placeholder, Action<string> onChanged)
    {
        var edit = new LineEdit
        {
            PlaceholderText = placeholder,
            CustomMinimumSize = new Vector2(230, 28),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        edit.TextChanged += txt => onChanged(txt);
        parent.AddChild(edit);
        return edit;
    }

    private Button _MakeChip(string text, Color tint, out Control dot)
    {
        var btn = new Button { ToggleMode = true, ButtonPressed = true };
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 4);
        var d = new ColorRect { Color = tint, CustomMinimumSize = new Vector2(7, 7), MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddChild(d);
        var lbl = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        lbl.AddThemeFontSizeOverride("font_size", 11);
        row.AddChild(lbl);
        btn.AddChild(row);
        dot = d;
        return btn;
    }

    private Button _MakeActionButton(Node parent, string text, Color tint)
    {
        var btn = new Button { Text = text };
        btn.AddThemeColorOverride("font_color", tint);
        parent.AddChild(btn);
        return btn;
    }

    private void _BuildPanes(Node parent)
    {
        var panes = new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        parent.AddChild(panes);

        // 遗物 / 药水：分组标题 + 图标网格
        (_relicPane, _relicScroll, _relicGroups, _relicEmpty) = _MakeGridPane(panes, "Relic");
        (_potionPane, _potionScroll, _potionGroups, _potionEmpty) = _MakeGridPane(panes, "Potion");
        _potionPane.Visible = false;

        // 卡牌：虚拟滚动的文字列表
        _cardPane = new Control { Name = "CardPane" };
        _cardPane.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _cardPane.Visible = false;
        panes.AddChild(_cardPane);

        _cardScroll = new ScrollContainer { Name = "CardScroll" };
        _cardScroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _cardScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        _cardPane.AddChild(_cardScroll);

        _cardSizer = new Control { Name = "CardSizer", CustomMinimumSize = new Vector2(0, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _cardScroll.AddChild(_cardSizer);

        _cardEmpty = _MakeEmptyState(_cardPane, _Loc("没有匹配的卡牌", "No matching cards"));

        _cardScroll.GetVScrollBar().ValueChanged += _ => _UpdateCardWindow();
    }

    private (Control pane, ScrollContainer scroll, VBoxContainer host, Label empty) _MakeGridPane(Node parent, string name)
    {
        var pane = new Control { Name = name + "Pane" };
        pane.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        parent.AddChild(pane);

        var scroll = new ScrollContainer { Name = name + "Scroll" };
        scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        pane.AddChild(scroll);

        var host = new VBoxContainer { Name = name + "Groups", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        host.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(host);

        var empty = _MakeEmptyState(pane, _Loc("没有匹配的条目", "No matching entries"));
        return (pane, scroll, host, empty);
    }

    private Label _MakeEmptyState(Node parent, string text)
    {
        var lbl = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        lbl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        lbl.AddThemeColorOverride("font_color", ColTextMute);
        parent.AddChild(lbl);
        return lbl;
    }

    private void _BuildFooter(Node parent)
    {
        var bar = new PanelContainer { CustomMinimumSize = new Vector2(0, 30) };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.22f),
            BorderColor = new Color(ColGold, 0.22f),
            BorderWidthTop = 1,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 3, ContentMarginBottom = 3,
        };
        bar.AddThemeStyleboxOverride("panel", style);
        parent.AddChild(bar);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        bar.AddChild(row);

        _footerRelics = _MakeFooterLabel(row);
        _footerPotions = _MakeFooterLabel(row);
        _footerCards = _MakeFooterLabel(row);
        _legendInherited = _MakeFooterLabel(row);
        _legendInherited.Text = _Loc("灰 ✕ = 继承自全局层", "grey ✕ = inherited from global");
        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _hintLabel = _MakeFooterLabel(row);
        _hintLabel.Text = _Loc("左键切换 ban · 悬停看详情 · 右键复制 ID", "Click to toggle · hover for details · right-click copies id");
    }

    private Label _MakeFooterLabel(Node parent)
    {
        var lbl = new Label();
        lbl.AddThemeFontSizeOverride("font_size", 11);
        lbl.AddThemeColorOverride("font_color", ColTextMute);
        parent.AddChild(lbl);
        return lbl;
    }

    private void _BuildConfirmOverlay()
    {
        _confirmOverlay = new Control { Name = "Confirm", Visible = false };
        _confirmOverlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _confirmOverlay.MouseFilter = Control.MouseFilterEnum.Stop;
        _window.AddChild(_confirmOverlay);

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f) };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _confirmOverlay.AddChild(dim);

        var box = new PanelContainer();
        box.SetAnchorsPreset(Control.LayoutPreset.Center);
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.14f, 0.11f, 0.07f, 0.99f),
            BorderColor = ColGold,
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 14, ContentMarginBottom = 14,
        };
        box.AddThemeStyleboxOverride("panel", style);
        _confirmOverlay.AddChild(box);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 12);
        box.AddChild(col);

        _confirmText = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(300, 0) };
        _confirmText.AddThemeFontSizeOverride("font_size", 13);
        _confirmText.AddThemeColorOverride("font_color", ColText);
        col.AddChild(_confirmText);

        var btns = new HBoxContainer();
        btns.AddThemeConstantOverride("separation", 8);
        btns.Alignment = BoxContainer.AlignmentMode.End;
        col.AddChild(btns);

        var no = new Button { Text = _Loc("取消", "Cancel") };
        no.Pressed += () => { _confirmOverlay.Visible = false; _confirmAction = null; };
        btns.AddChild(no);

        var yes = new Button { Text = _Loc("确定", "OK") };
        yes.AddThemeColorOverride("font_color", ColBan);
        yes.Pressed += () =>
        {
            var act = _confirmAction;
            _confirmOverlay.Visible = false;
            _confirmAction = null;
            act?.Invoke();
        };
        btns.AddChild(yes);
    }

    private void _BuildToast()
    {
        _toastLabel = new Label
        {
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _toastLabel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _toastLabel.OffsetLeft = -220; _toastLabel.OffsetRight = 220;
        _toastLabel.OffsetTop = -46; _toastLabel.OffsetBottom = -14;
        _toastLabel.AddThemeFontSizeOverride("font_size", 12);
        _toastLabel.AddThemeColorOverride("font_color", ColText);
        _toastLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _window.AddChild(_toastLabel);

        _toastTimer = new Timer { WaitTime = 1.6, OneShot = true };
        _toastTimer.Timeout += () => { if (_toastLabel != null) _toastLabel.Visible = false; };
        AddChild(_toastTimer);
    }

    private void _BuildCardPreview()
    {
        _cardPreview = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(240, 0) };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.14f, 0.11f, 0.07f, 0.98f),
            BorderColor = ColGold,
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 10, ContentMarginBottom = 10,
        };
        _cardPreview.AddThemeStyleboxOverride("panel", style);
        // 注意：父节点在函数末尾统一挂到 _window 上，这里不要再 AddChild（重复设父会报错）
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 6);
        _cardPreview.AddChild(col);

        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 8);
        col.AddChild(top);
        _pvCost = new Label();
        _pvCost.AddThemeFontSizeOverride("font_size", 20);
        _pvCost.AddThemeColorOverride("font_color", ColGoldBright);
        top.AddChild(_pvCost);
        _pvName = new Label();
        _pvName.AddThemeFontSizeOverride("font_size", 15);
        _pvName.AddThemeColorOverride("font_color", ColText);
        top.AddChild(_pvName);

        _pvMeta = new Label();
        _pvMeta.AddThemeFontSizeOverride("font_size", 11);
        _pvMeta.AddThemeColorOverride("font_color", ColTextMute);
        col.AddChild(_pvMeta);

        _pvDesc = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(220, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _pvDesc.AddThemeFontSizeOverride("normal_font_size", 12);
        _pvDesc.AddThemeColorOverride("default_color", new Color(0.87f, 0.82f, 0.71f));
        col.AddChild(_pvDesc);

        _pvBanned = new Label { Visible = false };
        _pvBanned.AddThemeFontSizeOverride("font_size", 11);
        _pvBanned.AddThemeColorOverride("font_color", ColBan);
        col.AddChild(_pvBanned);

        // 直接挂在 _window 上（不要再 AddChild 到 CanvasLayer，否则重复设父会报错）
        _cardPreview.Position = new Vector2(20, 60);
        _window.AddChild(_cardPreview);
    }

    // ── 首次填充（分帧）─────────────────────────────────────────────────
    private void _EnsurePopulated()
    {
        if (_relicGroups.GetChildCount() > 0 || _populating) return;
        ModelCatalog.EnsureBuilt();
        _relics = ModelCatalog.Relics;
        _potions = ModelCatalog.Potions;
        _cards = ModelCatalog.Cards;

        _BuildGridCells(Kind.Relic, _relics, RelicGroups, _relicGroups, _relicCells, _allRelicCells);
        _BuildGridCells(Kind.Potion, _potions, PotionGroups, _potionGroups, _potionCells, _allPotionCells);
        _BuildCardRowsModel();

        MerchantBlacklistLog.Info($"Panel catalog: relics={_relics.Count}, potions={_potions.Count}, cards={_cards.Count}.");
        _populating = true;
    }

    private readonly Queue<Action> _pendingBuildSteps = new();

    private void _Queue(Action step) => _pendingBuildSteps.Enqueue(step);

    private void _KickBuild()
    {
        if (_pendingBuildSteps.Count == 0) return;
        if (SelfTestSyncBuild)
        {
            while (_pendingBuildSteps.Count > 0) _pendingBuildSteps.Dequeue()();
            _syncBuilt++;
            return;
        }
        _buildTimer.Start();
    }

    private void _BuildNextChunk()
    {
        int n = 0;
        while (n < BuildChunkPerFrame && _pendingBuildSteps.Count > 0)
        {
            _pendingBuildSteps.Dequeue()();
            n++;
        }
        if (_pendingBuildSteps.Count > 0) _buildTimer.Start();
    }

    private void _BuildGridCells(Kind kind, IReadOnlyList<ModelCatalog.Entry> items,
        string[] groups, VBoxContainer host, Dictionary<string, Cell> byId, List<Cell> all)
    {
        var ordered = new List<string>(groups);
        foreach (var g in items.Select(i => i.Rarity).Distinct())
            if (!ordered.Contains(g)) ordered.Add(g);

        foreach (var rarity in ordered)
        {
            var list = items.Where(i => i.Rarity == rarity)
                            .OrderBy(i => i.Title ?? i.Id, StringComparer.Ordinal)
                            .ToList();
            if (list.Count == 0) continue;

            var header = new HBoxContainer();
            header.AddThemeConstantOverride("separation", 8);
            var hl = new Label { Text = _LocRarityGroup(rarity) };
            hl.AddThemeFontSizeOverride("font_size", 14);
            hl.AddThemeColorOverride("font_color", ColGoldBright);
            header.AddChild(hl);
            var stat = new Label { Text = "" };
            stat.AddThemeFontSizeOverride("font_size", 11);
            stat.AddThemeColorOverride("font_color", ColTextMute);
            header.AddChild(stat);
            host.AddChild(header);

            var grid = new GridContainer { Columns = 8 };
            grid.AddThemeConstantOverride("h_separation", 6);
            grid.AddThemeConstantOverride("v_separation", 6);
            host.AddChild(grid);

            foreach (var entry in list)
            {
                var cell = _MakeCell(kind, entry);
                if (cell == null) continue;
                byId[entry.Id] = cell;
                all.Add(cell);
                var captured = cell;
                _Queue(() => { grid.AddChild(captured.Node); _attachedCells++; });
            }
            _Queue(() => stat.Text = _GroupStatText(kind, list));
        }
    }

    private string _GroupStatText(Kind kind, List<ModelCatalog.Entry> list)
    {
        int banned = list.Count(e => _ActiveBanned(kind, e.Id));
        return _Loc($"已 ban {banned} / {list.Count}", $"{banned} banned / {list.Count}");
    }

    // ── 原生格子（保留原有原生观感）─────────────────────────────────────
    private Cell _MakeCell(Kind kind, ModelCatalog.Entry entry)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(IconSize + CellPadding, IconSize + CellPadding + CellHeightExtra),
            Flat = true,
            FocusMode = Control.FocusModeEnum.All,
            ToggleMode = false,
            ClipContents = false,
        };

        var bg = new StyleBoxFlat
        {
            BgColor = ColCellBg,
            BorderColor = new Color(ColGold, 0.6f),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        };
        btn.AddThemeStyleboxOverride("normal", bg);
        btn.AddThemeStyleboxOverride("hover", bg);
        btn.AddThemeStyleboxOverride("pressed", bg);
        btn.AddThemeStyleboxOverride("focus", bg);

        var iconHost = _MakeIconHost(kind, entry);
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
        nameLabel.AddThemeColorOverride("font_color", ColText);
        nameLabel.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        nameLabel.OffsetTop = -CellHeightExtra;
        nameLabel.OffsetBottom = -2;
        nameLabel.OffsetLeft = 2;
        nameLabel.OffsetRight = -2;
        btn.AddChild(nameLabel);

        var cross = new Label { Text = "✕", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        cross.AddThemeColorOverride("font_color", new Color(1f, 0.45f, 0.45f, 1f));
        cross.AddThemeFontSizeOverride("font_size", 26);
        cross.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        cross.OffsetLeft = -24;
        cross.OffsetTop = -2;
        btn.AddChild(cross);

        var inheritedTag = new Label { Text = _Loc("全局", "global"), Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        inheritedTag.AddThemeFontSizeOverride("font_size", 9);
        inheritedTag.AddThemeColorOverride("font_color", ColTextMute);
        inheritedTag.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        inheritedTag.OffsetLeft = -30; inheritedTag.OffsetTop = -16;
        inheritedTag.OffsetRight = -3; inheritedTag.OffsetBottom = -2;
        btn.AddChild(inheritedTag);

        btn.Pressed += () => _ToggleBan(kind, entry.Id);
        btn.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right)
            {
                _CopyId(entry.Id);
                btn.AcceptEvent();
            }
        };
        btn.MouseEntered += () =>
        {
            if (kind == Kind.Card) _ShowCardPreview(entry, btn);
            else _ShowEntryTip(entry, btn, kind);
        };
        btn.MouseExited += _HideCardPreview;

        return new Cell
        {
            Id = entry.Id, Node = btn, Bg = bg, NameLabel = nameLabel,
            CrossLabel = cross, InheritedTag = inheritedTag, IconHost = iconHost,
        };
    }

    private Control _MakeIconHost(Kind kind, ModelCatalog.Entry entry)
    {
        var host = new Control
        {
            CustomMinimumSize = new Vector2(IconSize, IconSize),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipContents = false,
        };
        host.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        host.OffsetLeft = CellPadding / 2f;
        host.OffsetRight = -(CellPadding / 2f);
        host.OffsetTop = 4;
        host.OffsetBottom = 4 + IconSize;

        Node nativeNode = kind == Kind.Relic
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
            host.AddChild(nativeCtrl);
            _nativeIconCount++;
            return host;
        }

        _fallbackIconCount++;
        if (entry.Icon != null)
        {
            var rect = new TextureRect
            {
                Texture = entry.Icon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(IconSize, IconSize),
                MouseFilter = Control.MouseFilterEnum.Ignore,
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

    // ── 卡牌列表：模型 + 虚拟滚动 ───────────────────────────────────────
    private void _BuildCardRowsModel()
    {
        _cardRows.Clear();
        foreach (var type in new[] { "Attack", "Skill", "Power" })
        {
            var list = _cards.Where(c => c.CardType == type)
                             .OrderBy(c => c.Title ?? c.Id, StringComparer.Ordinal)
                             .ToList();
            if (list.Count == 0) continue;
            _cardRows.Add(new FlatRow { IsHeader = true, HeaderText = _LocCardType(type), Count = list.Count });
            foreach (var c in list) _cardRows.Add(new FlatRow { Card = c });
        }
        // 非攻击/技能/能力的其它类型（例如诅咒/状态卡）兜底归到"其它"
        var others = _cards.Where(c => c.CardType != "Attack" && c.CardType != "Skill" && c.CardType != "Power")
                           .OrderBy(c => c.Title ?? c.Id, StringComparer.Ordinal).ToList();
        if (others.Count > 0)
        {
            _cardRows.Add(new FlatRow { IsHeader = true, HeaderText = _Loc("其它", "Other"), Count = others.Count });
            foreach (var c in others) _cardRows.Add(new FlatRow { Card = c });
        }
    }

    private bool _CardMatches(ModelCatalog.Entry c)
    {
        if (_typeFilter.Count > 0 && !_typeFilter.Contains(c.CardType)) return false;
        if (_cardRarityFilter.Count > 0 && !_cardRarityFilter.Contains(c.Rarity)) return false;
        if (_cardQuery.Length == 0) return true;
        var q = _cardQuery.ToLowerInvariant();
        return (c.Title ?? "").ToLowerInvariant().Contains(q)
            || c.Id.ToLowerInvariant().Contains(q);
    }

    /** 过滤 + 计算累计偏移（只算一次；滚动时零计算） */
    private void _RebuildCardRows()
    {
        _visibleCardRows.Clear();
        int y = 0;
        string currentHeader = null;
        var bucket = new List<ModelCatalog.Entry>();
        foreach (var row in _cardRows)
        {
            if (row.IsHeader)
            {
                FlushBucket();
                currentHeader = row.HeaderText;
                continue;
            }
            if (_CardMatches(row.Card)) bucket.Add(row.Card);
        }
        FlushBucket();
        _cardContentHeight = y;
        _cardSizer.CustomMinimumSize = new Vector2(0, y);
        _cardEmpty.Visible = _visibleCardRows.Count(r => !r.IsHeader) == 0;
        _UpdateCardWindow();

        void FlushBucket()
        {
            if (bucket.Count == 0) { currentHeader = null; return; }
            if (currentHeader != null)
            {
                int banned = bucket.Count(c => _ActiveBanned(Kind.Card, c.Id));
                _visibleCardRows.Add(new FlatRow { IsHeader = true, HeaderText = currentHeader, Count = bucket.Count, Banned = banned, Offset = y });
                y += CardHeaderHeight;
            }
            foreach (var c in bucket)
            {
                _visibleCardRows.Add(new FlatRow { Card = c, Offset = y });
                y += CardRowHeight;
            }
            bucket.Clear();
            currentHeader = null;
        }
    }

    /** 只实例化可视区附近的行，并复用离开视区的行 */
    private void _UpdateCardWindow()
    {
        if (_cardScroll == null || _cardSizer == null) return;
        int scroll = (int)_cardScroll.ScrollVertical;
        int viewH = (int)_cardScroll.Size.Y;

        int first = 0, last = _visibleCardRows.Count - 1, lo = 0, hi = _visibleCardRows.Count - 1;
        int minY = scroll - CardRowHeight * CardRowBuffer;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_visibleCardRows[mid].Offset < minY) { first = mid + 1; lo = mid + 1; }
            else hi = mid - 1;
        }
        int maxY = scroll + viewH + CardRowHeight * CardRowBuffer;
        for (last = first; last < _visibleCardRows.Count && _visibleCardRows[last].Offset <= maxY; last++) { }

        var want = new HashSet<int>();
        for (int i = first; i < last; i++) want.Add(i);

        foreach (var kv in _rowPool.Where(kv => !want.Contains(kv.Key)).ToList())
        {
            kv.Value.Node.QueueFree();
            _rowPool.Remove(kv.Key);
        }

        for (int i = first; i < last; i++)
        {
            var model = _visibleCardRows[i];
            if (!_rowPool.TryGetValue(i, out var row) || row.IsHeader != model.IsHeader)
            {
                if (row != null) { row.Node.QueueFree(); _rowPool.Remove(i); }
                row = model.IsHeader ? _MakeCardHeaderRow() : _MakeCardRow();
                _cardSizer.AddChild(row.Node);
                _rowPool[i] = row;
            }
            _BindRow(row, model);
        }
    }

    private Row _MakeCardHeaderRow()
    {
        var node = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        node.CustomMinimumSize = new Vector2(0, CardHeaderHeight);
        var hbox = new HBoxContainer();
        hbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        hbox.AddThemeConstantOverride("separation", 8);
        node.AddChild(hbox);

        var label = new Label();
        label.AddThemeFontSizeOverride("font_size", 13);
        label.AddThemeColorOverride("font_color", ColGoldBright);
        hbox.AddChild(label);

        var stat = new Label();
        stat.AddThemeFontSizeOverride("font_size", 11);
        stat.AddThemeColorOverride("font_color", ColTextMute);
        hbox.AddChild(stat);

        return new Row { Node = node, IsHeader = true, HeaderLabel = label, HeaderStat = stat };
    }

    private Row _MakeCardRow()
    {
        var node = new Button { Flat = true, FocusMode = Control.FocusModeEnum.All };
        node.CustomMinimumSize = new Vector2(0, CardRowHeight);
        node.AddThemeStyleboxOverride("hover", new StyleBoxFlat { BgColor = ColRowHover });
        node.AddThemeStyleboxOverride("pressed", new StyleBoxFlat { BgColor = ColRowHover });
        node.AddThemeStyleboxOverride("focus", new StyleBoxFlat { BgColor = ColRowHover });

        var hbox = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        hbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        hbox.AddThemeConstantOverride("separation", 8);
        node.AddChild(hbox);

        var cost = new Label { CustomMinimumSize = new Vector2(26, 0), HorizontalAlignment = HorizontalAlignment.Center };
        cost.AddThemeFontSizeOverride("font_size", 13);
        cost.AddThemeColorOverride("font_color", ColGoldBright);
        hbox.AddChild(cost);

        var name = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true };
        name.AddThemeFontSizeOverride("font_size", 13);
        name.AddThemeColorOverride("font_color", ColText);
        hbox.AddChild(name);

        var type = new Label { CustomMinimumSize = new Vector2(52, 0) };
        type.AddThemeFontSizeOverride("font_size", 11);
        hbox.AddChild(type);

        var rarity = new Label { CustomMinimumSize = new Vector2(44, 0), HorizontalAlignment = HorizontalAlignment.Right };
        rarity.AddThemeFontSizeOverride("font_size", 11);
        hbox.AddChild(rarity);

        var id = new Label { CustomMinimumSize = new Vector2(170, 0), HorizontalAlignment = HorizontalAlignment.Right, ClipText = true };
        id.AddThemeFontSizeOverride("font_size", 10);
        id.AddThemeColorOverride("font_color", ColTextMute);
        hbox.AddChild(id);

        var mark = new Label { CustomMinimumSize = new Vector2(18, 0), HorizontalAlignment = HorizontalAlignment.Center };
        mark.AddThemeFontSizeOverride("font_size", 14);
        hbox.AddChild(mark);

        var row = new Row { Node = node, Name = name, Cost = cost, Type = type, Rarity = rarity, Id = id, Mark = mark };

        node.Pressed += () => { if (row.CardId != null) _ToggleBan(Kind.Card, row.CardId); };
        node.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right && row.CardId != null)
            {
                _CopyId(row.CardId);
                node.AcceptEvent();
            }
        };
        node.MouseEntered += () =>
        {
            _cardActiveId = row.CardId;
            var entry = _cards.FirstOrDefault(c => c.Id == row.CardId);
            if (entry != null) _ShowCardPreview(entry, node);
        };
        node.MouseExited += _HideCardPreview;
        return row;
    }

    private void _BindRow(Row row, FlatRow model)
    {
        if (model.IsHeader)
        {
            row.HeaderLabel.Text = model.HeaderText;
            row.HeaderStat.Text = _Loc($"已 ban {model.Banned} / {model.Count}", $"{model.Banned} banned / {model.Count}");
            return;
        }

        var c = model.Card;
        row.CardId = c.Id;
        bool banned = _ActiveBanned(Kind.Card, c.Id);
        bool inherited = _IsInherited(Kind.Card, c.Id);

        row.Cost.Text = c.CardCost >= 0 ? c.CardCost.ToString() : "?";
        row.Name.Text = c.Title ?? c.Id;
        row.Name.AddThemeColorOverride("font_color", banned ? new Color(0.66f, 0.42f, 0.42f) : ColText);
        row.Type.Text = _LocCardType(c.CardType);
        row.Type.AddThemeColorOverride("font_color", _CardTypeColor(c.CardType));
        row.Rarity.Text = _LocRarity(c.Rarity);
        row.Rarity.AddThemeColorOverride("font_color", _RarityColor(c.Rarity));
        row.Id.Text = inherited ? c.Id + _Loc(" · 全局", " · global") : c.Id;
        row.Mark.Text = banned ? "✕" : "○";
        row.Mark.AddThemeColorOverride("font_color", banned ? (inherited ? ColTextMute : ColBan) : ColTextMute);
    }

    // ── 绘制（原地更新，绝不重建）──────────────────────────────────────
    private void _PaintAll()
    {
        _PaintGrids();
        _RebuildCardRows();
        _UpdateCounters();
        _UpdateCategoryTexts();
    }

    private void _PaintGrids()
    {
        _PaintGrid(Kind.Relic, _relics, _relicCells, _relicEmpty);
        _PaintGrid(Kind.Potion, _potions, _potionCells, _potionEmpty);
    }

    private void _PaintGrid(Kind kind, IReadOnlyList<ModelCatalog.Entry> items,
        Dictionary<string, Cell> byId, Label emptyState)
    {
        int shown = 0;
        foreach (var entry in items)
        {
            if (!byId.TryGetValue(entry.Id, out var cell)) continue;
            bool hit = _GridMatches(entry);
            cell.Node.Visible = hit;
            if (!hit) continue;
            shown++;

            bool banned = _ActiveBanned(kind, entry.Id);
            bool inherited = _IsInherited(kind, entry.Id);
            _ApplyCellVisual(cell, banned, inherited);
        }
        if (emptyState != null) emptyState.Visible = shown == 0;

        // 组标题的统计与"整组为空则隐藏"
        var host = kind == Kind.Relic ? _relicGroups : _potionGroups;
        foreach (var child in host.GetChildren())
        {
            if (child is not HBoxContainer header) continue;
            // 分组是「标题 HBox → 网格 GridContainer」成对插入的，靠相邻位置取网格
            var parent = header.GetParent();
            int idx = header.GetIndex();
            if (parent == null || idx < 0 || idx + 1 >= parent.GetChildCount()) continue;
            if (parent.GetChild(idx + 1) is not GridContainer grid) continue;
            int n = 0;
            foreach (var g in grid.GetChildren()) if (g is Control c && c.Visible) n++;
            header.Visible = n > 0;
            grid.Visible = n > 0;
            if (header.GetChildCount() > 1 && header.GetChild(1) is Label stat)
                stat.Text = _Loc($"显示 {n}", $"{n} shown");
        }
    }

    private void _ApplyCellVisual(Cell cell, bool banned, bool inherited)
    {
        cell.Bg.BgColor = banned ? ColCellBgBanned : ColCellBg;
        cell.Bg.BorderColor = banned
            ? (inherited ? new Color(0.66f, 0.60f, 0.47f, 0.55f) : ColBan)
            : new Color(ColGold, 0.6f);
        int bw = banned ? 2 : 1;
        cell.Bg.BorderWidthLeft = bw; cell.Bg.BorderWidthRight = bw;
        cell.Bg.BorderWidthTop = bw; cell.Bg.BorderWidthBottom = bw;

        if (cell.IconHost != null)
        {
            var modulate = banned ? (inherited ? new Color(0.55f, 0.55f, 0.55f, 1f) : new Color(0.45f, 0.45f, 0.45f, 1f)) : Colors.White;
            cell.IconHost.Modulate = modulate;
        }
        cell.NameLabel.AddThemeColorOverride("font_color", banned ? new Color(0.62f, 0.50f, 0.50f) : ColText);
        cell.CrossLabel.Visible = banned;
        cell.CrossLabel.AddThemeColorOverride("font_color",
            inherited ? new Color(0.80f, 0.77f, 0.69f, 0.55f) : new Color(1f, 0.45f, 0.45f, 1f));
        cell.InheritedTag.Visible = inherited;
        cell.Node.TooltipText = banned
            ? $"{cell.NameLabel.Text}{_Loc("（已 ban）", " (banned)")}"
            : cell.NameLabel.Text;
    }

    private bool _GridMatches(ModelCatalog.Entry entry)
    {
        if (_rarityFilter.Count > 0 && !_rarityFilter.Contains(entry.Rarity)) return false;
        if (_gridQuery.Length == 0) return true;
        var q = _gridQuery.ToLowerInvariant();
        return (entry.Title ?? "").ToLowerInvariant().Contains(q) || entry.Id.ToLowerInvariant().Contains(q);
    }

    private void _UpdateCounters()
    {
        int r = _relics.Count(e => _ActiveBanned(Kind.Relic, e.Id));
        int p = _potions.Count(e => _ActiveBanned(Kind.Potion, e.Id));
        int c = _cards.Count(e => _ActiveBanned(Kind.Card, e.Id));

        _tabCounts[0].Text = $" {r}/{_relics.Count}";
        _tabCounts[1].Text = $" {p}/{_potions.Count}";
        _tabCounts[2].Text = $" {c}/{_cards.Count}";
        _footerRelics.Text = _Loc($"遗物 {r}/{_relics.Count}", $"Relics {r}/{_relics.Count}");
        _footerPotions.Text = _Loc($"药水 {p}/{_potions.Count}", $"Potions {p}/{_potions.Count}");
        _footerCards.Text = _Loc($"卡牌 {c}/{_cards.Count}", $"Cards {c}/{_cards.Count}");
        _banBadge.Text = _Loc($"已 ban {r + p + c}", $"Banned {r + p + c}");

        // 批量按钮上标出"会影响多少项"，避免误操作
        int gridShown = _currentTab == Tab.Potions
            ? _potions.Count(_GridMatches)
            : _relics.Count(_GridMatches);
        int cardShown = _cards.Count(_CardMatches);
        _gridBanAll.Text = _Loc($"全 ban（{gridShown}）", $"Ban all ({gridShown})");
        _gridAllowAll.Text = _Loc($"全启用（{gridShown}）", $"Allow all ({gridShown})");
        _cardBanAll.Text = _Loc($"全 ban（{cardShown}）", $"Ban all ({cardShown})");
        _cardAllowAll.Text = _Loc($"全启用（{cardShown}）", $"Allow all ({cardShown})");
    }

    private void _UpdateCategoryTexts()
    {
        bool isGlobal = _LayerKey() == "GLOBAL";
        var name = isGlobal ? _Loc("全局", "Global") : CharacterDetector.GetCharacterDisplayName(_LayerKey());
        _categoryLabel.Text = isGlobal ? _Loc("全局层", "Global layer") : _Loc($"{name} 角色层", $"{name} layer");
        _catNote.Text = isGlobal
            ? _Loc("全局层对所有角色生效", "Applies to every character")
            : _Loc($"只对 {name} 生效，与全局层取并集", $"Applies to {name} only, unioned with global");
        if (_legendInherited != null) _legendInherited.Visible = !isGlobal;
    }

    // ── 交互 ────────────────────────────────────────────────────────────
    private Kind _CurrentKind() => _currentTab switch
    {
        Tab.Potions => Kind.Potion,
        Tab.Cards => Kind.Card,
        _ => Kind.Relic,
    };

    private void _SwitchTab(Tab tab)
    {
        _currentTab = tab;
        for (int i = 0; i < 3; i++) _tabButtons[i].ButtonPressed = (int)tab == i;
        _relicPane.Visible = tab == Tab.Relics;
        _potionPane.Visible = tab == Tab.Potions;
        _cardPane.Visible = tab == Tab.Cards;
        _gridToolbar.Visible = tab != Tab.Cards;
        _cardToolbar.Visible = tab == Tab.Cards;
        _hintLabel.Text = tab == Tab.Cards
            ? _Loc("左键切换 ban · 悬停看卡牌预览 · 右键复制 ID", "Click toggles · hover previews · right-click copies id")
            : _Loc("左键切换 ban · 悬停看详情 · 右键复制 ID", "Click toggles · hover details · right-click copies id");
        if (tab == Tab.Cards) _UpdateCardWindow();
        _UpdateCounters();
        _HideCardPreview();
    }

    private void _ToggleBan(Kind kind, string id)
    {
        if (_IsInherited(kind, id))
        {
            _Toast(_Loc("该条目由全局层 ban；切到「全局」才能取消", "Banned by the global layer; switch to Global to un-ban"));
            return;
        }
        switch (kind)
        {
            case Kind.Relic: BlacklistStore.ToggleRelic(id); break;
            case Kind.Potion: BlacklistStore.TogglePotion(id); break;
            default: BlacklistStore.ToggleCard(id); break;
        }
        // 只重画受影响的范围：格子是原地改样式，卡牌列表只重绑可见行
        if (kind == Kind.Card) { _RebuildCardRows(); }
        else _PaintGrid(kind, kind == Kind.Relic ? _relics : _potions,
                        kind == Kind.Relic ? _relicCells : _potionCells,
                        kind == Kind.Relic ? _relicEmpty : _potionEmpty);
        _UpdateCounters();
    }

    private void _ToggleFilter(HashSet<string> set, string key, Button chip)
    {
        if (set.Contains(key)) set.Remove(key); else set.Add(key);
        chip.ButtonPressed = set.Contains(key);
    }

    private void _ApplyPendingSearch()
    {
        if (_currentTab == Tab.Cards) _RebuildCardRows();
        else _PaintGrids();
        _UpdateCounters();
    }

    private void _Bulk(Kind kind, bool ban)
    {
        var layer = _LayerKey();
        var changed = new List<string>();
        var items = kind == Kind.Relic ? _relics : kind == Kind.Potion ? _potions : _cards;
        foreach (var e in items)
        {
            bool inView = kind == Kind.Card ? _CardMatches(e) : _GridMatches(e);
            if (!inView) continue;
            bool now = _OwnBanned(kind, e.Id);
            if (ban == now) continue;
            switch (kind)
            {
                case Kind.Relic: BlacklistStore.ToggleRelic(e.Id); break;
                case Kind.Potion: BlacklistStore.TogglePotion(e.Id); break;
                default: BlacklistStore.ToggleCard(e.Id); break;
            }
            changed.Add(e.Id);
        }
        if (changed.Count > 0)
        {
            _undoStack.Add(new UndoEntry
            {
                Layer = layer, Kind = kind, Ids = changed, WasBanned = !ban,
                Label = _Loc($"{(ban ? "批量 ban" : "批量启用")} {changed.Count} 项",
                             $"{(ban ? "bulk ban" : "bulk allow")} {changed.Count}"),
            });
            if (_undoStack.Count > 20) _undoStack.RemoveAt(0);
        }
        _PaintAll();
        _Toast(changed.Count > 0
            ? _Loc($"{(ban ? "已 ban" : "已启用")} {changed.Count} 项", $"{(ban ? "Banned" : "Allowed")} {changed.Count}")
            : _Loc("没有可改动的条目", "Nothing to change"));
    }

    private void _OnClearPressed()
    {
        var kind = _CurrentKind();
        var layer = _LayerKey();
        var ids = new List<string>();
        var items = kind == Kind.Relic ? _relics : kind == Kind.Potion ? _potions : _cards;
        foreach (var e in items) if (_OwnBanned(kind, e.Id)) ids.Add(e.Id);
        if (ids.Count == 0) { _Toast(_Loc("当前层这一栏本来就是空的", "Already empty")); return; }

        _confirmText.Text = _Loc(
            $"确定清空「{(layer == "GLOBAL" ? "全局层" : CharacterDetector.GetCharacterDisplayName(layer) + " 角色层")}」的{_KindName(kind)}黑名单？共 {ids.Count} 项，其它层不受影响。可撤销。",
            $"Clear {ids.Count} {_KindName(kind)} entries in this layer? Other layers are untouched. Undoable.");
        _confirmAction = () =>
        {
            switch (kind)
            {
                case Kind.Relic: BlacklistStore.ClearRelics(); break;
                case Kind.Potion: BlacklistStore.ClearPotions(); break;
                default: BlacklistStore.ClearCards(); break;
            }
            _undoStack.Add(new UndoEntry { Layer = layer, Kind = kind, Ids = ids, WasBanned = true, Label = _Loc($"清空 {ids.Count} 项", $"cleared {ids.Count}") });
            _PaintAll();
            _Toast(_Loc($"已清空 {ids.Count} 项（可撤销）", $"Cleared {ids.Count} (undoable)"));
        };
        _confirmOverlay.Visible = true;
    }

    private void _UndoLast()
    {
        if (_undoStack.Count == 0) { _Toast(_Loc("没有可撤销的操作", "Nothing to undo")); return; }
        var e = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        var saved = BlacklistStore.ActiveCategory;
        BlacklistStore.ActiveCategory = e.Layer;
        foreach (var id in e.Ids)
        {
            bool now = _OwnBanned(e.Kind, id);
            if (now == e.WasBanned) continue;
            switch (e.Kind)
            {
                case Kind.Relic: BlacklistStore.ToggleRelic(id); break;
                case Kind.Potion: BlacklistStore.TogglePotion(id); break;
                default: BlacklistStore.ToggleCard(id); break;
            }
        }
        BlacklistStore.ActiveCategory = saved;
        _PaintAll();
        _Toast(_Loc($"已撤销：{e.Label}", $"Undone: {e.Label}"));
    }

    private void _CopyId(string id)
    {
        DisplayServer.ClipboardSet(id);
        _Toast(_Loc($"已复制 ID：{id}", $"Copied id: {id}"));
    }

    private void _Toast(string text)
    {
        if (_toastLabel == null) return;
        _toastLabel.Text = text;
        _toastLabel.Visible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private static string _KindName(Kind kind) => kind switch
    {
        Kind.Relic => "遗物",
        Kind.Potion => "药水",
        _ => "卡牌",
    };

    // ── 悬停提示 ────────────────────────────────────────────────────────
    private IDisposable _entryTip;

    private void _ShowEntryTip(ModelCatalog.Entry entry, Control owner, Kind kind)
    {
        _entryTip?.Dispose();
        _entryTip = NativeNodeFactory.ShowHoverTip(owner, entry.RawModel, kind == Kind.Relic);
    }

    private void _ShowCardPreview(ModelCatalog.Entry entry, Control owner)
    {
        if (_cardPreview == null || entry == null) return;
        _pvCost.Text = entry.CardCost >= 0 ? entry.CardCost.ToString() : "?";
        _pvName.Text = entry.Title ?? entry.Id;
        _pvMeta.Text = $"{_LocCardType(entry.CardType)} · {_LocRarity(entry.Rarity)} · {entry.Id}";
        _pvDesc.Text = string.IsNullOrEmpty(entry.Description) ? _Loc("（无描述）", "(no description)") : entry.Description;
        bool banned = _ActiveBanned(Kind.Card, entry.Id);
        _pvBanned.Visible = banned;
        _pvBanned.Text = _IsInherited(Kind.Card, entry.Id)
            ? _Loc("该卡由全局层 ban", "Banned by the global layer")
            : _Loc("该卡已被 ban", "This card is banned");

        _cardPreview.Visible = true;
        var mouse = _window.GetLocalMousePosition();
        var size = _cardPreview.Size;
        float x = mouse.X + 18f, y = mouse.Y + 12f;
        var viewport = _window.Size;
        if (x + size.X > viewport.X - 8f) x = mouse.X - size.X - 18f;
        if (y + size.Y > viewport.Y - 8f) y = viewport.Y - size.Y - 8f;
        _cardPreview.Position = new Vector2(Mathf.Max(8f, x), Mathf.Max(8f, y));
    }

    private void _HideCardPreview()
    {
        if (_cardPreview != null) _cardPreview.Visible = false;
        _entryTip?.Dispose();
        _entryTip = null;
    }

    // ── 本地化 / 颜色助手 ───────────────────────────────────────────────
    private static string _Loc(string zh, string en) => _IsChinese() ? zh : en;

    private static string _LocRarity(string rarity) => rarity switch
    {
        "Common" => _Loc("普通", "Common"),
        "Uncommon" => _Loc("罕见", "Uncommon"),
        "Rare" => _Loc("稀有", "Rare"),
        "Shop" => _Loc("商店", "Shop"),
        "Basic" => _Loc("初始", "Basic"),
        "Ancient" => _Loc("先古", "Ancient"),
        "Event" => _Loc("事件", "Event"),
        _ => rarity,
    };

    private static string _LocRarityGroup(string rarity) => _LocRarity(rarity);

    private static Color _RarityColor(string rarity) => rarity switch
    {
        "Common" => new Color(0.73f, 0.70f, 0.64f),
        "Uncommon" => new Color(0.35f, 0.66f, 0.90f),
        "Rare" => new Color(0.91f, 0.75f, 0.29f),
        "Shop" => new Color(0.44f, 0.82f, 0.55f),
        "Basic" => new Color(0.60f, 0.60f, 0.60f),
        _ => ColTextMute,
    };

    private static string _LocCardType(string cardType) => cardType switch
    {
        "Attack" => _Loc("攻击", "Attack"),
        "Skill" => _Loc("技能", "Skill"),
        "Power" => _Loc("能力", "Power"),
        "Curse" => _Loc("诅咒", "Curse"),
        "Status" => _Loc("状态", "Status"),
        _ => cardType,
    };

    private static Color _CardTypeColor(string cardType) => cardType switch
    {
        "Attack" => new Color(0.88f, 0.42f, 0.35f),
        "Skill" => new Color(0.44f, 0.66f, 0.86f),
        "Power" => new Color(0.73f, 0.55f, 0.88f),
        _ => ColTextMute,
    };

    private static bool _IsChinese()
    {
        try
        {
            var locType = _FindType("MegaCrit.Sts2.Core.Localization.LocManager");
            var instance = locType?.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
            var lang = locType?.GetProperty("Language")?.GetValue(instance) as string;
            return !string.IsNullOrEmpty(lang) && lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /** 游戏类型不在本程序集里，得遍历已加载的程序集去找 */
    private static Type _FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            catch { /* 某些程序集反射会抛，跳过 */ }
        }
        return null;
    }

    // ── 自检入口（无头回归用；不设环境变量时不会被调用）──────────────────
    internal static void RunSelfTestPass()
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree?.Root == null) { MerchantBlacklistLog.Warn("[SelfTest] no SceneTree root; skip panel pass"); return; }

        SelfTestSyncBuild = true;
        var panel = new BlacklistPanel();
        tree.Root.AddChild(panel);          // 同步入树 → _Ready → _BuildUI

        panel._SelfTestReport("built");
        panel._ShowSelf();                  // 触发首次填充（同步模式，因为 SelfTestSyncBuild=true）
        panel._SelfTestReport("populated");

        panel._SwitchTab(Tab.Cards);
        panel._SelfTestReport("cards");
        panel._SwitchTab(Tab.Potions);
        panel._SelfTestReport("potions");
        panel._SwitchTab(Tab.Relics);
        panel._SelfTestReport("relics");

        // 搜索 + 筛选 + ban 切换各走一遍，确认不炸
        panel._gridQuery = "剑";
        panel._PaintGrids();
        panel._SelfTestReport("search");
        panel._gridQuery = "";
        panel._PaintGrids();

        if (panel._relics.Count > 0)
        {
            panel._ToggleBan(Kind.Relic, panel._relics[0].Id);
            panel._ToggleBan(Kind.Relic, panel._relics[0].Id);   // 立刻还原，保持只读
        }
        panel._SelfTestReport("toggled");

        panel._HideSelf();
        panel.QueueFree();
        SelfTestSyncBuild = false;
        MerchantBlacklistLog.Info("[SelfTest] panel pass done (sync).");
    }

    // ── 分帧构建路径的自检（用户实际走的就是这条：Timer 逐帧插入）──────────
    private static BlacklistPanel _asyncProbe;

    internal static void RunSelfTestAsyncPassBegin()
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree?.Root == null) return;
        SelfTestSyncBuild = false;
        _asyncProbe = new BlacklistPanel();
        tree.Root.AddChild(_asyncProbe);
        _asyncProbe._ShowSelf();            // 这里会启动 _buildTimer，逐帧插入
        MerchantBlacklistLog.Info("[SelfTest] async pass: panel shown; waiting for chunked build…");
    }

    internal static void RunSelfTestAsyncPassReport()
    {
        if (_asyncProbe == null) { MerchantBlacklistLog.Warn("[SelfTest] async pass: no panel"); return; }
        _asyncProbe._SelfTestReport("async");
        int expectedCells = _asyncProbe._relics.Count + _asyncProbe._potions.Count;
        if (_asyncProbe._attachedCells == expectedCells)
            MerchantBlacklistLog.Info($"[SelfTest] async pass OK: 分帧构建已插入全部 {expectedCells} 个格子");
        else
            MerchantBlacklistLog.Warn($"[SelfTest] async pass: 只插入 {_asyncProbe._attachedCells}/{expectedCells} 个格子（分帧未跑完？）");
        _asyncProbe._HideSelf();
        _asyncProbe.QueueFree();
        _asyncProbe = null;
        MerchantBlacklistLog.Info("[SelfTest] async pass done.");
    }

    private void _SelfTestReport(string stage)
    {
        int relicCells = _allRelicCells.Count;
        int potionCells = _allPotionCells.Count;
        int cardRows = _visibleCardRows.Count(r => !r.IsHeader);
        int pooled = _rowPool.Count;
        int colorless = _cards.Count(c => c.IsColorless);
        int relicVisible = _allRelicCells.Count(c => c.Node.Visible);
        int potionVisible = _allPotionCells.Count(c => c.Node.Visible);
        MerchantBlacklistLog.Info(
            $"[SelfTest] panel/{stage}: relics={_relics.Count}(cells={relicCells},visible={relicVisible}) " +
            $"potions={_potions.Count}(cells={potionCells},visible={potionVisible}) cards={_cards.Count} " +
            $"cardRows={cardRows}(pooled={pooled}) colorless={colorless} " +
            $"nativeIcons={_nativeIconCount} fallbackIcons={_fallbackIconCount} " +
            $"tab={_currentTab} syncBuilt={_syncBuilt}");
    }
}
