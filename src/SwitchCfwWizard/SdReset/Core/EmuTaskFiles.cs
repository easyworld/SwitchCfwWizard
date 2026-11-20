// 移植自 yijianhuanbao（源文件 EmuTaskFiles.cs，sha256 270c42ae69c7…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SwitchCfwWizard.SdReset;

public static class EmuTaskFiles
{
	public static readonly string[] AssetNames = new string[6] { "payload.bin", "nyx.bin", "libsys_minerva.bso", "libsys_lp0.bso", "res.pak", "thk.bin" };

	public static void Deploy(string root, EmuTask task, IReadOnlyDictionary<string, byte[]> assets, Action guard)
	{
		guard();
		if (EmuTask.HasPending(root))
		{
			throw new IOException("此卡已有未完成的重建任务，请先恢复或继续准备。");
		}
		if (assets.Count != AssetNames.Length || AssetNames.Any((string n) => !assets.TryGetValue(n, out var value) || value.Length == 0))
		{
			throw new InvalidDataException("内置自动创建组件不完整。");
		}
		task.VerifyPayload(File.ReadAllBytes(P("payload.bin")));
		Directory.CreateDirectory(P("bootloader/ns-toolbox"));
		WriteVerified(P("bootloader/ns-toolbox/task.bin"), task.Encode(), guard);
		string[] assetNames = AssetNames;
		foreach (string text in assetNames)
		{
			WriteVerified(P("bootloader/ns-toolbox/" + text), assets[text], guard);
		}
		guard();
		File.Move(P("payload.bin"), P("payload.bin.backup"));
		WriteVerified(P("payload.bin"), assets["payload.bin"], guard);
		WriteVerified(P("ready.flag.tmp"), task.Encode(), guard);
		guard();
		File.Move(P("ready.flag.tmp"), P("ready.flag"));
		string P(string p)
		{
			return PathPolicy.SafePath(root, p);
		}
	}

	public static void Resume(string root, IReadOnlyDictionary<string, byte[]> assets, Action guard)
	{
		guard();
		EmuTask emuTask = EmuTask.Read(root);
		if (File.Exists(P("ready.flag.restore")))
		{
			Restore(root, guard);
			return;
		}
		string text = P("payload.bin.backup");
		emuTask.VerifyPayload(File.ReadAllBytes(File.Exists(text) ? text : P("payload.bin")));
		if (AssetNames.Any((string n) => !assets.TryGetValue(n, out var value) || value.Length == 0))
		{
			throw new IOException("内置自动创建组件不完整。");
		}
		Directory.CreateDirectory(P("bootloader/ns-toolbox"));
		WriteVerified(P("bootloader/ns-toolbox/task.bin"), emuTask.Encode(), guard);
		string[] assetNames = AssetNames;
		foreach (string text2 in assetNames)
		{
			WriteVerified(P("bootloader/ns-toolbox/" + text2), assets[text2], guard);
		}
		guard();
		if (!File.Exists(text))
		{
			File.Move(P("payload.bin"), text);
		}
		WriteVerified(P("payload.bin"), assets["payload.bin"], guard);
		WriteVerified(P("ready.flag.tmp"), emuTask.Encode(), guard);
		guard();
		File.Move(P("ready.flag.tmp"), P("ready.flag"), overwrite: true);
		string P(string p)
		{
			return PathPolicy.SafePath(root, p);
		}
	}

	public static void Restore(string root, Action guard)
	{
		guard();
		string text = P("payload.bin.backup");
		EmuTask emuTask;
		try
		{
			emuTask = EmuTask.Read(root);
		}
		catch (Exception ex) when ((ex is IOException || ex is InvalidDataException) ? true : false)
		{
			if (File.Exists(text) || File.Exists(P("ready.flag")) || File.Exists(P("ready.flag.restore")) || !CanDiscardStaging(root))
			{
				throw;
			}
			RemoveTemporary(root, guard);
			return;
		}
		byte[] array = File.ReadAllBytes(File.Exists(text) ? text : P("payload.bin"));
		emuTask.VerifyPayload(array);
		WriteVerified(P("ready.flag.restore"), emuTask.Encode(), guard);
		WriteVerified(P("payload.bin.restore"), array, guard);
		guard();
		File.Move(P("payload.bin.restore"), P("payload.bin"), overwrite: true);
		emuTask.VerifyPayload(File.ReadAllBytes(P("payload.bin")));
		WriteVerified(P("ready.flag"), emuTask.Encode(), guard);
		RemoveTemporary(root, guard);
		string P(string p)
		{
			return PathPolicy.SafePath(root, p);
		}
	}

	private static bool CanDiscardStaging(string root)
	{
		string path = PathPolicy.SafePath(root, "bootloader/ns-toolbox");
		byte[] array = File.ReadAllBytes(PathPolicy.SafePath(root, "payload.bin"));
		int num = array.Length;
		bool flag = ((num < 288 || num > 196608) ? true : false);
		if (flag || ((ReadOnlySpan<byte>)array.AsSpan(0, Math.Min(512, array.Length))).IndexOf("ICTC"u8) < 0)
		{
			return false;
		}
		string path2 = PathPolicy.SafePath(root, "bootloader/ns-toolbox/payload.bin");
		if (File.Exists(path2) && File.ReadAllBytes(path2).SequenceEqual(array))
		{
			return false;
		}
		string[] allowed = AssetNames.Append("task.bin").ToArray();
		if (Directory.Exists(path))
		{
			return Directory.EnumerateFileSystemEntries(path).All((string p) => !Directory.Exists(p) && allowed.Contains(Path.GetFileName(p), PathPolicy.Comparer));
		}
		return true;
	}

	private static void RemoveTemporary(string root, Action guard)
	{
		guard();
		string text = PathPolicy.SafePath(root, "bootloader/ns-toolbox");
		if (Directory.Exists(text))
		{
			Check(text);
			Directory.Delete(text, recursive: true);
		}
		string[] array = new string[5] { "payload.bin.restore", "ready.flag.tmp", "payload.bin.backup", "ready.flag", "ready.flag.restore" };
		foreach (string relative in array)
		{
			guard();
			string path = PathPolicy.SafePath(root, relative);
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		static void Check(string dir)
		{
			foreach (string item in Directory.EnumerateFileSystemEntries(dir))
			{
				PathPolicy.EnsureNotLink(item);
				if (Directory.Exists(item))
				{
					Check(item);
				}
			}
		}
	}

	internal static void WriteVerified(string path, byte[] bytes, Action guard)
	{
		guard();
		using (FileStream fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
		{
			fileStream.Write(bytes);
			fileStream.Flush(flushToDisk: true);
		}
		guard();
		if (!File.ReadAllBytes(path).SequenceEqual(bytes))
		{
			throw new IOException("写入后读回校验失败：" + Path.GetFileName(path));
		}
	}
}
