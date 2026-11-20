using System;
using System.IO;
using System.Security.Cryptography;

namespace SwitchCfwWizard.SdReset;

/// <summary>
/// 定位原生格式化组件 <c>NsToolbox.Formatter.dll</c>（fatfs 核心，导出 <c>ns_format</c>）。
///
/// 为什么需要这一层：该组件是**原生 DLL**，而本项目的发布形态里有「单文件」——
/// 单文件发布时 exe 同级目录里不会再有这个 dll，直接按 exe 目录拼路径就会
/// 「编译通过、发布通过、点下去才报找不到文件」。
/// 所以：先找 exe 同级（普通构建 / 多文件发布），找不到再从程序集内嵌资源释放到临时目录。
///
/// ⚠️ 释放目录带内容哈希：换版本会落到新目录，不会被上一次的旧 dll 顶替。
/// </summary>
internal static class NativeFormatter
{
    private const string FileName = "NsToolbox.Formatter.dll";

    private const string ResourceName = "SwitchCfwWizard.Native." + FileName;

    private static string? _resolved;

    internal static string EnsurePath()
    {
        if (_resolved is not null)
        {
            return _resolved;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, FileName);
        if (File.Exists(beside))
        {
            return _resolved = beside;
        }

        using var stream = typeof(NativeFormatter).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new IOException("内置格式化组件缺失，请重新获取程序。");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();

        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16];
        var directory = Path.Combine(Path.GetTempPath(), "SwitchCfwWizard-native-" + hash);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
        {
            File.WriteAllBytes(path, bytes);
        }

        return _resolved = path;
    }
}
