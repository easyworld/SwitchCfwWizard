using System.Windows;
using System.Windows.Input;

namespace SwitchCfwWizard.SdReset;

/// <summary>
/// 与主界面同一套外观的提示框（替代 <see cref="MessageBox"/>）。
///
/// 为什么不用 MessageBox：它走 Win32，外观由**系统**决定 —— 浅色主题下是系统白，
/// 暗夜主题下也仍是系统的样子，字体也不是程序的 `Microsoft YaHei UI`。放进这个程序里
/// 一眼就能看出「不是一套东西」。这里只是个小窗口，代价远小于观感上的割裂。
/// </summary>
public partial class NoticeDialog : Window
{
    internal NoticeDialog()
    {
        InitializeComponent();
    }

    /// <summary>问「要继续吗」。返回 true = 用户选了主按钮。</summary>
    internal static bool Ask(Window? owner, string heading, string body, string primaryText, string cancelText)
    {
        var dialog = new NoticeDialog
        {
            Owner = owner,
            Title = heading,
        };

        dialog.Heading.Text = heading;
        dialog.Body.Text = body;
        dialog.PrimaryButton.Content = primaryText;
        dialog.CancelButton.Content = cancelText;
        dialog.CancelButton.Visibility = Visibility.Visible;

        return dialog.ShowDialog() == true;
    }

    /// <summary>只告知一件事（只有一个「知道了」）。</summary>
    internal static void Inform(Window? owner, string heading, string body, string okText)
    {
        var dialog = new NoticeDialog
        {
            Owner = owner,
            Title = heading,
        };

        dialog.Heading.Text = heading;
        dialog.Body.Text = body;
        dialog.PrimaryButton.Content = okText;
        dialog.CancelButton.Visibility = Visibility.Collapsed;

        dialog.ShowDialog();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
