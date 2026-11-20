// 移植自 yijianhuanbao（源文件 SdVolumeMounts.cs，sha256 947ccf9002a5…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;

namespace SwitchCfwWizard.SdReset;

internal static class SdVolumeMounts
{
	internal static bool IsDriveLetter(string mount)
	{
		if (mount.Length == 3)
		{
			char c = char.ToUpperInvariant(mount[0]);
			if (c >= 'A' && c <= 'Z' && mount[1] == ':')
			{
				return mount[2] == '\\';
			}
		}
		return false;
	}

	internal static bool Matches(SdVolume volume, int disk, SdPartition partition)
	{
		if (volume.Disks.Length == 1 && volume.Disks[0] == disk && volume.Offset == partition.Offset)
		{
			return volume.Length == partition.Length;
		}
		return false;
	}

	internal static bool IsEmu(SdVolume volume, int disk, SdLayout layout)
	{
		return layout.Partitions.Skip(1).Any((SdPartition p) => Matches(volume, disk, p));
	}

	internal static string WaitForMain(int disk, SdLayout layout, Func<List<SdVolume>> observe, Action<SdVolume, string> removeMount, Func<SdVolume, string?> readyMain, Action<int>? delay = null)
	{
		if (delay == null)
		{
			delay = Thread.Sleep;
		}
		int num = 0;
		string? b = null;
		Exception? innerException = null;
		string message = "主数据分区尚未挂载。";
		for (int i = 0; i < 60; i++)
		{
			try
			{
				List<SdVolume> source = observe();
				innerException = null;
				bool flag = false;
				foreach (SdVolume item in source.Where((SdVolume v) => IsEmu(v, disk, layout)))
				{
					string[] mounts = item.Mounts;
					foreach (string arg in mounts)
					{
						removeMount(item, arg);
						flag = true;
					}
				}
				bool flag2 = source.Any((SdVolume v) => v.Disks.Contains(disk) && v.Mounts.Length != 0 && !layout.Partitions.Any((SdPartition p) => Matches(v, disk, p)));
				SdVolume? sdVolume = source.SingleOrDefault((SdVolume v) => Matches(v, disk, layout.Main));
				string? text = ((sdVolume == null) ? null : readyMain(sdVolume));
				if ((flag | flag2) || text == null)
				{
					num = 0;
					b = null;
					if (flag)
					{
						message = "emuMMC 分区的盘符或挂载点尚未移除。";
					}
					else
					{
						message = (flag2 ? "Windows 仍保留与新分区范围不符的旧盘符。" : "主数据分区尚未正确挂载。");
					}
				}
				else
				{
					num = ((!string.Equals(text, b, StringComparison.OrdinalIgnoreCase)) ? 1 : (num + 1));
					b = text;
					if (num >= 13)
					{
						return text;
					}
				}
			}
			catch (Exception ex) when ((ex is Win32Exception ex2 && (SdDeviceAccess.IsUnavailable(ex2.NativeErrorCode) || ex2.NativeErrorCode == 1005)) || (ex is IOException && ex.HResult == -2147023891))
			{
				num = 0;
				b = null;
				innerException = ex;
				if (ex is Win32Exception ex3)
				{
					if (ex3.NativeErrorCode == 1005)
					{
						goto IL_0222;
					}
				}
				else if (ex is IOException)
				{
					goto IL_0222;
				}
				bool flag3 = false;
				goto IL_022a;
				IL_0222:
				flag3 = true;
				goto IL_022a;
				IL_022a:
				message = (flag3 ? ("Windows 无法识别待检查卷的文件系统（系统错误码：1005）。\n" + ex.Message + "\n") : "Windows 正在重新识别 TF 卡设备。");
			}
			if (i < 59)
			{
				delay(250);
			}
		}
		throw new IOException(message, innerException);
	}
}
