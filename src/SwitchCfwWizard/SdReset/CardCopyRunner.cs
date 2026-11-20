using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SwitchCfwWizard.Localization;

namespace SwitchCfwWizard.SdReset;

/// <summary>
/// 「清空卡 → 复制 out」的**文件操作层**。
///
/// 从窗口里抽出来的原因很实际：这两步是这个功能里唯一会改别人磁盘的动作，
/// 而窗口的方法没法在回归里调用。抽到这里之后，回归可以拿两个临时目录当
/// 「假卡 / 假 out」，把整条链跑一遍再断言结果 —— 不必等插一张真卡。
///
/// 这里**不碰分区表、不读物理设备**，只是普通的文件 API。
/// </summary>
internal static class CardCopyRunner
{
    private const string LogPrefix = "[复制] ";

    /// <summary>清空结果：删掉几项、哪些没删掉（附原因）。</summary>
    internal sealed record ClearResult(int Deleted, List<(string Name, string Error)> Failed);

    /// <summary>复制结果：复制了几个文件、多少字节。</summary>
    internal sealed record CopyResult(int Files, long Bytes);

    /// <summary>
    /// 删掉 <paramref name="root"/> 下**除保留项之外**的一切。
    ///
    /// 一项失败不中止其余项：卡上总有删不掉的东西（`System Volume Information`、
    /// 被别的程序占用的文件）。但失败的必须逐条返回，否则用户会以为「清空了」。
    /// </summary>
    internal static ClearResult Clear(string root)
    {
        var deleted = 0;
        var failed = new List<(string Name, string Error)>();

        foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            // 同名**文件**不算保留项：用户要留的是那三个目录（见 CardCopyPlan.IsKept 的注释）
            if (info is DirectoryInfo && CardCopyPlan.IsKept(info.Name))
            {
                continue;
            }

            try
            {
                if (info is DirectoryInfo directory)
                {
                    ClearReadOnlyTree(directory.FullName);
                    directory.Delete(recursive: true);
                }
                else
                {
                    ClearReadOnly(info.FullName);
                    info.Delete();
                }

                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add((info.Name, ex.Message));
            }
        }

        return new ClearResult(deleted, failed);
    }

    /// <summary>
    /// 把 <paramref name="sourceRoot"/> 整份复制到 <paramref name="targetRoot"/>（同名覆盖）。
    ///
    /// 每个目标路径都过一遍 <see cref="CardCopyPlan.IsInsideRoot"/>：这一步一旦算错，
    /// 后果是「把一整份 out 写到卡外面去」，而那时候没有回收站可用。
    /// </summary>
    internal static CopyResult Copy(
        string sourceRoot,
        string targetRoot,
        IProgress<(double Percent, string Relative, int Index, int Total)>? progress = null,
        CancellationToken token = default)
    {
        var files = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .ToList();

        var total = files.Sum(file => new FileInfo(file).Length);
        long done = 0;
        var copied = 0;

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceRoot, file);
            var target = Path.Combine(targetRoot, relative);

            if (!CardCopyPlan.IsInsideRoot(targetRoot, target))
            {
                throw new IOException("目标路径不在卡内：" + target);
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            // 卡上已有同名文件时，`File.Copy(overwrite: true)` 遇到只读目标会抛 —— 先摘掉只读位。
            ClearReadOnly(target);
            File.Copy(file, target, overwrite: true);

            done += new FileInfo(file).Length;
            copied++;

            progress?.Report(
                (total == 0 ? 100 : done * 100.0 / total, relative, copied, files.Count));
        }

        return new CopyResult(copied, total);
    }

    /// <summary>源目录总字节数与文件数（用于空间检查与进度条刻度）。</summary>
    internal static (int Files, long Bytes) Measure(string root)
    {
        if (!Directory.Exists(root))
        {
            return (0, 0);
        }

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        return (files.Count, files.Sum(file => new FileInfo(file).Length));
    }

    internal static void ClearReadOnly(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// 递归清掉只读位。
    ///
    /// 卡上的文件多半是从 Windows 拷过去的，常常带着只读属性 ——
    /// 而 <c>Directory.Delete(recursive: true)</c> 遇到只读文件会直接抛，整项删不掉。
    /// 这一步只是「允许删除」，不改变任何内容。
    /// </summary>
    internal static void ClearReadOnlyTree(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ClearReadOnly(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(directory, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    /// <summary>「12.3 MiB」这种给人看的字节数。</summary>
    internal static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30)
        {
            return $"{bytes / (double)(1L << 30):0.00} GiB";
        }

        if (bytes >= 1L << 20)
        {
            return $"{bytes / (double)(1L << 20):0.0} MiB";
        }

        return bytes >= 1024 ? $"{bytes / 1024.0:0.0} KiB" : $"{bytes} B";
    }

    /// <summary>
    /// 把卡根的一级子项读成列表项（带「保留 / 将删除」标记）。
    ///
    /// 放在这里而不是窗口里，是为了让回归能直接断言分类结果 ——
    /// 窗口那个类一碰就要拉 WPF，而这条逻辑本身跟界面无关。
    ///
    /// <paramref name="keepEverything"/> 对应窗口里的「不删除，直接复制」：
    /// 那一档**什么都不会删**，所以清单里每一项都得标「保留」。
    /// 让清单跟着开关一起变，比让用户看到一片「将删除」却一件都不删要诚实。
    /// </summary>
    internal static List<CardEntry> ListRoot(string root, bool keepEverything = false)
    {
        var loc = LocalizationService.Instance;
        var list = new List<CardEntry>();

        foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            var keep = keepEverything || (info is DirectoryInfo && CardCopyPlan.IsKept(info.Name));

            list.Add(new CardEntry
            {
                Name = info.Name + (info is DirectoryInfo ? "\\" : string.Empty),
                FullPath = info.FullName,
                IsDirectory = info is DirectoryInfo,
                Keep = keep,
                TagText = keep ? loc["CardCopy.Keep"] : loc["CardCopy.Delete"],
                SizeText = info is FileInfo file ? FormatBytes(file.Length) : string.Empty,
            });
        }

        // 目录在前、再按名字 —— 与资源管理器一致，也让「保留项」通常聚在一起。
        return list
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

/// <summary>卡根上的一项（给列表绑定用）。</summary>
public sealed class CardEntry
{
    public string Name { get; init; } = string.Empty;

    public string FullPath { get; init; } = string.Empty;

    public bool IsDirectory { get; init; }

    /// <summary>是否保留（三个目录之一）。</summary>
    public bool Keep { get; init; }

    /// <summary>「保留」/「将删除」，按当前界面语言算好。</summary>
    public string TagText { get; init; } = string.Empty;

    public string SizeText { get; init; } = string.Empty;
}
