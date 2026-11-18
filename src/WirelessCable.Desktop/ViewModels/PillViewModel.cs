namespace WirelessCable.Desktop.ViewModels;

/// <summary>状态胶囊的数据：文字 + 色调（ok / idle / error）。</summary>
public sealed class PillViewModel
{
    public required string Text { get; init; }
    public required string Tone { get; init; }

    public static PillViewModel Idle(string text) => new() { Text = text, Tone = "idle" };
    public static PillViewModel Ok(string text) => new() { Text = text, Tone = "ok" };
    public static PillViewModel Error(string text) => new() { Text = text, Tone = "error" };
}
