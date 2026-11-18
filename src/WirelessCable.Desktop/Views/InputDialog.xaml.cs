using System.Windows;

namespace WirelessCable.Desktop.Views;

/// <summary>单行文本输入对话框。用于新建目录名、重命名等场景。</summary>
public partial class InputDialog : Window
{
    public InputDialog(string prompt, string initial = "")
    {
        InitializeComponent();
        PromptText.Text = prompt;
        InputBox.Text = initial;
        InputBox.SelectAll();
        Loaded += (_, _) => InputBox.Focus();
    }

    /// <summary>用户输入的文本。取消时为 null。</summary>
    public string? Value { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        Value = text;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Value = null;
        DialogResult = false;
        Close();
    }
}
