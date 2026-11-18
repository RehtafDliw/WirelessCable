namespace WirelessCable.Desktop.ViewModels;

/// <summary>
/// 左侧导航栏的一项。
///
/// 用数据而不是在 XAML 里写死六个按钮：激活态要跟着 Navigate 变，
/// 写死的话每加一个页面都要复制一遍触发器。
/// </summary>
public sealed class NavItemViewModel : ViewModelBase
{
    /// <summary>与 <see cref="MainViewModel.NavigateCommand"/> 的参数一致。</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }

    /// <summary>Segoe MDL2 Assets 字形码（如 \uE80F）。</summary>
    public required string Glyph { get; init; }

    private bool _isActive;

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}
