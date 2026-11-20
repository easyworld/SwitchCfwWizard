// 移植自 yijianhuanbao（源文件 AutoPayloadAssets.cs，sha256 9696bacbe8c6…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace SwitchCfwWizard.SdReset;

internal static class AutoPayloadAssets
{
	internal static int ExportSource(string destination)
	{
		using Stream stream = typeof(AutoPayloadAssets).Assembly.GetManifestResourceStream("NsToolbox.AutoEmu.zip") ?? throw new IOException("内置自动创建组件缺失。");
		using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read);
		using Stream stream2 = (zipArchive.GetEntry("source.zip") ?? throw new IOException("内置组件源码缺失。")).Open();
		if (!Path.IsPathRooted(destination))
		{
			string? baseDirectory = Path.GetDirectoryName(Environment.GetEnvironmentVariable("NS_TOOLBOX_EXECUTABLE") ?? Environment.ProcessPath ?? throw new IOException("无法确定导出位置，请指定绝对路径。"));
			destination = Path.Combine(baseDirectory ?? throw new IOException("无法确定导出位置，请指定绝对路径。"), destination);
		}
		using FileStream fileStream = new FileStream(Path.GetFullPath(destination), FileMode.CreateNew, FileAccess.Write, FileShare.None);
		stream2.CopyTo(fileStream);
		fileStream.Flush(flushToDisk: true);
		return 0;
	}

	internal static Dictionary<string, byte[]> Load()
	{
		using Stream stream = typeof(AutoPayloadAssets).Assembly.GetManifestResourceStream("NsToolbox.AutoEmu.zip") ?? throw new IOException("内置自动创建组件缺失，请重新下载工具。");
		using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read);
		using Stream utf8Json = (zipArchive.GetEntry("sha256.json") ?? throw new IOException("自动创建组件索引缺失。")).Open();
		Dictionary<string, string> dictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(utf8Json) ?? throw new IOException("组件索引无效。");
		Dictionary<string, byte[]> dictionary2 = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		string[] assetNames = EmuTaskFiles.AssetNames;
		foreach (string name in assetNames)
		{
			ZipArchiveEntry? zipArchiveEntry = zipArchive.GetEntry(name) ?? throw new IOException("自动创建组件不完整。");
			long length = zipArchiveEntry.Length;
			bool flag = ((length <= 0 || length > 16777216) ? true : false);
			if (flag || Enumerable.Count(zipArchive.Entries, (ZipArchiveEntry e) => e.FullName == name) != 1)
			{
				throw new IOException("自动创建组件大小或名称无效。");
			}
			using Stream stream2 = zipArchiveEntry.Open();
			using MemoryStream memoryStream = new MemoryStream();
			stream2.CopyTo(memoryStream);
			byte[] array = memoryStream.ToArray();
			if (!dictionary.TryGetValue(name, out var value) || Convert.ToHexString(SHA256.HashData(array)) != value)
			{
				throw new IOException("自动创建组件校验失败。");
			}
			dictionary2.Add(name, array);
		}
		if (dictionary2["payload.bin"].Length > 196608)
		{
			throw new IOException("自动创建启动文件超过安全大小。");
		}
		return dictionary2;
	}
}
