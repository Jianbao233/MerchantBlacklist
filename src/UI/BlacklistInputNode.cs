using Godot;

namespace MerchantBlacklist.UI;

/// <summary>
/// 全局热键监听节点（轮询式，参考 NoClientCheats.InputHandlerNode）。
/// F10 切换 BlacklistPanel 显示。
/// </summary>
public partial class BlacklistInputNode : Node
{
    public static Key ToggleKey = Key.F10;

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
        bool down = Input.IsKeyPressed(ToggleKey);
        if (down && !_prev)
        {
            BlacklistPanel.Toggle();
        }
        _prev = down;
    }
}