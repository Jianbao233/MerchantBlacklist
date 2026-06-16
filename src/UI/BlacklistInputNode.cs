using Godot;

namespace MerchantBlacklist.UI;

/// <summary>
/// 全局热键监听节点（轮询式保底通道）。
///
/// 实际首选通道：
///   1. ModConfig 给玩家改键 / 持久化
///   2. ritsulib RuntimeHotkeyService 注册回调（自带文本输入屏蔽等保护）
///
/// 二者任一存在时，回调里直接调 BlacklistPanel.Toggle()。本节点轮询逻辑
/// 仍然保留，作为没有 ModConfig / 没有 ritsulib 时的保底通道，避免热键完全失效。
/// 当 ritsulib 已经注册并 MarkInputHandled，该节点不会重复触发（事件被吞掉）；
/// 当只有 ModConfig 而无 ritsulib，本轮询是唯一通道。
/// </summary>
public partial class BlacklistInputNode : Node
{
    private bool _prev;

    public BlacklistInputNode()
    {
        Name = "MerchantBlacklistInput";
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Ready()
    {
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        // ritsulib 已经接管热键路由，事件层已处理；本地轮询关闭，避免重复 Toggle。
        if (HotkeyService.RouterActive) { _prev = false; return; }

        var key = HotkeyService.CurrentKey;
        bool down = key != Key.None && Input.IsKeyPressed(key);
        if (down && !_prev)
        {
            BlacklistPanel.Toggle();
        }
        _prev = down;
    }
}