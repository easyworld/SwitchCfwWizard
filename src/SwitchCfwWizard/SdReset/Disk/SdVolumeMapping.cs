// 移植自 yijianhuanbao（源文件 SdVolumeMapping.cs，sha256 4649eec473d4…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace SwitchCfwWizard.SdReset;

internal static class SdVolumeMapping
{
	internal const uint Extents = 5636096u;

	internal const uint DeviceNumber = 2953344u;

	internal const uint PartitionInfo = 458824u;

	internal static SdVolume? ReadEnumerated(string name, Func<string[]> readMounts, Func<string[], SdVolume> readMapping)
	{
		string[] array;
		try
		{
			array = readMounts();
		}
		catch (Win32Exception ex) when (SdDeviceAccess.IsUnavailable(ex.NativeErrorCode))
		{
			return null;
		}
		catch (Win32Exception ex2)
		{
			throw new Win32Exception(ex2.NativeErrorCode, "读取卷挂载点失败（" + name + "）：" + ex2.Message);
		}
		try
		{
			return readMapping(array);
		}
		catch (Win32Exception ex3) when (ex3.NativeErrorCode == 1005 && array.Length == 0)
		{
			return null;
		}
		catch (Win32Exception ex4) when (((Func<bool>)delegate
		{
			// Could not convert BlockContainer to single expression
			int nativeErrorCode = ex4.NativeErrorCode;
			return ((nativeErrorCode == 1 || nativeErrorCode == 50) ? true : false) || SdDeviceAccess.IsUnavailable(ex4.NativeErrorCode);
		}).Invoke())
		{
			return null;
		}
		catch (Win32Exception ex5)
		{
			throw new Win32Exception(ex5.NativeErrorCode, $"读取卷设备映射失败（{name}，挂载点：{string.Join("、", array)}）：{ex5.Message}");
		}
	}

	internal static SdVolume Read(string name, string[] mounts, Func<uint, byte[]> query)
	{
		byte[] extents;
		try
		{
			extents = query(5636096u);
		}
		catch (Win32Exception ex) when (((Func<bool>)delegate
		{
			// Could not convert BlockContainer to single expression
			int nativeErrorCode = ex.NativeErrorCode;
			return (nativeErrorCode == 1 || nativeErrorCode == 50 || nativeErrorCode == 1005) ? true : false;
		}).Invoke())
		{
			byte[] array = query(2953344u);
			if (array.Length < 12)
			{
				throw new IOException("磁盘设备映射数据不完整。");
			}
			if (BinaryPrimitives.ReadInt32LittleEndian(array) != 7)
			{
				throw new Win32Exception(50, "此卷不是磁盘分区。");
			}
			byte[] array2 = query(458824u);
			bool flag = array2.Length < 32 || BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan(4)) < 0 || BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan(8)) <= 0 || BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan(8)) != BinaryPrimitives.ReadInt32LittleEndian(array2.AsSpan(24));
			if (!flag)
			{
				int num = BinaryPrimitives.ReadInt32LittleEndian(array2);
				bool flag2 = (uint)num <= 1u;
				flag = !flag2;
			}
			if (flag)
			{
				throw new IOException("无法确认未格式化卷对应的物理分区。");
			}
			long num2 = BinaryPrimitives.ReadInt64LittleEndian(array2.AsSpan(8));
			long num3 = BinaryPrimitives.ReadInt64LittleEndian(array2.AsSpan(16));
			if (num2 < 0 || num3 <= 0 || num2 % 512 != 0L || num3 % 512 != 0L || num2 > long.MaxValue - num3)
			{
				throw new IOException("未格式化卷的分区范围无效。");
			}
			return new SdVolume(name, new int[1] { BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan(4)) }, mounts, num2, num3);
		}
		if (extents.Length < 8)
		{
			throw new IOException("磁盘卷映射数据不完整。");
		}
		int num4 = BinaryPrimitives.ReadInt32LittleEndian(extents);
		if (num4 < 1 || num4 > 1024 || extents.Length < 8 + num4 * 24)
		{
			throw new IOException("磁盘卷映射无效。");
		}
		return new SdVolume(name, (from i in Enumerable.Range(0, num4)
			select BinaryPrimitives.ReadInt32LittleEndian(extents.AsSpan(8 + i * 24))).Distinct().ToArray(), mounts, (num4 == 1) ? BinaryPrimitives.ReadInt64LittleEndian(extents.AsSpan(16)) : (-1), (num4 == 1) ? BinaryPrimitives.ReadInt64LittleEndian(extents.AsSpan(24)) : (-1));
	}
}
