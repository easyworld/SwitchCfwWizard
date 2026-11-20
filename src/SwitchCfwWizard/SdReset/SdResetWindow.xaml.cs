using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Localization;
using SwitchCfwWizard.ViewModels;

namespace SwitchCfwWizard.SdReset;

/// <summary>
/// 格式化与重建虚拟系统。
///
/// 移植自 yijianhuanbao 的同名功能，按用户 2026-11-20 的六项要求改过：
///   ① emuMMC 容量不再用「文本 + 下拉框」，改为拖动布局条上的分隔块分配，下方实时显示两者容量；
///   ② 设备**必须手动选择**（不再自动挑第一个，避免认错卡）；
///   ③ 文案统一用「TF 卡」（原文写的 TF 卡）；
///   ④ 打开前由调用方检查主界面定制选项（见 <see cref="MainViewModel.HasCustomSelection"/>）；
///   ⑤ 「准备与确认」阶段用主界面勾选的定制选项生成整合包（<see cref="MainViewModel.GeneratePackageForSdResetAsync"/>）；
///   ⑥ README 只写用法，不写本轮移植过程。
///
/// ⚠️ 这里是**破坏性操作**的唯一入口：一旦进入「清空并开始」，整卡数据不可恢复。
/// 所以分两道闸：确认页必须勾选知晓，写入前再核对一次设备身份与分区表（<see cref="VerifyBeforeErase"/> / <see cref="Guard"/>）。
/// </summary>
public partial class SdResetWindow : Window
{
    private enum Phase
    {
        Settings,
        Confirm,
        Format,
        Mount,
        Install,
        Deploy,
        Complete,
    }

    private static LocalizationService Loc => LocalizationService.Instance;

    private readonly MainViewModel _viewModel;

    /// <summary>本次是否要装整合包。false = 用户在主界面没勾任何定制选项、但选择了「继续」⇒ 只做分区。</summary>
    private readonly bool _installPackage;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    private readonly Stopwatch _speedClock = new();

    private UpdateProgress? _latest;

    private long _speedBytes;

    private long _nextGuard;

    private bool _ready;

    private bool _scanning;

    private bool _busy;

    private bool _cancelAllowed;

    private bool _ejected;

    private bool _pollViewModel;

    private Phase _phase;

    private CancellationTokenSource? _cancel;

    private SdDevice? _device;

    private SdFormatOptions? _options;

    private SdLayout? _layout;

    private string? _root;

    private byte[]? _publishedMbr;

    private uint _expectedSignature;

    private PackageSnapshot? _package;

    private Dictionary<string, byte[]>? _assets;

    private int _emuGiB = 12;

    private string _layoutSummary = string.Empty;

    /// <summary>上一条已写进日志的阶段文字 —— worker 会高频回报同一个阶段，按变化记录即可。</summary>
    private string _lastLoggedStage = string.Empty;

    /// <summary>把本窗口的动作写进主界面的运行日志（用户要求：这个窗口里的操作也要留痕）。</summary>
    private void Log(string message, LogLevel level = LogLevel.Info) => _viewModel.Log(level, "[重置] " + message);

    public SdResetWindow(MainViewModel viewModel, bool installPackage = true)
    {
        _viewModel = viewModel;
        _installPackage = installPackage;
        InitializeComponent();

        Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 24));
        MaxHeight = SystemParameters.WorkArea.Height - 12;

        _ready = true;
        SetStep(1);

        _timer.Tick += (_, _) => Tick();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _timer.Stop();
        Closing += (_, e) =>
        {
            if (!_busy)
            {
                return;
            }

            e.Cancel = true;
            if (_cancelAllowed)
            {
                _cancel?.Cancel();
            }
        };

        LayoutBarHost.SizeChanged += (_, _) => RenderBar();
    }

    // ── 设备 ───────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        if (_scanning || _busy)
        {
            return;
        }

        _scanning = true;
        RefreshButton.IsEnabled = false;
        DevicePicker.IsEnabled = false;
        StartButton.IsEnabled = false;

        try
        {
            var devices = await Task.Run(SdDisks.Scan);

            // 优先展示第一张卡：下拉框里有内容，用户才看得见「检测到了什么」。
            // 仍可改成别的卡；设备身份在点「清空并开始」之前还会再核对一次（EnsureSame）。
            BindDevices(devices);
            UpdatePreview();

            Log(devices.Count == 0
                ? "未检测到可用的 TF 卡设备"
                : string.Format("检测到 {0} 个设备：{1}", devices.Count,
                    string.Join("；", devices.Select(d => d.Display))),
                devices.Count == 0 ? LogLevel.Warning : LogLevel.Info);

            DeviceDetails.Text = devices.Count == 0
                ? Loc["SdReset.Device.Empty"]
                : string.Format(Loc["SdReset.Device.Count"], devices.Count);
        }
        catch (Exception ex)
        {
            Log("检测设备失败：" + ServerAddressPrivacy.Hide(ex.Message), LogLevel.Error);
            DeviceDetails.Text = Loc["SdReset.Device.Failed"] + ServerAddressPrivacy.Hide(ex.Message);
        }
        finally
        {
            _scanning = false;
            RefreshButton.IsEnabled = true;
            DevicePicker.IsEnabled = true;
        }
    }

    /// <summary>
    /// 把扫描结果放进下拉框，并**默认选中第一个**（用户 2026-11-20：「优先展示第一张 TF 卡」）。
    /// 单独抽出来是为了让自检能直接驱动这一步 —— 真实的 <see cref="RefreshAsync"/> 要枚举磁盘，
    /// 而「有没有默认选中」这件事与硬件无关，用合成设备就能钉住。
    /// </summary>
    internal void BindDevices(IReadOnlyList<SdDevice> devices)
    {
        DevicePicker.ItemsSource = devices;
        DevicePicker.SelectedItem = devices.FirstOrDefault();
    }

    private SdFormatOptions BuildOptions()
    {
        var fileSystem = FileSystemPicker.SelectedIndex == 1 ? SdFileSystem.ExFat : SdFileSystem.Fat32;
        var emu = WithEmu.IsChecked == true ? 1 : 0;
        return new SdFormatOptions(fileSystem, emu, _emuGiB);
    }

    private void UpdatePreview()
    {
        if (!_ready)
        {
            return;
        }

        StartButton.IsEnabled = false;
        _layout = null;
        _layoutSummary = string.Empty;

        var rebuild = WithEmu.IsChecked == true;
        LayoutBarHost.Visibility = Visibility.Collapsed;
        LegendPanel.Visibility = Visibility.Collapsed;
        LegendPanelEmu.Visibility = Visibility.Collapsed;
        DragHint.Visibility = Visibility.Collapsed;
        LayoutPlaceholder.Visibility = Visibility.Visible;
        LayoutPlaceholder.Text = Loc["SdReset.Layout.Empty"];

        if (DevicePicker.SelectedItem is not SdDevice device)
        {
            DeviceDetails.Text = Loc["SdReset.Device.Hint"];
            return;
        }

        try
        {
            device.EnsureResetAllowed();

            var options = BuildOptions();
            if (rebuild)
            {
                var max = SdDragPlan.MaxAllowedGiB(device.Capacity);
                if (max < SdDragPlan.MinEmuGiB)
                {
                    LayoutPlaceholder.Text = Loc["SdReset.Layout.TooSmall"];
                    DeviceDetails.Text = string.Format(Loc["SdReset.Device.Detail"], device.Capacity / 1073741824d);
                    return;
                }

                _emuGiB = SdDragPlan.ClampGiB(device.Capacity, _emuGiB);
                options = BuildOptions();
            }

            var layout = SdLayout.Create(device.Capacity, options);
            _layout = layout;

            DeviceDetails.Text = string.Format(Loc["SdReset.Device.Detail"], device.Capacity / 1073741824d);

            var parts = new List<string>
            {
                $"{Loc["SdReset.Layout.Main"]}：{Bytes(layout.Main.Length)}",
            };

            if (layout.Partitions.Length > 1)
            {
                parts.Add($"{Loc["SdReset.Layout.Emu"]}：{Bytes(layout.Partitions[1].Length)}");
            }

            _layoutSummary = string.Join("\n", parts);

            MainSizeText.Text = Bytes(layout.Main.Length);
            EmuSizeText.Text = layout.Partitions.Length > 1 ? Bytes(layout.Partitions[1].Length) : "-";

            LegendPanel.Visibility = Visibility.Visible;
            LegendPanelEmu.Visibility = rebuild ? Visibility.Visible : Visibility.Collapsed;
            LayoutBarHost.Visibility = rebuild ? Visibility.Visible : Visibility.Collapsed;
            DragHint.Visibility = rebuild ? Visibility.Visible : Visibility.Collapsed;
            LayoutPlaceholder.Visibility = rebuild ? Visibility.Collapsed : Visibility.Visible;

            if (!rebuild)
            {
                LayoutPlaceholder.Text = Loc["SdReset.Layout.FormatOnly"];
            }

            RenderBar();
            StartButton.IsEnabled = !_busy;
        }
        catch (Exception ex)
        {
            LayoutPlaceholder.Text = Loc["SdReset.Layout.Invalid"];
            DeviceDetails.Text = ServerAddressPrivacy.Hide(ex.Message);
        }
    }

    private void RenderBar()
    {
        if (_layout is null || !(LayoutBarHost.ActualWidth > 0))
        {
            return;
        }

        var width = LayoutBarHost.ActualWidth;
        var mainWidth = width * _layout.Main.Length / _layout.DiskBytes;

        MainBlock.Width = Math.Max(0, mainWidth);
        EmuBlock.Width = Math.Max(0, width - mainWidth);
        Canvas.SetLeft(EmuBlock, mainWidth);
        Canvas.SetLeft(DragThumb, Math.Max(0, mainWidth - DragThumb.Width / 2));
        DragThumb.Visibility = _layout.Partitions.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 拖动分配容量（① 的核心）────────────────────────────────────

    private void DragThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_busy || !(LayoutBarHost.ActualWidth > 0))
        {
            return;
        }

        var left = Canvas.GetLeft(DragThumb);
        if (double.IsNaN(left))
        {
            left = 0;
        }

        // DragDelta 给的是**增量**；换算成「分隔条中心在整条里的比例」，再交给纯函数吸附与夹取。
        var center = left + DragThumb.Width / 2 + e.HorizontalChange;
        ApplyDragFraction(center / LayoutBarHost.ActualWidth);
    }

    /// <summary>
    /// 把「分隔条位置（0～1）」应用成新的 emuMMC 容量。
    /// 抽成 internal 是为了让自检能直接调用：像素级拖动没法离线模拟，
    /// 但「给定比例 → 容量是否按预期变化」可以，而且这正是写反方向时唯一会露馅的地方。
    /// 返回是否真的改变了容量（同一档位 / 卡太小 / 没选设备都返回 false）。
    /// </summary>
    internal bool ApplyDragFraction(double fraction)
    {
        // ⚠️ 这里必须读**下拉框当前选中的设备**，不能用 `_device`：
        //    `_device` 只在「下一步」里赋值 —— 在①分区设置里拖的时候它还是 null，
        //    原先的判空直接 return，表现就是「方块完全拖不动」，而且没有任何报错。
        if (DevicePicker.SelectedItem is not SdDevice device)
        {
            return false;
        }

        var next = SdDragPlan.GiBFromFraction(device.Capacity, fraction);
        if (next == 0 || next == _emuGiB)
        {
            return false;
        }

        _emuGiB = next;
        UpdatePreview();
        return true;
    }

    // ── 步骤与忙碌态 ───────────────────────────────────────────────

    private void SetStep(int step)
    {
        var steps = new[] { Step1, Step2, Step3, Step4 };
        for (var i = 0; i < steps.Length; i++)
        {
            var active = i + 1 == step;
            steps[i].Foreground = (Brush)FindResource(active ? "AccentBrush" : "TextTertiaryBrush");
            steps[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void SetBusy(bool value, bool canCancel = false)
    {
        _busy = value;
        _cancelAllowed = canCancel;

        StartButton.IsEnabled = !value;
        SettingsPanel.IsEnabled = !value;
        BackButton.Visibility = !value && (int)_phase is 1 or 2 ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.IsEnabled = !value || canCancel;
        CloseButton.Content = value ? Loc["SdReset.Button.Cancel"] : Loc["SdReset.Button.Close"];

        if (value)
        {
            _timer.Start();
            return;
        }

        RenderProgress();
        _timer.Stop();
    }

    private IProgress<UpdateProgress> Reporter() => new RelayProgress(value => Interlocked.Exchange(ref _latest, value));

    private sealed class RelayProgress(Action<UpdateProgress> action) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => action(value);
    }

    private void Tick()
    {
        if (_pollViewModel)
        {
            StatusText.Text = _viewModel.OverallStatus;
            ResetProgress.IsIndeterminate = false;
            ResetProgress.Value = _viewModel.OverallProgress;
            ProgressPercent.Text = $"{_viewModel.OverallProgress:0}%";
            return;
        }

        RenderProgress();
    }

    private void RenderProgress()
    {
        var progress = Volatile.Read(ref _latest);
        if (progress is null)
        {
            return;
        }

        StatusText.Text = ServerAddressPrivacy.Hide(progress.Stage);
        ProgressPath.Text = progress.IsDownload ? string.Empty : ServerAddressPrivacy.Hide(progress.Path);
        ResetProgress.IsIndeterminate = progress.Total <= 0;
        ResetProgress.Value = progress.Percent;
        ProgressPercent.Text = progress.Total > 0 ? $"{progress.Percent}%" : string.Empty;

        if (progress.IsDownload)
        {
            if (!_speedClock.IsRunning)
            {
                _speedBytes = progress.Done;
                _speedClock.Start();
            }

            if (_speedClock.ElapsedMilliseconds >= 500)
            {
                var rate = Math.Max(0L, progress.Done - _speedBytes) / _speedClock.Elapsed.TotalSeconds;
                DownloadText.Text = $"{Bytes(progress.Done)} / {(progress.Total > 0 ? Bytes(progress.Total) : "?")}"
                                    + $"　·　{Bytes((long)rate)}/s";
                _speedBytes = progress.Done;
                _speedClock.Restart();
            }
        }
        else
        {
            _speedClock.Reset();
            DownloadText.Text = progress.TotalFiles > 0
                ? $"{Loc["SdReset.Files"]}：{progress.Files} / {progress.TotalFiles}"
                : string.Empty;
        }
    }

    private static string Bytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }

    // ── ① → ② 准备与确认 ──────────────────────────────────────────

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _scanning)
        {
            return;
        }

        if (_phase == Phase.Settings)
        {
            await PrepareAsync();
        }
        else if (_phase == Phase.Confirm && Acknowledgement.IsChecked == true)
        {
            await WriteAsync();
        }
    }

    private async Task PrepareAsync()
    {
        if (DevicePicker.SelectedItem is not SdDevice device)
        {
            Log("未选择 TF 卡，准备已中止", LogLevel.Warning);
            DeviceDetails.Text = Loc["SdReset.Device.Hint"];
            NoticeDialog.Inform(this, Loc["SdReset.Title"], Loc["SdReset.Device.Hint"], Loc["SdReset.Button.Ok"]);
            return;
        }

        try
        {
            _device = device;
            _options = BuildOptions();
            _layout = SdLayout.Create(device.Capacity, _options);

            Log($"开始准备：{device.Display}；主分区 {Bytes(_layout.Main.Length)}"
                + (_layout.Partitions.Length > 1 ? $"，emuMMC {Bytes(_layout.Partitions[1].Length)}" : "，仅格式化"));

            SetStep(2);
            SetBusy(true, canCancel: true);
            await Task.Run(VerifyBeforeErase);

            if (_options.EmuCount == 1 && _installPackage)
            {
                // 现场复核：对话框打开期间主界面可能被改过（比如把勾选全取消了）。
                if (!_viewModel.HasCustomSelection)
                {
                    throw new IOException(Loc["SdReset.Error.NoSelection"]);
                }

                ShowResult(Loc["SdReset.Prepare.Title"], Loc["SdReset.Prepare.Hint"]);
                SetBusy(true, canCancel: true);

                _cancel = new CancellationTokenSource();
                _pollViewModel = true;

                // 开关只有一个，在「全局选项 → 输出内容」里。这里**只读、不改写**：
                // 以前在窗口里勾、准备结束又写回 false，等于把用户在主界面的选择清掉。
                Log(_viewModel.PreferLocalDownloads
                    ? "开始生成整合包（已启用「优先使用本地资源」：本地文件大小一致时不再下载）"
                    : "开始生成整合包（走网络下载）");

                var generated = await _viewModel.GeneratePackageForSdResetAsync();
                _pollViewModel = false;

                // 取消要把两件事都停掉：本窗口的等待，以及主视图模型里真正的下载/生成。
                // 只 CancellationToken 不够 —— 那条管线用的是它自己的 CTS（见 Close_Click）。
                _cancel.Token.ThrowIfCancellationRequested();

                Log(generated ? "整合包生成完成" : "整合包生成后没有 payload.bin",
                    generated ? LogLevel.Success : LogLevel.Error);

                if (!generated)
                {
                    throw new IOException(Loc["SdReset.Error.PackageMissing"]);
                }

                _package = await Task.Run(() => Package.Scan(AppPaths.OutputRoot), _cancel.Token);
                await Task.Run(() => EmuTask.CheckPackage(_package), _cancel.Token);
                Log($"整合包：{_package.Files.Length} 个文件 / {_package.Directories.Length} 个目录 / {Bytes(_package.TotalBytes)}");

                _assets = await Task.Run(AutoPayloadAssets.Load, _cancel.Token);

                if (_options.FileSystem == SdFileSystem.Fat32
                    && _package.Files.Any(f => f.Length > uint.MaxValue))
                {
                    throw new IOException(Loc["SdReset.Error.Fat32Size"]);
                }

                var needed = _package.TotalBytes
                             + (_package.Files.Length + _package.Directories.Length + 1) * 65536L
                             + 33554432L;

                if (_layout.Main.Length < needed)
                {
                    throw new IOException(Loc["SdReset.Error.Space"]);
                }

                await Task.Run(VerifyBeforeErase);
            }

            _phase = Phase.Confirm;
            SettingsPanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Collapsed;
            ConfirmPanel.Visibility = Visibility.Visible;

            ConfirmDevice.Text = device.Display;
            ConfirmLayout.Text = _layoutSummary + "\n"
                                 + $"{Loc["SdReset.FileSystem"]}：" +
                                 (_options.FileSystem == SdFileSystem.Fat32
                                     ? Loc["SdReset.FileSystem.Fat32"]
                                     : Loc["SdReset.FileSystem.ExFat"]);

            // 只做分区的那一次，必须在这里说清楚「不会装东西」——
            // 后面的完成页说就晚了，用户已经按了「清空并开始」。
            if (!_installPackage)
            {
                ConfirmLayout.Text += "\n" + Loc["SdReset.Confirm.NoPackage"];
            }

            Acknowledgement.IsChecked = false;
            AckHint.Visibility = Visibility.Visible;
            StartButton.Content = Loc["SdReset.Button.Start"];
        }
        catch (OperationCanceledException)
        {
            Log("已取消准备", LogLevel.Warning);
            BackToSettings();
        }
        catch (Exception ex)
        {
            var message = ServerAddressPrivacy.Hide(ex.Message);
            Log("准备失败：" + message, LogLevel.Error);

            BackToSettings();
            DeviceDetails.Text = message;

            // ⚠️ 以前只把原因写进上面这行浅灰小字 —— 它在滚动区里、字号又小，
            //    用户看到的就是「点了下一步没反应」。现在一律给提示框，原因不可能被忽略。
            NoticeDialog.Inform(this, Loc["SdReset.Error.Title"],
                message + "\n\n" + Loc["SdReset.Error.HelpPrepare"], Loc["SdReset.Button.Ok"]);
        }
        finally
        {
            _pollViewModel = false;
            _cancel?.Dispose();
            _cancel = null;
            SetBusy(false);

            if (_phase == Phase.Confirm)
            {
                StartButton.IsEnabled = false;
            }
        }
    }

    private void BackToSettings()
    {
        _phase = Phase.Settings;
        SettingsPanel.Visibility = Visibility.Visible;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        AckHint.Visibility = Visibility.Collapsed;
        StartButton.Visibility = Visibility.Visible;
        StartButton.Content = Loc["SdReset.Button.Next"];
        SetStep(1);
        UpdatePreview();
    }

    // ── ③ 写入 ───────────────────────────────────────────────────

    private void VerifyBeforeErase()
    {
        try
        {
            var current = SdDisks.Scan().SingleOrDefault(d => d.Number == _device!.Number)
                          ?? throw new IOException(Loc["SdReset.Error.CardGone"]);

            _device!.EnsureSame(current);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(Loc["SdReset.Error.AccessDenied"], ex);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 5 or 32 or 33)
        {
            // 5=拒绝访问、32/33=被占用：这两类以前都被报成「TF 卡已断开」，方向完全错了。
            throw new IOException(Loc["SdReset.Error.AccessDenied"], ex);
        }
    }

    private void Guard()
    {
        if (Environment.TickCount64 < _nextGuard)
        {
            return;
        }

        if (_device is null || _root is null || _publishedMbr is null)
        {
            throw new IOException(Loc["SdReset.Error.NotConfirmed"]);
        }

        if (!SdDisks.ReadTaskMbr(_device, _root).SequenceEqual(_publishedMbr))
        {
            throw new IOException(Loc["SdReset.Error.LayoutChanged"]);
        }

        _nextGuard = Environment.TickCount64 + 800;
    }

    private void ForceGuard()
    {
        _nextGuard = 0;
        Guard();
    }

    private async Task WriteAsync()
    {
        SetBusy(true);
        StartButton.Visibility = Visibility.Collapsed;
        EjectResetButton.Visibility = Visibility.Collapsed;

        ShowResult(_phase == Phase.Mount ? Loc["SdReset.Mount.Title"] : Loc["SdReset.Write.Title"],
            Loc["SdReset.Write.Hint"]);
        SetStep(3);

        try
        {
            if ((int)_phase is >= 1 and <= 3)
            {
                var mountOnly = _phase == Phase.Mount;

                if (!mountOnly)
                {
                    await Task.Run(VerifyBeforeErase);
                    _phase = Phase.Format;
                    _expectedSignature = 0;
                }

                StatusText.Text = mountOnly ? Loc["SdReset.Mount.Stage"] : Loc["SdReset.Format.Stage"];
                Log(mountOnly ? "开始检查 TF 卡盘符" : "开始写入分区表并格式化（会请求管理员权限）");

                SdResetStatus? final = null;
                await SdResetWorker.ExecuteAsync(
                    new SdResetRequest(_device!, _options!, mountOnly, _expectedSignature),
                    status =>
                    {
                        StatusText.Text = status.Error ?? status.Stage;

                        // 只在**阶段变化**时记一行：worker 会高频回报同一段文字，全记会把日志淹掉。
                        if (status.Stage != _lastLoggedStage)
                        {
                            _lastLoggedStage = status.Stage;
                            Log(status.Stage, status.Error is null ? LogLevel.Info : LogLevel.Error);
                        }

                        if (status.Finished)
                        {
                            final = status;
                        }
                    });

                if (final is null)
                {
                    throw new IOException(Loc["SdReset.Error.NoResult"]);
                }

                if (final.Error is not null)
                {
                    throw new IOException(final.Error);
                }

                _phase = Phase.Mount;
                _root = final.Root;

                if (final.Signature != 0)
                {
                    _expectedSignature = final.Signature;
                }

                if (_expectedSignature == 0)
                {
                    throw new IOException(Loc["SdReset.Error.NoSignature"]);
                }

                if (_root is null)
                {
                    throw new IOException(final.Stage);
                }

                _publishedMbr = await Task.Run(() => SdDisks.ReadTaskMbr(_device!, _root!));

                if (BinaryPrimitives.ReadUInt32LittleEndian(_publishedMbr.AsSpan(440)) != _expectedSignature)
                {
                    throw new IOException(Loc["SdReset.Error.SignatureChanged"]);
                }

                var expectedMbr = _layout!.CreateMbr(_expectedSignature);
                for (var i = 0; i < 4; i++)
                {
                    if (expectedMbr[450 + i * 16] != _publishedMbr[450 + i * 16]
                        || !expectedMbr.AsSpan(454 + i * 16, 8).SequenceEqual(_publishedMbr.AsSpan(454 + i * 16, 8)))
                    {
                        throw new IOException(Loc["SdReset.Error.LayoutMismatch"]);
                    }
                }

                ForceGuard();

                // 没有整合包（主界面没勾任何定制选项、用户选择了继续）：
                // 分区与格式化就是这次的全部内容，不再进入安装 / 自动创建。
                if (_options!.EmuCount == 0 || !_installPackage)
                {
                    Complete();
                    return;
                }

                _phase = Phase.Install;
            }

            if (_phase == Phase.Install)
            {
                ForceGuard();
                ShowResult(Loc["SdReset.Install.Title"], Loc["SdReset.Install.Hint"]);
                Log($"开始安装整合包：{_package!.Files.Length} 个文件 / {Bytes(_package.TotalBytes)}");
                await Task.Run(() => FreshInstall.ExecuteAsync(
                    _package!, _root!, Guard, Reporter(), CancellationToken.None));
                Log("整合包安装并逐文件校验完成", LogLevel.Success);
                ForceGuard();
                _phase = Phase.Deploy;
            }

            if (_phase != Phase.Deploy)
            {
                return;
            }

            ShowResult(Loc["SdReset.Deploy.Title"], Loc["SdReset.Deploy.Hint"]);
            Log("开始部署「下次开机自动创建虚拟系统」的任务");
            await Task.Run(() =>
            {
                ForceGuard();

                if (EmuTask.HasPending(_root!))
                {
                    try
                    {
                        EmuTask.Read(_root!);
                    }
                    catch (IOException)
                    {
                        EmuTaskFiles.Restore(_root!, Guard);
                    }
                }

                if (EmuTask.HasPending(_root!))
                {
                    EmuTaskFiles.Resume(_root!, _assets!, Guard);
                }
                else
                {
                    var payload = File.ReadAllBytes(PathPolicy.SafePath(_root!, "payload.bin"));
                    var hash = _package!.Files.Single(f => PathPolicy.Comparer.Equals(f.Relative, "payload.bin")).Hash;

                    if (!Convert.ToHexString(SHA256.HashData(payload)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException(Loc["SdReset.Error.PayloadChanged"]);
                    }

                    var task = EmuTask.Create(_layout!, _publishedMbr!, payload);
                    EmuTaskFiles.Deploy(_root!, task, _assets!, Guard);
                }

                ForceGuard();
                EmuTask.Read(_root!).ValidateLayout(_publishedMbr!, _device!.Capacity);
            });

            Complete();
            Log(_installPackage ? "全部完成：分区、格式化、整合包与自动创建任务均已就绪" : "全部完成：只做了分区与格式化（未安装内容）",
                LogLevel.Success);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log("已取消 Windows 授权，尚未开始本次操作", LogLevel.Warning);
            Error(Loc["SdReset.Error.UacCancelled"]);
        }
        catch (Exception ex)
        {
            var message = ServerAddressPrivacy.Hide(ex.Message);
            Log("写入失败：" + message, LogLevel.Error);
            Error(message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowResult(string title, string help)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        CompletionPanel.Visibility = Visibility.Collapsed;

        ResultTitle.Text = title;
        ResultTitle.Foreground = (Brush)FindResource("TextPrimaryBrush");
        ResultHelp.Text = help;

        StatusText.Text = string.Empty;
        ProgressPath.Text = string.Empty;
        ProgressPercent.Text = string.Empty;
        DownloadText.Text = string.Empty;

        ResetProgress.IsIndeterminate = true;
        ResetProgress.Value = 0;

        _latest = null;
        _speedClock.Reset();
    }

    private void Error(string text)
    {
        _latest = null;
        ResultTitle.Text = Loc["SdReset.Error.Title"];
        ResultTitle.Foreground = (Brush)FindResource("DangerBrush");
        StatusText.Text = text;
        ResetProgress.IsIndeterminate = false;
        ProgressPath.Text = string.Empty;
        ProgressPercent.Text = string.Empty;
        DownloadText.Text = string.Empty;

        ResultHelp.Text = _phase switch
        {
            Phase.Mount => Loc["SdReset.Error.HelpMount"],
            Phase.Install or Phase.Deploy => Loc["SdReset.Error.HelpPartial"],
            _ => Loc["SdReset.Error.HelpRestart"],
        };

        StartButton.Content = _phase == Phase.Mount ? Loc["SdReset.Button.Recheck"] : Loc["SdReset.Button.Retry"];
        StartButton.Visibility = _phase == Phase.Format ? Visibility.Collapsed : Visibility.Visible;

        if (_phase == Phase.Format)
        {
            BackButton.Visibility = Visibility.Visible;
        }
    }

    private void Complete()
    {
        _phase = Phase.Complete;
        _latest = null;
        SetStep(4);

        ResetProgress.IsIndeterminate = false;
        ResetProgress.Value = 100;
        ProgressPercent.Text = "100%";

        var rebuild = _options!.EmuCount > 0;
        var withPackage = rebuild && _installPackage;

        ResultTitle.Text = rebuild
            ? (withPackage ? Loc["SdReset.Done.Title"] : Loc["SdReset.Done.TitlePartitionOnly"])
            : Loc["SdReset.Done.TitleFormatOnly"];
        ResultTitle.Foreground = (Brush)FindResource("SuccessBrush");

        StatusText.Text = string.Empty;
        ProgressPath.Text = string.Empty;
        DownloadText.Text = string.Empty;
        ResultHelp.Text = string.Empty;

        CompletionAction.Text = rebuild
            ? (withPackage ? Loc["SdReset.Done.Action"] : Loc["SdReset.Done.ActionPartitionOnly"])
            : Loc["SdReset.Done.ActionFormatOnly"];
        RebuildInstructions.Visibility = withPackage ? Visibility.Visible : Visibility.Collapsed;
        CompletionPanel.Visibility = Visibility.Visible;

        StartButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
        EjectResetButton.Visibility = Visibility.Visible;
        EjectResetButton.IsEnabled = true;
    }

    // ── 底部按钮 ─────────────────────────────────────────────────

    private void Options_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DevicePicker.SelectedItem is SdDevice device)
        {
            Log("已选择设备：" + device.Display);
        }

        UpdatePreview();
    }

    private void Acknowledgement_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready && _phase == Phase.Confirm)
        {
            var acknowledged = Acknowledgement.IsChecked == true;
            StartButton.IsEnabled = acknowledged && !_busy;
            AckHint.Visibility = acknowledged ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            if (_cancelAllowed)
            {
                _cancel?.Cancel();

                // ⚠️ 主视图模型的生成管线用它**自己的** CancellationTokenSource ——
                //    只取消本窗口那个 token，下载/生成会照跑不误，用户看到的是「点了取消没反应」。
                if (_viewModel.IsRunning)
                {
                    _viewModel.CancelCommand.Execute(null);
                }
            }

            return;
        }

        Close();
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        BackToSettings();
        await RefreshAsync();
    }

    private async void Eject_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _ejected || _device is null || _root is null)
        {
            return;
        }

        SetBusy(true);
        EjectResetButton.IsEnabled = false;

        try
        {
            ForceGuard();
            var root = _root;
            await Task.Run(() => SafeEject.Eject(root));
            _ejected = true;
            ResultTitle.Text += " · " + Loc["SdReset.Done.Ejected"];
            ResultHelp.Text = string.Empty;
            EjectResetButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ResultHelp.Text = ServerAddressPrivacy.Hide(ex.Message);
            ResultHelp.Foreground = (Brush)FindResource("DangerBrush");
            EjectResetButton.IsEnabled = true;
        }
        finally
        {
            SetBusy(false);
        }
    }
}
