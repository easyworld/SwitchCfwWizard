// 移植自 yijianhuanbao（源文件 SdDiskMetadata.cs，sha256 92cb37265352…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;

namespace SwitchCfwWizard.SdReset;

internal static class SdDiskMetadata
{
	internal static void Clear(long diskBytes, Action<long, byte[]> write, Action<string> progress)
	{
		if (diskBytes < 536870912 || diskBytes % 512 != 0L)
		{
			throw new ArgumentOutOfRangeException("diskBytes");
		}
		byte[] array = new byte[1048576];
		progress("清除磁盘头部的旧分区信息");
		for (long num = 0L; num < 16777216; num += array.Length)
		{
			write(num, array);
		}
		progress("清除磁盘尾部的备份分区信息");
		write(diskBytes - array.Length, array);
	}
}
