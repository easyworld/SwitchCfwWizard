// 移植自 yijianhuanbao（源文件 SdVolumeScope.cs，sha256 b05219386181…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.Collections.Generic;
using System.Linq;

namespace SwitchCfwWizard.SdReset;

internal static class SdVolumeScope
{
	internal static bool IsPartitionDevice(string name)
	{
		if (name.StartsWith("\\Device\\HarddiskVolume", StringComparison.OrdinalIgnoreCase))
		{
			return Digits(name.AsSpan("\\Device\\HarddiskVolume".Length));
		}
		if (!name.StartsWith("\\Device\\Harddisk", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		int num = name.IndexOf("\\Partition", "\\Device\\Harddisk".Length, StringComparison.OrdinalIgnoreCase);
		if (num > "\\Device\\Harddisk".Length && Digits(name.AsSpan("\\Device\\Harddisk".Length, num - "\\Device\\Harddisk".Length)))
		{
			return Digits(name.AsSpan(num + 10));
		}
		return false;
	}

	private static bool Digits(ReadOnlySpan<char> text)
	{
		if (text.IsEmpty)
		{
			return false;
		}
		ReadOnlySpan<char> readOnlySpan = text;
		for (int i = 0; i < readOnlySpan.Length; i++)
		{
			char c = readOnlySpan[i];
			if ((c < '0' || c > '9') ? true : false)
			{
				return false;
			}
		}
		return true;
	}

	internal static SdVolume? ReadDrive(int disk, string root, string deviceName, List<SdVolume> volumes, Func<SdVolume> readMapping)
	{
		for (int i = 0; i < volumes.Count; i++)
		{
			if (!volumes[i].DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase) && volumes[i].Mounts.Contains(root, StringComparer.OrdinalIgnoreCase))
			{
				volumes[i] = volumes[i] with
				{
					Mounts = volumes[i].Mounts.Where((string m) => !m.Equals(root, StringComparison.OrdinalIgnoreCase)).ToArray()
				};
			}
		}
		if (!IsPartitionDevice(deviceName))
		{
			return null;
		}
		SdVolume[] array = volumes.Where((SdVolume v) => v.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
		if (array.Length != 0 && array.All((SdVolume v) => !v.Disks.Contains(disk)))
		{
			return null;
		}
		return readMapping() with
		{
			DeviceName = deviceName
		};
	}
}
