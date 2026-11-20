// 移植自 yijianhuanbao（源文件 FreshInstall.cs，sha256 e4105097ce09…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SwitchCfwWizard.SdReset;

public static class FreshInstall
{
	public static async Task ExecuteAsync(PackageSnapshot package, string root, Action guard, IProgress<UpdateProgress>? progress, CancellationToken token)
	{
		Check();
		EmuTask.CheckPackage(package);
		if (EmuTask.HasPending(root))
		{
			throw new IOException("TF 卡已有自动创建任务，请先恢复启动文件。");
		}
		if (PathPolicy.WithinAbsolute(package.Root, root) || PathPolicy.WithinAbsolute(root, package.Root))
		{
			throw new IOException("整合包缓存不能位于目标 TF 卡。");
		}
		long done;
		int count;
		byte[] buffer;
		PackageFile[] files;
		checked
		{
			long num = package.TotalBytes + unchecked((long)checked(package.Files.Length + package.Directories.Length + 1)) * 65536L;
			string rootPath = Path.GetPathRoot(root) ?? throw new IOException("无法确认主分区所在的盘符。");
			if (new DriveInfo(rootPath).AvailableFreeSpace < num)
			{
				throw new IOException("主分区剩余空间不足，无法安装整合包。");
			}
			foreach (string item in package.Directories.OrderBy((string p) => p.Count((char c) => c == '/')))
			{
				Check();
				Directory.CreateDirectory(PathPolicy.SafePath(root, item));
			}
			done = 0L;
			count = 0;
			buffer = new byte[1048576];
			files = package.Files;
		}
		foreach (PackageFile file in files)
		{
			Check();
			string path = PathPolicy.SafePath(package.Root, file.Relative);
			string target = PathPolicy.SafePath(root, file.Relative);
			using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			long copied = 0L;
			await using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true))
			{
				await using FileStream output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true);
				while (true)
				{
					int num3;
					int n = (num3 = await input.ReadAsync(buffer, token));
					if (num3 <= 0)
					{
						break;
					}
					Check();
					await output.WriteAsync(buffer.AsMemory(0, n), token);
					hash.AppendData(buffer, 0, n);
					copied += n;
					progress?.Report(new UpdateProgress("正在安装整合包", file.Relative, done + copied, package.TotalBytes, count, package.Files.Length));
				}
				if (copied != file.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Hash, StringComparison.OrdinalIgnoreCase))
				{
					throw new IOException("整合包源文件发生变化，请重新准备整合包。");
				}
				await output.FlushAsync(token);
				output.Flush(flushToDisk: true);
			}
			Check();
			await using (FileStream input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true))
			{
				if (!Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(file.Hash, StringComparison.OrdinalIgnoreCase))
				{
					throw new IOException("TF 卡文件读回校验失败：" + file.Relative);
				}
			}
			File.SetLastWriteTimeUtc(target, file.Modified);
			done += file.Length;
			count++;
		}
		Check();
		progress?.Report(new UpdateProgress("整合包安装并校验完成", "", done, done, count, count));
		void Check()
		{
			token.ThrowIfCancellationRequested();
			guard();
		}
	}
}
