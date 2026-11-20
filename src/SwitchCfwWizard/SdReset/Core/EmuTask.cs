// 移植自 yijianhuanbao（源文件 EmuTask.cs，sha256 0498845e3424…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace SwitchCfwWizard.SdReset;

public sealed record EmuTask(uint DiskSectors, uint DiskSignature, uint MainStart, uint MainCount, uint RawStart, uint RawCount, SdFullEmuSize FullSize, uint PayloadLength, byte[] PayloadHash, byte[] Id)
{
	public const string DirectoryPath = "bootloader/ns-toolbox";

	public const string ReadyPath = "ready.flag";

	public const string BackupPath = "payload.bin.backup";

	public const string RestorePath = "payload.bin.restore";

	public const string RestoreCheckpoint = "ready.flag.restore";

	public const int ManifestLength = 128;

	public byte[] Encode()
	{
		byte[] array = new byte[128];
		"NSTASK01"u8.CopyTo(array);
		uint[] array2 = new uint[9]
		{
			1u,
			DiskSectors,
			DiskSignature,
			MainStart,
			MainCount,
			RawStart,
			RawCount,
			(uint)FullSize,
			PayloadLength
		};
		for (int i = 0; i < array2.Length; i++)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(8 + i * 4), array2[i]);
		}
		if (PayloadHash.Length != 32 || Id.Length != 16)
		{
			throw new InvalidDataException("自动创建任务校验信息无效。");
		}
		PayloadHash.CopyTo(array, 44);
		Id.CopyTo(array, 76);
		BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(124), Crc32(array.AsSpan(0, 124)));
		return array;
	}

	public static EmuTask Decode(ReadOnlySpan<byte> data)
	{
		byte[] fields;
		if (data.Length == 128 && data.Slice(0, 8).SequenceEqual("NSTASK01"u8))
		{
			if (BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, data.Length - 8)) == 1)
			{
				if (BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(124, data.Length - 124)) == Crc32(data.Slice(0, 124)) && data.Slice(92, 32).IndexOfAnyExcept((byte)0) < 0)
				{
					fields = data.ToArray();
					EmuTask emuTask = new EmuTask(U(12), U(16), U(20), U(24), U(28), U(32), (SdFullEmuSize)U(36), U(40), data.Slice(44, 32).ToArray(), data.Slice(76, 16).ToArray());
					emuTask.ValidateFields();
					return emuTask;
				}
			}
		}
		throw new InvalidDataException("自动创建任务损坏或版本不受支持，请恢复原启动文件。");
		uint U(int at)
		{
			return BinaryPrimitives.ReadUInt32LittleEndian(fields.AsSpan(at));
		}
	}

	private void ValidateFields()
	{
		bool flag = DiskSectors == 0 || DiskSignature == 0 || (ulong)MainStart != 32768 || MainCount <= 4194304 || RawStart != (long)MainStart + (long)MainCount || RawCount < 8450048 || (ulong)((long)RawStart + (long)RawCount) > (ulong)DiskSectors || !Enum.IsDefined(FullSize);
		if (!flag)
		{
			uint payloadLength = PayloadLength;
			bool flag2 = ((payloadLength < 288 || payloadLength > 196608) ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			throw new InvalidDataException("自动创建任务的分区或启动文件参数无效。");
		}
	}

	public void ValidateLayout(ReadOnlySpan<byte> mbr, long diskBytes)
	{
		ValidateFields();
		int num;
		if (diskBytes == (long)DiskSectors * 512L && mbr.Length == 512 && mbr[510] == 85 && mbr[511] == 170)
		{
			num = ((BinaryPrimitives.ReadUInt32LittleEndian(mbr.Slice(440, mbr.Length - 440)) != DiskSignature) ? 1 : 0);
		}
		else
		{
			num = 1;
		}
		bool flag = (byte)num != 0;
		if (!flag)
		{
			byte b = mbr[450];
			bool flag2 = ((b == 7 || b == 12) ? true : false);
			flag = !flag2;
		}
		if (!flag && mbr[466] == 240)
		{
			if (BinaryPrimitives.ReadUInt32LittleEndian(mbr.Slice(454, mbr.Length - 454)) == MainStart)
			{
				if (BinaryPrimitives.ReadUInt32LittleEndian(mbr.Slice(458, mbr.Length - 458)) == MainCount)
				{
					if (BinaryPrimitives.ReadUInt32LittleEndian(mbr.Slice(470, mbr.Length - 470)) == RawStart)
					{
						if (BinaryPrimitives.ReadUInt32LittleEndian(mbr.Slice(474, mbr.Length - 474)) == RawCount && mbr.Slice(478, 32).IndexOfAnyExcept((byte)0) < 0)
						{
							return;
						}
					}
				}
			}
		}
		throw new IOException("TF 卡分区与自动创建任务不一致，请重新检测原 TF 卡。");
	}

	public void VerifyPayload(byte[] payload)
	{
		if (payload.Length != PayloadLength || !SHA256.HashData(payload).SequenceEqual(PayloadHash))
		{
			throw new IOException("原启动文件校验失败，已停止自动恢复。");
		}
	}

	public static void CheckPackage(PackageSnapshot package)
	{
		Package.CheckSnapshot(package, default, verifyContents: true);
		foreach (string item in package.Directories.Concat(package.Files.Select((PackageFile f) => f.Relative)))
		{
			if (IsReserved(item))
			{
				throw new InvalidDataException("整合包包含自动创建任务保留文件：" + item);
			}
		}
		byte[] array = File.ReadAllBytes(PathPolicy.SafePath(package.Root, "payload.bin"));
		int num = array.Length;
		bool flag = ((num < 288 || num > 196608) ? true : false);
		if (flag || ((ReadOnlySpan<byte>)array.AsSpan(0, Math.Min(512, array.Length))).IndexOf("ICTC"u8) < 0 || !((ReadOnlySpan<byte>)array.AsSpan(array.Length - 6)).SequenceEqual("ICTC49"u8))
		{
			throw new InvalidDataException("重建功能要求整合包的 payload.bin 为可直接启动的 hekate。");
		}
	}

	public static bool IsReserved(string path)
	{
		if (!PathPolicy.Within(path, "bootloader/ns-toolbox"))
		{
			return new[] { "ready.flag", "ready.flag.tmp", "ready.flag.restore", "payload.bin.backup", "payload.bin.restore" }.Contains(path, PathPolicy.Comparer);
		}
		return true;
	}

	public static bool HasPending(string root)
	{
		return new string[6] { "ready.flag", "ready.flag.tmp", "ready.flag.restore", "payload.bin.backup", "payload.bin.restore", "bootloader/ns-toolbox" }.Any((string p) => PathPolicy.Exists(PathPolicy.SafePath(root, p)));
	}

	public static EmuTask Read(string root)
	{
		EmuTask? emuTask = null;
		string[] array = new string[3] { "bootloader/ns-toolbox/task.bin", "ready.flag", "ready.flag.restore" };
		foreach (string relative in array)
		{
			string path = PathPolicy.SafePath(root, relative);
			if (File.Exists(path))
			{
				EmuTask emuTask2;
				try
				{
					emuTask2 = Decode(File.ReadAllBytes(path));
				}
				catch (InvalidDataException)
				{
					continue;
				}
				if (emuTask != null && !emuTask2.Encode().SequenceEqual(emuTask.Encode()))
				{
					throw new IOException("TF 卡上的任务标记不一致，请停止操作并检查此卡。");
				}
				emuTask = emuTask2;
			}
		}
		return emuTask ?? throw new IOException("未找到有效的自动创建任务，请恢复原启动文件。");
	}

	public static EmuTask Create(SdLayout layout, ReadOnlySpan<byte> mbr, byte[] payload)
	{
		if (layout.Options.EmuCount != 1)
		{
			throw new InvalidDataException("自动创建仅支持一个 emuMMC 分区。");
		}
		SdPartition main = layout.Main;
		SdPartition sdPartition = layout.Partitions[1];
		EmuTask emuTask = new EmuTask(checked((uint)unchecked(layout.DiskBytes / 512)), BinaryPrimitives.ReadUInt32LittleEndian(mbr.Slice(440, mbr.Length - 440)), (uint)main.StartSector, (uint)main.SectorCount, (uint)sdPartition.StartSector, (uint)sdPartition.SectorCount, layout.Options.FullSize, (uint)payload.Length, SHA256.HashData(payload), RandomNumberGenerator.GetBytes(16));
		emuTask.ValidateLayout(mbr, layout.DiskBytes);
		return emuTask;
	}

	public static uint Crc32(ReadOnlySpan<byte> data)
	{
		uint num = uint.MaxValue;
		ReadOnlySpan<byte> readOnlySpan = data;
		for (int i = 0; i < readOnlySpan.Length; i++)
		{
			byte b = readOnlySpan[i];
			num ^= b;
			for (int j = 0; j < 8; j++)
			{
				num = (num >> 1) ^ (uint)(((num & 1) != 0) ? (-306674912) : 0);
			}
		}
		return ~num;
	}
}
