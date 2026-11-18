namespace WirelessCable.Desktop.ViewModels;

/// <summary>
/// 传输任务行。显示用的字符串全部由 <see cref="TransfersViewModel"/> 算好后写入，
/// 视图不做任何格式化，避免把业务规则散到 XAML 里。
/// </summary>
public sealed class TransferRowViewModel : ViewModelBase
{
    private string _fileName = string.Empty;
    private string _subtitle = string.Empty;
    private string _detail = string.Empty;
    private string _stateText = string.Empty;
    private string _tone = "idle";
    private double _percent;
    private bool _canCancel;
    private bool _canPause;
    private bool _canResume;
    private string _errorText = string.Empty;
    private bool _hasError;
    private string? _localPath;
    private bool _canReveal;
    private bool _canDeleteRecord = true;
    private bool _isSelected;

    public required string TaskId { get; init; }

    /// <summary>upload / download。</summary>
    public required string Direction { get; init; }

    /// <summary>原始状态（waiting / transferring / completed …），分组用。</summary>
    public string State { get; set; } = string.Empty;

    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    /// <summary>「来自 设备名」/「发送到 设备名」。</summary>
    public string Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    /// <summary>「12.4 MB / 88.0 MB · 45% · 8.2 MB/s · 剩余 9 秒」。</summary>
    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public string StateText
    {
        get => _stateText;
        set => SetProperty(ref _stateText, value);
    }

    public string Tone
    {
        get => _tone;
        set => SetProperty(ref _tone, value);
    }

    public double Percent
    {
        get => _percent;
        set => SetProperty(ref _percent, value);
    }

    public bool CanCancel
    {
        get => _canCancel;
        set => SetProperty(ref _canCancel, value);
    }

    /// <summary>进行中才可暂停。</summary>
    public bool CanPause
    {
        get => _canPause;
        set => SetProperty(ref _canPause, value);
    }

    /// <summary>暂停或中断后可继续。</summary>
    public bool CanResume
    {
        get => _canResume;
        set => SetProperty(ref _canResume, value);
    }

    public string ErrorText
    {
        get => _errorText;
        set => SetProperty(ref _errorText, value);
    }

    public bool HasError
    {
        get => _hasError;
        set => SetProperty(ref _hasError, value);
    }

    /// <summary>电脑上的绝对路径；为空表示文件不在这台电脑上（或还没落盘）。</summary>
    public string? LocalPath
    {
        get => _localPath;
        set => SetProperty(ref _localPath, value);
    }

    /// <summary>能不能在资源管理器里定位到这个文件。</summary>
    public bool CanReveal
    {
        get => _canReveal;
        set => SetProperty(ref _canReveal, value);
    }

    /// <summary>还在传输的任务不能删记录：先取消，否则临时文件变孤儿。</summary>
    public bool CanDeleteRecord
    {
        get => _canDeleteRecord;
        set => SetProperty(ref _canDeleteRecord, value);
    }

    /// <summary>多选。刷新时按 TaskId 复用同一行对象，勾选状态不会丢。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsDownload => Direction == "download";
}
