// 移植自 yijianhuanbao（源文件 SdResetEngine.cs，sha256 77475e3e0738…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace SwitchCfwWizard.SdReset;

internal static class SdResetEngine
{
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int DiskIo(int write, ulong sector, uint count, nint data);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int Format(uint sectors, int exfat, DiskIo callback);

	private sealed class PreparedDisk(SafeFileHandle disk) : IDisposable
	{
		internal SafeFileHandle Disk { get; } = disk;

		internal List<SafeFileHandle> Locks { get; } = new List<SafeFileHandle>();

		public void Dispose()
		{
			foreach (SafeFileHandle @lock in Locks)
			{
				@lock.Dispose();
			}
			Disk.Dispose();
		}
	}

	private static PreparedDisk Prepare(SdResetRequest request, Action<SdResetStatus> report)
	{
		return SdDeviceAccess.WaitUntilReady(() =>
		{
			report(new SdResetStatus("检查设备身份与分区信息"));
			List<SdVolume> list = SdDisks.EnumerateVolumes();
			PreparedDisk preparedDisk = new PreparedDisk(SdDisks.Open(request.Device.DevicePath, 3221225472u));
			try
			{
				request.Device.EnsureSame(SdDisks.Read(preparedDisk.Disk, request.Device.Number, list, SdDisks.ProtectedDisks(list)));
				report(new SdResetStatus("锁定 TF 卡，请关闭正在访问此卡的其他程序"));
				int nativeErrorCode = default;
				foreach (SdVolume item in list.Where((SdVolume v) => v.Disks.Contains(request.Device.Number)))
				{
					SafeFileHandle safeFileHandle = SdDisks.Open(item.Name.TrimEnd('\\'), 3221225472u);
					preparedDisk.Locks.Add(safeFileHandle);
					try
					{
						SdDisks.Control(safeFileHandle, 589848u);
						SdDisks.Control(safeFileHandle, 589856u);
					}
					catch (Win32Exception ex) when (((Func<bool>)delegate
					{
						// Could not convert BlockContainer to single expression
						nativeErrorCode = ex.NativeErrorCode;
						return (nativeErrorCode == 5 || (uint)(nativeErrorCode - 32) <= 1u) ? true : false;
					}).Invoke())
					{
						throw new IOException("TF 卡正在被其他程序使用。请关闭文件窗口、同步软件等后重试。", ex);
					}
				}
				List<SdVolume> list2 = SdDisks.EnumerateVolumes();
				HashSet<string> locked = (from v in list
					where v.Disks.Contains(request.Device.Number)
					select v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
				if (list2.Any((SdVolume v) => v.Disks.Contains(request.Device.Number) && !locked.Contains(v.Name)))
				{
					throw new Win32Exception(21, "TF 卡卷正在重新连接，等待卷信息稳定。");
				}
				request.Device.EnsureSame(SdDisks.Read(preparedDisk.Disk, request.Device.Number, list2, SdDisks.ProtectedDisks(list2)));
				SdDisks.Control(preparedDisk.Disk, 458788u);
				return preparedDisk;
			}
			catch
			{
				preparedDisk.Dispose();
				throw;
			}
		}, (int attempt) =>
		{
			report(new SdResetStatus($"等待 TF 卡设备就绪，正在重新检查（{attempt}/4）"));
		});
	}

	internal static void Run(SdResetRequest request, Action<SdResetStatus> report)
	{
		request.Device.EnsureResetAllowed();
		SdLayout layout = SdLayout.Create(request.Device.Capacity, request.Options);
		if (request.MountOnly)
		{
			try
			{
				report(new SdResetStatus("正在核对原 TF 卡身份与已写入的分区", Finished: false, null, Written: true));
				using (SafeFileHandle disk = SdDisks.Open(request.Device.DevicePath, 0u))
				{
					List<SdVolume> volumes = SdDisks.EnumerateVolumes();
					SdDevice sdDevice = SdDisks.Read(disk, request.Device.Number, volumes, SdDisks.ProtectedDisks(volumes));
					if (sdDevice.Identity != request.Device.Identity)
					{
						throw new IOException("TF 卡设备已更换。");
					}
					sdDevice.EnsureResetAllowed();
					byte[] array = SdDisks.ReadLayout(disk);
					if (request.ExpectedSignature == 0 || BinaryPrimitives.ReadInt32LittleEndian(array) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan(8)) != request.ExpectedSignature)
					{
						throw new IOException("TF 卡分区身份已改变，禁止沿用上次的重建任务。");
					}
				}
				report(new SdResetStatus("正在检查主数据分区与 emuMMC 隐藏状态", Finished: false, null, Written: true));
				report(new SdResetStatus("TF 卡盘符检查完成", Finished: true, null, Written: true, SdDisks.MountMain(request.Device, layout), request.ExpectedSignature));
				return;
			}
			catch (Exception ex)
			{
				report(new SdResetStatus("分区已写入，本次盘符检查未通过：" + ServerAddressPrivacy.Hide(ex.Message), Finished: true, null, Written: true, null, request.ExpectedSignature));
				return;
			}
		}
		bool flag = false;
		uint num = 0u;
		nint num2 = IntPtr.Zero;
		string stage = "准备格式化组件";
		try
		{
			num2 = NativeLibrary.Load(NativeFormatter.EnsurePath());
			Format delegateForFunctionPointer = Marshal.GetDelegateForFunctionPointer<Format>(NativeLibrary.GetExport(num2, "ns_format"));
			using PreparedDisk preparedDisk = Prepare(request, Progress);
			SafeFileHandle disk2 = preparedDisk.Disk;
			flag = true;
			SdDiskMetadata.Clear(layout.DiskBytes, (long at, byte[] data) =>
			{
				RandomAccess.Write(disk2, data, at);
			}, (string step) =>
			{
				Progress(new SdResetStatus(step, Finished: false, null, Written: true));
			});
			byte[] array2 = new byte[1048576];
			Progress(new SdResetStatus((request.Options.EmuCount == 0) ? "创建主数据分区" : "创建主数据分区和隐藏的 emuMMC 原始分区", Finished: false, null, Written: true));
			num = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4)) | 1;
			byte[] array3 = new byte[24];
			BinaryPrimitives.WriteUInt32LittleEndian(array3.AsSpan(4), num);
			SdDisks.Control(disk2, 507992u, array3);
			byte[] array4 = new byte[624];
			BinaryPrimitives.WriteInt32LittleEndian(array4.AsSpan(4), 4);
			BinaryPrimitives.WriteUInt32LittleEndian(array4.AsSpan(8), num);
			for (int num3 = 0; num3 < 4; num3++)
			{
				Span<byte> span = array4.AsSpan(48 + num3 * 144, 144);
				span[28] = 1;
				if (num3 < layout.Partitions.Length)
				{
					SdPartition sdPartition = layout.Partitions[num3];
					BinaryPrimitives.WriteInt64LittleEndian(span.Slice(8, span.Length - 8), sdPartition.Offset);
					BinaryPrimitives.WriteInt64LittleEndian(span.Slice(16, span.Length - 16), sdPartition.Length);
					BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, span.Length - 24), sdPartition.Index);
					span[32] = sdPartition.Type;
					span[34] = ((num3 == 0) ? ((byte)1) : ((byte)0));
					BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(36, span.Length - 36), checked((uint)sdPartition.StartSector));
				}
			}
			byte[] mbr = layout.CreateMbr(num);
			Progress(new SdResetStatus("写入新分区表", Finished: false, null, Written: true));
			RandomAccess.Write(disk2, mbr, 0L);
			if (request.Options.EmuCount > 0)
			{
				Progress(new SdResetStatus("初始化 emuMMC 分区保护区域", Finished: false, null, Written: true));
			}
			foreach (SdPartition item in layout.Partitions.Skip(1))
			{
				for (long num4 = 0L; num4 < 16777216; num4 += array2.Length)
				{
					RandomAccess.Write(disk2, array2, item.Offset + num4);
				}
			}
			RandomAccess.FlushToDisk(disk2);
			Progress(new SdResetStatus((request.Options.FileSystem == SdFileSystem.Fat32) ? "正在格式化主分区为 FAT32" : "正在格式化主分区为 exFAT", Finished: false, null, Written: true));
			Exception? ioError = null;
			byte[] array7;
			checked
			{
				DiskIo diskIo = (int operation, ulong sector, uint count, nint pointer) =>
				{
					try
					{
						if (operation == 2)
						{
							RandomAccess.FlushToDisk(disk2);
							return 1;
						}
						long num7 = (long)sector * 512;
						long num8 = unchecked((long)count) * 512L;
						if (unchecked(num8 <= 0 || num8 > 4194304 || num7 < 0 || num7 + num8 > layout.DiskBytes || ((num7 != 0L || num8 != 512) && (num7 < layout.Main.Offset || num7 + num8 > layout.Main.Offset + layout.Main.Length))))
						{
							throw new IOException("格式化写入范围超出主分区，已停止。");
						}
						byte[] array8 = new byte[(int)num8];
						switch (operation)
						{
						case 0:
							if (RandomAccess.Read(disk2, array8, num7) != array8.Length)
							{
								throw new IOException("读取 TF 卡时返回的数据不完整。");
							}
							Marshal.Copy(array8, 0, pointer, array8.Length);
							break;
						case 1:
							Marshal.Copy(pointer, array8, 0, array8.Length);
							if (num7 == 0L && !((ReadOnlySpan<byte>)array8.AsSpan()).SequenceEqual((ReadOnlySpan<byte>)mbr))
							{
								throw new IOException("格式化程序意外更改分区表，已停止。");
							}
							RandomAccess.Write(disk2, array8, num7);
							break;
						default:
							return 0;
						}
						return 1;
					}
					catch (Exception ex7)
					{
						if (ioError == null)
						{
							ioError = ex7;
						}
						return 0;
					}
				};
				int num5 = delegateForFunctionPointer((uint)unchecked(layout.DiskBytes / 512), (request.Options.FileSystem == SdFileSystem.ExFat) ? 1 : 0, diskIo);
				GC.KeepAlive(diskIo);
				if (ioError != null)
				{
					throw new IOException("读写 TF 卡失败：" + ioError.Message, ioError);
				}
				if (num5 != 0)
				{
					throw new IOException($"格式化未完成，文件系统错误码：{num5}。请检查 TF 卡状态后重新执行。");
				}
				Progress(new SdResetStatus("校验分区表与文件系统", Finished: false, null, Written: true));
				byte[] array5 = new byte[512];
				if (RandomAccess.Read(disk2, array5, 0L) != 512 || !((ReadOnlySpan<byte>)array5.AsSpan()).SequenceEqual((ReadOnlySpan<byte>)mbr))
				{
					throw new IOException("分区表写入校验失败。");
				}
				byte[] array6 = new byte[512];
				if (RandomAccess.Read(disk2, array6, layout.Main.Offset) != 512 || array6[510] != 85 || array6[511] != 170 || !((request.Options.FileSystem == SdFileSystem.ExFat) ? ((ReadOnlySpan<byte>)array6.AsSpan(3, 8)).SequenceEqual("EXFAT   "u8) : ((ReadOnlySpan<byte>)array6.AsSpan(82, 8)).SequenceEqual("FAT32   "u8)))
				{
					throw new IOException("主分区文件系统校验失败。");
				}
				RandomAccess.FlushToDisk(disk2);
				Progress(new SdResetStatus("刷新 Windows 分区信息", Finished: false, null, Written: true));
				SdDisks.Control(disk2, 507988u, array4);
				SdDisks.Control(disk2, 459072u);
				array7 = new byte[512];
				if (RandomAccess.Read(disk2, array7, 0L) != 512 || array7[510] != 85 || array7[511] != 170)
				{
					throw new IOException("刷新后的分区表校验失败。");
				}
			}
			for (int num6 = 0; num6 < 4; num6++)
			{
				if (array7[450 + num6 * 16] != mbr[450 + num6 * 16] || !((ReadOnlySpan<byte>)array7.AsSpan(454 + num6 * 16, 8)).SequenceEqual((ReadOnlySpan<byte>)mbr.AsSpan(454 + num6 * 16, 8)))
				{
					throw new IOException("刷新后的分区范围校验失败。");
				}
			}
		}
		catch (Exception ex2)
		{
			string text = "失败步骤：" + stage + "\n" + ServerAddressPrivacy.Hide(ex2.Message);
			for (Exception? ex3 = ex2; ex3 != null; ex3 = ex3.InnerException)
			{
				if (ex3 is Win32Exception ex4)
				{
					text += $"\n系统错误码：{ex4.NativeErrorCode}";
					break;
				}
			}
			report(new SdResetStatus("操作未完成", Finished: true, text + (flag ? "\n已开始重建分区，旧数据不会自动恢复。请重新执行完整操作。" : "\n尚未开始重建分区。"), flag));
			return;
		}
		finally
		{
			if (num2 != IntPtr.Zero)
			{
				NativeLibrary.Free(num2);
			}
		}
		report(new SdResetStatus("检查 emuMMC 隐藏状态并等待主数据分区挂载", Finished: false, null, Written: true));
		try
		{
			report(new SdResetStatus("TF 卡格式化与分区完成", Finished: true, null, flag, SdDisks.MountMain(request.Device, layout), num));
		}
		catch (Exception ex5)
		{
			string text2 = ServerAddressPrivacy.Hide(ex5.Message);
			if (ex5 is Win32Exception ex6)
			{
				text2 += $"\n系统错误码：{ex6.NativeErrorCode}";
			}
			report(new SdResetStatus("分区和文件系统已写入，但 Windows 盘符或隐藏检查未完成：" + text2, Finished: true, null, flag, null, num));
		}
		void Progress(SdResetStatus status)
		{
			stage = status.Stage;
			report(status);
		}
	}
}
