using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Localization;
using SwitchCfwWizard.ViewModels;

namespace SwitchCfwWizard.SdReset;

/// <summary>
/// 「把刚生成的 out 复制进 TF 卡」的窗口（用户 2026-11-20 要求）。
///
/// 顺序是硬性要求：**先删（只留 Nintendo / emuMMC / JKSV），再复制**。
/// 判定规则在 <see cref="CardCopyPlan"/>、文件操作在 <see cref="CardCopyRunner"/>，
/// 这里只负责「问清楚 + 显示进度 + 把每一步写进运行日志」。
///
/// 与「格式化与重建虚拟系统」的区别：这里**不碰分区表、不需要管理员权限**，
/// 只是普通文件操作，所以既不用 runas 子进程，也不用锁卷。
/// </summary>
public partial class CardCopyWindow : Window
{
    private readonly MainViewModel _viewModel;

    private List<SdDevice> _devices = new();
    private List<CardEntry> _entries = new();
    private SdDevice? _device;
    private string? _root;
    private bool _busy;

    /// <summary>
    /// 复制已经成功走完一遍（用户 2026-11-20 要求）：此时右下角那个主按钮
    /// **变成「弹出 TF 卡」**，设备下拉与「直接复制」勾选一并锁住 ——
    /// 这次操作已经结束，再让用户改设备只会让人以为「还要再复制一次」。
    /// </summary>
    private bool _copied;

    /// <summary>已经安全弹出过。按钮定在「已安全弹出」且不可点，直到这张卡又被重新绑上。</summary>
    private bool _ejected;

    public CardCopyWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        var loc = LocalizationService.Instance;
        ContentHint.Text = string.Format(
            loc["CardCopy.Content.Hint"], string.Join("、", CardCopyPlan.KeepNames));
        StatusText.Text = loc["CardCopy.Status.Ready"];
        ApplyStartButton();

        Loaded += async (_, _) => await RefreshAsync();
    }

    private void Log(string message, LogLevel level = LogLevel.Info)
        => _viewModel.Log(level, "[复制] " + message);

    private static string FormatBytes(long bytes) => CardCopyRunner.FormatBytes(bytes);

    /// <summary>「不删除卡上文件与文件夹，直接复制」（用户 2026-11-20 要求）。</summary>
    private bool SkipClear => SkipClearCheck.IsChecked == true;

    /// <summary>
    /// 右下角那个按钮现在是什么：复制前/复制中 → 复制按钮；复制完 → 弹出按钮。
    ///
    /// 单独抽出来是因为它有**三个**写入点（构造、清单刷新、批量禁用），
    /// 分散写就会有一个地方漏掉，于是按钮文案和它实际干的事对不上。
    /// </summary>
    private void ApplyStartButton()
    {
        var loc = LocalizationService.Instance;

        // 已经弹出过：定在终态。卡真的走了就别再给一个指向空气的「弹出」按钮。
        if (_ejected)
        {
            StartButton.Content = loc["CardCopy.Button.Ejected"];
            StartButton.Style = (Style)FindResource("SecondaryButton");
            StartButton.IsEnabled = false;
            return;
        }

        // ✅ 两个字面量各写一次：语言键检查器按源码里的字面量对账，
        //    写成 `loc[cond ? "A" : "B"]` 会让两个键都被判成没人引用的死键。
        StartButton.Content = _copied
            ? loc["CardCopy.Button.Eject"]
            : SkipClear ? loc["CardCopy.Button.CopySkip"] : loc["CardCopy.Button.Copy"];

        // 复制完那个按钮已经**不是**主操作了（主操作是「关闭」/拔卡），降成次要样式，
        // 免得用户以为还得再点一次。
        StartButton.Style = (Style)FindResource(_copied ? "SecondaryButton" : "PrimaryButton");
        StartButton.IsEnabled = !_busy && (_copied || _root is not null);
    }

    // ── ① 设备枚举 ────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        var loc = LocalizationService.Instance;
        SetBusy(true);
        DeviceDetails.Text = loc["CardCopy.Device.Scanning"];

        try
        {
            // 枚举要访问物理设备，必须离开 UI 线程（读盘慢时界面会僵住）。
            var all = await Task.Run(SdDisks.Scan);

            // 只列**有盘符**的：没有盘符就写不进文件，列出来只会让人白选一次。
            _devices = all.Where(device => device.MountPoints.Length > 0).ToList();
            DevicePicker.ItemsSource = _devices;

            if (_devices.Count == 0)
            {
                DevicePicker.SelectedItem = null;
                DeviceDetails.Text = all.Count == 0
                    ? loc["CardCopy.Device.Empty"]
                    : loc["CardCopy.Device.NoLetter"];
                _device = null;
                _root = null;
                _entries = new List<CardEntry>();
                EntryList.ItemsSource = null;
                // 注意：这里**不能**显示 EmptyHint —— 它说的是「这张卡是空的」，
                // 而现在是「没有卡」。两句话意思完全不同，混着说会让人以为卡插上了但没东西。
                EmptyHint.Visibility = Visibility.Collapsed;
                // 用统一的入口收尾：卡没了，但如果是刚弹出过，按钮要留在「已安全弹出」而不是
                // 变回一个灰着的「清空并复制」—— 那会让人以为刚才的弹出没生效。
                ApplyStartButton();
                Log("没有检测到可写入的 TF 卡"
                    + (all.Count > 0 ? $"（{all.Count} 个设备没有盘符）" : string.Empty));
                return;
            }

            // 与「格式化」窗口一致：优先展示第一张卡，但仍可切换。
            DevicePicker.SelectedItem = _devices[0];
            DeviceDetails.Text = string.Format(loc["CardCopy.Device.Count"], _devices.Count);
            Log($"检测到 {_devices.Count} 个可写入设备："
                + string.Join(" | ", _devices.Select(device => device.Display)));
        }
        catch (Exception ex)
        {
            DeviceDetails.Text = loc["CardCopy.Device.Failed"] + ServerAddressPrivacy.Hide(ex.Message);
            Log("检测 TF 卡失败：" + ServerAddressPrivacy.Hide(ex.Message), LogLevel.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DevicePicker.SelectedItem is not SdDevice device)
        {
            _device = null;
            _root = null;
            _entries = new List<CardEntry>();
            EntryList.ItemsSource = null;
            StartButton.IsEnabled = false;
            return;
        }

        BindDevice(device);
    }

    private void BindDevice(SdDevice device)
    {
        var loc = LocalizationService.Instance;
        _device = device;
        _root = device.MountPoints[0];

        // 卡又出现了（有的读卡器弹不掉，或者用户换了另一张卡）：回到「可以复制」的状态。
        _ejected = false;

        ReloadEntries();

        var deleteCount = _entries.Count(entry => !entry.Keep);
        Log($"已选择 {device.Display}（{_root}）：{_entries.Count} 项"
            + (SkipClear
                ? "，按「直接复制」处理：一项都不删"
                : $"，其中将删除 {deleteCount} 项、保留 {_entries.Count - deleteCount} 项"));
    }

    /// <summary>
    /// 按当前的「清空方式」重新读一遍卡根，并刷新清单、说明与按钮文案。
    ///
    /// 勾上「不删除」之后清单必须**跟着变成全部保留** —— 否则用户看到一屏「将删除」
    /// 却一件都没删，会以为程序没按他的意思执行。
    /// </summary>
    private void ReloadEntries()
    {
        var loc = LocalizationService.Instance;

        if (_root is null)
        {
            _entries = new List<CardEntry>();
            EntryList.ItemsSource = null;
            StartButton.IsEnabled = false;
            return;
        }

        try
        {
            _entries = CardCopyRunner.ListRoot(_root, SkipClear);
        }
        catch (Exception ex)
        {
            _entries = new List<CardEntry>();
            DeviceDetails.Text = loc["CardCopy.Device.Unreadable"] + ServerAddressPrivacy.Hide(ex.Message);
            Log($"读取 {_root} 失败：{ServerAddressPrivacy.Hide(ex.Message)}", LogLevel.Warning);
        }

        EntryList.ItemsSource = _entries;
        EmptyHint.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var deleteCount = _entries.Count(entry => !entry.Keep);
        ContentHint.Text = SkipClear
            ? string.Format(loc["CardCopy.Content.Hint.Skip"], _entries.Count)
            : string.Format(loc["CardCopy.Content.Hint"], string.Join("、", CardCopyPlan.KeepNames))
              + "\n" + string.Format(loc["CardCopy.Content.Counts"], deleteCount, _entries.Count - deleteCount);

        ApplyStartButton();
    }

    private void SkipClear_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _busy)
        {
            // InitializeComponent 里 IsChecked="False" 不触发，这里是防御：真触发了也还没设备。
            // 复制途中也不能再改（_busy）：那会让「按哪种方式跑的」和界面显示的两回事。
            return;
        }

        ReloadEntries();
        Log(SkipClear
            ? "已切换为「直接复制」：卡上现有文件与文件夹全部保留"
            : "已切换回「清空后复制」：只保留 Nintendo、emuMMC、JKSV");
    }


    // ── ② 清空 + 复制 ─────────────────────────────────────────────

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        // 复制完这个按钮已经改名「弹出 TF 卡」了 —— 同一个控件、两件事，
        // 分开两个 XAML 按钮会多出一个「该显示哪个」的判断，反而更容易错。
        if (_copied)
        {
            await EjectAsync();
            return;
        }

        if (_busy || _device is null || _root is null)
        {
            return;
        }

        var loc = LocalizationService.Instance;
        var skip = SkipClear;
        var root = _root;
        var toDelete = skip ? new List<CardEntry>() : _entries.Where(entry => !entry.Keep).ToList();
        var keepCount = _entries.Count - toDelete.Count;

        if (skip)
        {
            // 「直接复制」不删任何东西，但**会覆盖同名文件** —— 这一步同样不可逆，照样问一句。
            var body = string.Format(loc["CardCopy.Confirm.Skip.Body"], root, _entries.Count);
            if (!NoticeDialog.Ask(this, loc["CardCopy.Confirm.Skip.Title"], body,
                    loc["CardCopy.Confirm.Skip.Ok"], loc["CardCopy.Button.Close"]))
            {
                Log("用户取消了直接复制");
                return;
            }
        }
        else if (toDelete.Count > 0)
        {
            var preview = string.Join("\n", toDelete.Take(10).Select(entry => "· " + entry.Name));
            if (toDelete.Count > 10)
            {
                preview += "\n" + string.Format(loc["CardCopy.Confirm.More"], toDelete.Count - 10);
            }

            var body = string.Format(
                loc["CardCopy.Confirm.Body"], toDelete.Count, keepCount, root, preview);

            if (!NoticeDialog.Ask(this, loc["CardCopy.Confirm.Title"], body,
                    loc["CardCopy.Confirm.Ok"], loc["CardCopy.Button.Close"]))
            {
                Log("用户取消了清空与复制");
                return;
            }
        }

        SetBusy(true);
        CopyProgress.Value = 0;
        ResultPanel.Visibility = Visibility.Collapsed;

        try
        {
            await ExecuteAsync(keepCount, clear: !skip);
        }
        catch (Exception ex)
        {
            var message = ServerAddressPrivacy.Hide(ex.Message);
            Error(message);
            Log(message, LogLevel.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ExecuteAsync(int keepCount, bool clear)
    {
        var loc = LocalizationService.Instance;
        var root = _root!;
        var outputRoot = AppPaths.OutputRoot;

        if (!Directory.Exists(outputRoot))
        {
            throw new IOException(string.Format(loc["CardCopy.Error.OutputMissing"], outputRoot));
        }

        var (sourceFiles, sourceBytes) = await Task.Run(() => CardCopyRunner.Measure(outputRoot));
        if (sourceFiles == 0)
        {
            throw new IOException(string.Format(loc["CardCopy.Error.OutputEmpty"], outputRoot));
        }

        var failed = new List<string>();

        if (clear)
        {
            // ── 删除 ──（项数不多时进度条无意义，用不确定态表示「正在做」）
            StatusText.Text = loc["CardCopy.Progress.DeleteAll"];
            CopyProgress.IsIndeterminate = true;

            var cleared = await Task.Run(() => CardCopyRunner.Clear(root));

            CopyProgress.IsIndeterminate = false;
            CopyProgress.Value = 0;

            foreach (var (name, error) in cleared.Failed)
            {
                Log($"未能删除 {name}：{ServerAddressPrivacy.Hide(error)}", LogLevel.Warning);
            }

            failed = cleared.Failed.Select(entry => entry.Name).ToList();

            Log($"已清空 {cleared.Deleted} 项，保留 {keepCount} 项"
                + (cleared.Failed.Count > 0 ? $"，{cleared.Failed.Count} 项未能删除" : string.Empty));
        }
        else
        {
            // 「直接复制」：一项都不删，卡上原有内容原样留着（同名文件会被覆盖）。
            StatusText.Text = loc["CardCopy.Progress.SkipCopy"];
            Log($"按「直接复制」处理：保留卡上现有 {keepCount} 项，不清空");
        }

        // ── 空间 ──（删完再算，才反映腾出来之后的情况）
        StatusText.Text = loc["CardCopy.Progress.Space"];

        var required = CardCopyPlan.RequiredFreeBytes(sourceBytes);
        var free = await Task.Run(() => new DriveInfo(root).AvailableFreeSpace);
        if (free < required)
        {
            throw new IOException(string.Format(
                loc["CardCopy.Error.NoSpace"], FormatBytes(required), FormatBytes(free))
                + (clear ? string.Empty : "\n" + loc["CardCopy.Error.NoSpace.Skip"]));
        }

        // ── 复制 ──
        var progress = new Progress<(double Percent, string Relative, int Index, int Total)>(report =>
        {
            CopyProgress.Value = report.Percent;
            StatusText.Text = string.Format(
                loc["CardCopy.Progress.Copy"], report.Relative, report.Index, report.Total);
        });

        CardCopyRunner.CopyResult result;
        try
        {
            result = await Task.Run(() => CardCopyRunner.Copy(outputRoot, root, progress));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Runner 抛的是「目标不在卡内」这类致命错误，但消息是中文硬编码；
            // 换成本地化的一句，具体路径写进日志。
            Log("复制失败：" + ServerAddressPrivacy.Hide(ex.Message), LogLevel.Error);
            throw new IOException(string.Format(loc["CardCopy.Error.Outside"], root));
        }

        CopyProgress.Value = 100;
        Complete(result.Files, result.Bytes, root, failed);
    }

    // ── 收尾 ──────────────────────────────────────────────────────

    private void SetBusy(bool busy)
    {
        _busy = busy;

        // 复制完之后设备下拉/方式勾选/重新检测都锁住：这次操作已经结束，
        // 还能改设备的话，按钮上写着「弹出 TF 卡」、实际却指向另一张卡。
        var idle = !busy && !_copied;
        RefreshButton.IsEnabled = idle;
        DevicePicker.IsEnabled = idle;
        SkipClearCheck.IsEnabled = idle;
        CloseButton.IsEnabled = !busy;
        ApplyStartButton();
    }

    private void Complete(int copied, long bytes, string root, List<string> failed)
    {
        var loc = LocalizationService.Instance;

        ResultTitle.Text = loc["CardCopy.Done.Title"];
        ResultTitle.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");

        var body = string.Format(loc["CardCopy.Done.Body"], copied, FormatBytes(bytes), root);
        if (failed.Count > 0)
        {
            body += "\n\n" + string.Format(loc["CardCopy.Done.Partial"], failed.Count)
                    + "\n" + string.Join("\n", failed.Take(6).Select(name => "· " + name));
        }

        ResultBody.Text = body;
        ResultPanel.Visibility = Visibility.Visible;
        StatusText.Text = loc["CardCopy.Done.Status"];

        Log($"复制完成：{copied} 个文件（{FormatBytes(bytes)}）→ {root}", LogLevel.Success);
        if (failed.Count > 0)
        {
            Log($"有 {failed.Count} 项未能删除，已在上方结果里列出", LogLevel.Warning);
        }

        // 复制成功 ⇒ 右下角按钮变成「弹出 TF 卡」（用户 2026-11-20 要求）。
        _copied = true;
        ApplyStartButton();
    }

    // ── ③ 弹出 TF 卡 ──────────────────────────────────────────────

    /// <summary>
    /// 复制完成之后的安全弹出（走 <see cref="SafeEject"/>，与「格式化与重建虚拟系统」同一套实现：
    /// 锁卷 → 卸载 → 弹出介质）。失败不静默：卡还在挂着，用户以为拔了就安全，那才是最坏的情况。
    /// </summary>
    private async Task EjectAsync()
    {
        if (_busy || _root is null)
        {
            return;
        }

        var loc = LocalizationService.Instance;
        var root = _root;

        SetBusy(true);
        StatusText.Text = loc["CardCopy.Eject.Busy"];
        var ejected = false;

        try
        {
            await Task.Run(() => SafeEject.Eject(root));

            ejected = true;
            StatusText.Text = loc["CardCopy.Eject.Done"];
            ResultTitle.Text = loc["CardCopy.Done.Title"] + " · " + loc["CardCopy.Eject.DoneShort"];
            Log($"已安全弹出 {root}", LogLevel.Success);

            // 卡已经走了：「弹出」这个按钮不能再留着（它指向一个不存在的盘符）。
            _copied = false;
            _ejected = true;
        }
        catch (Exception ex)
        {
            // 弹出失败不静默：卡还挂着，用户以为拔了就安全 —— 那才是最坏的情况。
            // 按钮保持「弹出 TF 卡」，让用户处理完占用后能再点一次。
            var message = ServerAddressPrivacy.Hide(ex.Message);
            Error(loc["CardCopy.Eject.Failed"] + message);
            Log("弹出失败：" + message, LogLevel.Error);
        }
        finally
        {
            SetBusy(false);
        }

        if (!ejected)
        {
            return;
        }

        // ⚠️ 重新枚举必须放在 finally **之后**：RefreshAsync 开头就是 `if (_busy) return`，
        //    在还没复位忙状态时调它等于什么都没做（设备下拉里会继续挂着一个已弹出的盘符）。
        await RefreshAsync();
    }

    private void Error(string message)
    {
        var loc = LocalizationService.Instance;

        ResultTitle.Text = loc["CardCopy.Error.Title"];
        ResultTitle.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        ResultBody.Text = message;
        ResultPanel.Visibility = Visibility.Visible;
        StatusText.Text = loc["CardCopy.Error.Status"];

        NoticeDialog.Inform(this, loc["CardCopy.Error.Title"], message, loc["CardCopy.Button.Close"]);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        Close();
    }
}
