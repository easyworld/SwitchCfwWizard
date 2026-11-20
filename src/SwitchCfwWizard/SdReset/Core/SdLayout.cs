// 移植自 yijianhuanbao（源文件 SdLayout.cs，sha256 c0f62273fbe9…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SwitchCfwWizard.SdReset;

public sealed record SdLayout(long DiskBytes, SdFormatOptions Options, SdPartition[] Partitions)
{
	public SdPartition Main => Partitions[0];

	public const long MiB = 1048576L;

	public const long GiB = 1073741824L;

	public const long AlignmentSectors = 32768L;

	public const byte HiddenEmuPartitionType = 240;

	public static SdLayout Create(long diskBytes, SdFormatOptions options)
	{
		ArgumentNullException.ThrowIfNull(options, "options");
		if (!Enum.IsDefined(options.FileSystem) || !Enum.IsDefined(options.FullSize))
		{
			throw new InvalidDataException("不支持的文件系统或 emuMMC 容量。");
		}
		if (diskBytes < 536870912 || diskBytes % 512 != 0L || diskBytes / 512 > uint.MaxValue)
		{
			throw new InvalidDataException("此功能支持 512 MiB 至小于 2 TiB、512 字节逻辑扇区的 TF 卡。");
		}
		int emuCount = options.EmuCount;
		if ((emuCount < 0 || emuCount > 2) ? true : false)
		{
			throw new InvalidDataException("按 hekate 分区管理器的布局，emuMMC 分区数量支持 1 个或 2 个。");
		}
		bool flag = options.EmuCount > 0 && options.FullSize == SdFullEmuSize.None;
		if (flag)
		{
			emuCount = options.EmuSizeGiB;
			bool flag2 = ((emuCount < 4 || emuCount > 24) ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			throw new InvalidDataException("缩小容量的 emuMMC 支持每个 4～24 GiB，步长 1 GiB。");
		}
		long num = diskBytes / 512;
		long num2 = num / 32768 * 32768;
		long num3 = options.FullSize switch
		{
			SdFullEmuSize.Original32Gb => 29856L, 
			SdFullEmuSize.Oled64Gb => 59680L, 
			_ => (long)options.EmuSizeGiB * 1024L + 32, 
		} * 2048;
		long num4 = ((options.EmuCount == 0) ? num : (num2 - num3 * options.EmuCount));
		long num5 = num4 - 32768;
		if (num5 <= 0 || (options.EmuCount > 0 && num5 <= 4194304))
		{
			throw new InvalidDataException("TF 卡容量不足：emuMMC 分区之外，主数据分区需保留超过 2 GiB。");
		}
		emuCount = 1;
		List<SdPartition> list = new List<SdPartition>(emuCount);
		CollectionsMarshal.SetCount(list, emuCount);
		Span<SdPartition> span = CollectionsMarshal.AsSpan(list);
		int index = 0;
		span[index] = new SdPartition(1, (byte)((options.FileSystem == SdFileSystem.Fat32) ? 12 : 7), 32768L, num5);
		List<SdPartition> list2 = list;
		for (int i = 0; i < options.EmuCount; i++)
		{
			long sectorCount = num3 - ((i == options.EmuCount - 1) ? 2048 : 0);
			list2.Add(new SdPartition(i + 2, 240, num4 + i * num3, sectorCount));
		}
		return new SdLayout(diskBytes, options, list2.ToArray());
	}

	public byte[] CreateMbr(uint signature)
	{
		byte[] array = new byte[512];
		BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(440), signature);
		SdPartition[] partitions = Partitions;
		foreach (SdPartition sdPartition in partitions)
		{
			Span<byte> span = array.AsSpan(446 + (sdPartition.Index - 1) * 16, 16);
			span[1] = (span[5] = 254);
			span[2] = (span[3] = (span[6] = (span[7] = byte.MaxValue)));
			span[4] = sdPartition.Type;
			BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, span.Length - 8), checked((uint)sdPartition.StartSector));
			BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, span.Length - 12), checked((uint)sdPartition.SectorCount));
		}
		array[510] = 85;
		array[511] = 170;
		return array;
	}
}
