// 移植自 yijianhuanbao（源文件 Package.cs，sha256 fcfcf9149450…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SwitchCfwWizard.SdReset;

public static class Package
{
	public const string UpdaterEntry = "bootloader/ini/Fresh_updater.bin.ini";

	public static void Validate(string root)
	{
		if (!Directory.Exists(root))
		{
			throw new InvalidDataException("更新包目录不可访问：" + root);
		}
		string[] array = new string[2] { "atmosphere", "bootloader" };
		foreach (string text in array)
		{
			if (!Directory.Exists(PathPolicy.SafePath(root, text)))
			{
				throw new InvalidDataException("整合包缺少 " + text + " 文件夹。");
			}
		}
		string text2 = PathPolicy.SafePath(root, "payload.bin");
		if (!File.Exists(text2))
		{
			throw new InvalidDataException("整合包缺少 payload.bin 文件。");
		}
		long length = new FileInfo(text2).Length;
		if (length < 4 || length > 134217728)
		{
			throw new InvalidDataException("payload.bin 大小必须在 4 字节至 128 MiB 之间。");
		}
	}

	public static string FindRoot(string extracted)
	{
		List<string> candidates = new List<string>();
		Search(extracted, 0);
		if (candidates.Count != 1)
		{
			throw new InvalidDataException((candidates.Count == 0) ? "压缩包中未找到完整整合包。" : "压缩包中存在多个完整整合包，无法确定更新目录。");
		}
		Validate(candidates[0]);
		return candidates[0];
		void Search(string directory, int depth)
		{
			if (depth > 64)
			{
				throw new InvalidDataException("解压目录层级过深。");
			}
			PathPolicy.EnsureNotLink(directory);
			if (Directory.Exists(PathPolicy.Combine(directory, "atmosphere")) && Directory.Exists(PathPolicy.Combine(directory, "bootloader")) && File.Exists(PathPolicy.Combine(directory, "payload.bin")))
			{
				candidates.Add(directory);
			}
			foreach (string item in Directory.EnumerateDirectories(directory))
			{
				Search(item, depth + 1);
			}
		}
	}

	public static PackageSnapshot Scan(string root, CancellationToken token = default(CancellationToken))
	{
		Validate(root);
		List<string> directories = new List<string>();
		List<PackageFile> files = new List<PackageFile>();
		HashSet<string> names = new HashSet<string>(PathPolicy.Comparer);
		Visit(root, "", 0);
		return new PackageSnapshot(Path.GetFullPath(root), directories.ToArray(), files.ToArray());
		void Visit(string directory, string relative, int depth)
		{
			token.ThrowIfCancellationRequested();
			if (depth > 64)
			{
				throw new InvalidDataException("更新包目录层级过深。");
			}
			foreach (string item in Directory.EnumerateFileSystemEntries(directory))
			{
				token.ThrowIfCancellationRequested();
				PathPolicy.EnsureNotLink(item);
				string text = PathPolicy.Normalize((relative.Length == 0) ? Path.GetFileName(item) : (relative + "/" + Path.GetFileName(item)));
				if (!names.Add(text))
				{
					throw new InvalidDataException("包内存在大小写重复条目：" + text);
				}
				if (PathPolicy.Within(text, "update"))
				{
					throw new InvalidDataException("整合包不能嵌套 update 目录。");
				}
				if (PathPolicy.Comparer.Equals(text, "bootloader/ini/Fresh_updater.bin.ini"))
				{
					if (Directory.Exists(item))
					{
						throw new InvalidDataException("Fresh_updater 入口必须是文件。");
					}
				}
				else if (Directory.Exists(item))
				{
					directories.Add(text);
					Visit(item, text, depth + 1);
				}
				else
				{
					FileInfo fileInfo = new FileInfo(item);
					using FileStream stream = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.Read);
					string hash = Hash(stream, token);
					files.Add(new PackageFile(text, fileInfo.Length, fileInfo.LastWriteTimeUtc, hash));
				}
			}
		}
	}

	private static string Hash(Stream stream, CancellationToken token)
	{
		using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		byte[] array = new byte[1048576];
		int count;
		while ((count = stream.Read(array)) > 0)
		{
			token.ThrowIfCancellationRequested();
			incrementalHash.AppendData(array, 0, count);
		}
		return Convert.ToHexString(incrementalHash.GetHashAndReset());
	}

	public static void CheckSnapshot(PackageSnapshot package, CancellationToken token = default(CancellationToken), bool verifyContents = false)
	{
		Validate(package.Root);
		PackageFile[] files = package.Files;
		foreach (PackageFile packageFile in files)
		{
			token.ThrowIfCancellationRequested();
			string text = PathPolicy.SafePath(package.Root, packageFile.Relative);
			FileInfo fileInfo = new FileInfo(text);
			if (!fileInfo.Exists || fileInfo.Length != packageFile.Length || fileInfo.LastWriteTimeUtc != packageFile.Modified)
			{
				throw new IOException("更新源发生变化，请重新加载：" + text);
			}
			if (!verifyContents)
			{
				continue;
			}
			using FileStream stream = new FileStream(text, FileMode.Open, FileAccess.Read, FileShare.Read);
			if (!PathPolicy.Comparer.Equals(Hash(stream, token), packageFile.Hash))
			{
				throw new IOException("更新源内容发生变化，请重新加载：" + text);
			}
		}
		string[] directories = package.Directories;
		foreach (string text2 in directories)
		{
			if (!Directory.Exists(PathPolicy.SafePath(package.Root, text2)))
			{
				throw new IOException("更新源目录缺失：" + text2);
			}
		}
		HashSet<string> expected;
		if (verifyContents)
		{
			expected = new HashSet<string>(package.Directories.Concat(package.Files.Select((PackageFile f) => f.Relative)), PathPolicy.Comparer);
			Visit(package.Root);
			if (expected.Count != 0)
			{
				throw new IOException("更新源条目发生变化，请重新加载。");
			}
		}
		void Visit(string directory)
		{
			foreach (string item in Directory.EnumerateFileSystemEntries(directory))
			{
				token.ThrowIfCancellationRequested();
				PathPolicy.EnsureNotLink(item);
				string text3 = Path.GetRelativePath(package.Root, item).Replace('\\', '/');
				if (!PathPolicy.Comparer.Equals(text3, "bootloader/ini/Fresh_updater.bin.ini"))
				{
					if (!expected.Remove(text3))
					{
						throw new IOException("更新源出现新条目，请重新加载：" + text3);
					}
					if (Directory.Exists(item))
					{
						Visit(item);
					}
				}
			}
		}
	}

	public static async Task ExtractAsync(string archive, string output, CancellationToken token, IProgress<UpdateProgress>? progress = null)
	{
		if (PathPolicy.Exists(output))
		{
			throw new IOException("解压目标目录必须不存在。");
		}
		using ZipArchive zip = ZipFile.OpenRead(archive);
		HashSet<string> hashSet = new HashSet<string>(PathPolicy.Comparer);
		long total = 0L;
		List<(ZipArchiveEntry, string, bool)> list = new List<(ZipArchiveEntry, string, bool)>();
		foreach (ZipArchiveEntry entry in zip.Entries)
		{
			token.ThrowIfCancellationRequested();
			string text = PathPolicy.Normalize(entry.FullName);
			if (text.Length == 0 || !hashSet.Add(text))
			{
				throw new InvalidDataException("压缩包路径重复或为空：" + entry.FullName);
			}
			if (entry.FullName.StartsWith('/') || entry.FullName.StartsWith('\\') || ((entry.ExternalAttributes >> 16) & 0xF000) == 40960)
			{
				throw new InvalidDataException("压缩包包含不支持的路径或链接：" + entry.FullName);
			}
			bool item = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
			total = checked(total + entry.Length);
			list.Add((entry, text, item));
		}
		string outputRoot = Path.GetPathRoot(Path.GetFullPath(output)) ?? throw new IOException("无法确认缓存目录所在的盘符。");
		if (new DriveInfo(outputRoot).AvailableFreeSpace < total)
		{
			throw new IOException("电脑缓存目录空间不足，无法解压整合包。");
		}
		Directory.CreateDirectory(output);
		long completed = 0L;
		byte[] buffer = new byte[1048576];
		foreach (var item2 in list)
		{
			token.ThrowIfCancellationRequested();
			string path = PathPolicy.Combine(output, item2.Item2);
			if (item2.Item3)
			{
				Directory.CreateDirectory(path);
				continue;
			}
			string? parent = Path.GetDirectoryName(path);
			if (parent is not null)
			{
			    Directory.CreateDirectory(parent);
			}
			await using Stream input = item2.Item1.Open();
			await using FileStream target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true);
			long written = 0L;
			while (true)
			{
				int num;
				int read = (num = await input.ReadAsync(buffer, token));
				if (num <= 0)
				{
					break;
				}
				await target.WriteAsync(buffer.AsMemory(0, read), token);
				written += read;
				completed += read;
				progress?.Report(new UpdateProgress("正在解压整合包", item2.Item2, completed, total));
			}
			if (written != item2.Item1.Length)
			{
				throw new IOException("压缩包文件不完整：" + item2.Item2);
			}
			await target.FlushAsync(token);
		}
	}
}
