// 移植自 yijianhuanbao（源文件 SdDevice.cs，sha256 da4e868c1ed0…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.IO;

namespace SwitchCfwWizard.SdReset;

public sealed record SdDevice(int Number, string Vendor, string Model, string Serial, int BusType, long Capacity, int SectorSize, string Identity, string LayoutIdentity, string[] MountPoints, bool Protected, string BlockReason)
{
	public string DevicePath => "\\\\.\\PhysicalDrive" + Number;

	public string Display => $"磁盘 {Number} · {Vendor} {Model} · {(double)Capacity / 1073741824.0:0.0} GiB · " + ((MountPoints.Length == 0) ? "无盘符" : string.Join("、", MountPoints));

	public void EnsureResetAllowed()
	{
		if (Number < 0 || Protected)
		{
			throw new IOException((BlockReason.Length > 0) ? BlockReason : "该设备不能执行整卡重置。");
		}
		int busType = BusType;
		if ((busType != 7 && (uint)(busType - 12) > 1u) || 1 == 0)
		{
			throw new IOException("仅支持 USB 读卡器、SD/MMC 读卡器或 hekate TF 卡挂载设备。");
		}
		if (Vendor.Contains("hekate", StringComparison.OrdinalIgnoreCase) && !Model.Trim().Equals("SD RAW", StringComparison.OrdinalIgnoreCase))
		{
			throw new IOException("当前 hekate 挂载的是 eMMC 或 emuMMC 局部区域。请在 hekate 中选择 USB 工具 → TF 卡。");
		}
		if (SectorSize != 512)
		{
			throw new IOException("该设备没有提供 512 字节逻辑扇区，无法按 hekate 的 SD 分区布局操作。");
		}
		SdLayout.Create(Capacity, new SdFormatOptions(SdFileSystem.Fat32, 0));
	}

	public void EnsureSame(SdDevice current)
	{
		if (Number != current.Number || Identity != current.Identity || Capacity != current.Capacity || SectorSize != current.SectorSize || LayoutIdentity != current.LayoutIdentity)
		{
			throw new IOException("TF 卡已断开、更换或分区发生变化，请重新选择设备并确认。");
		}
		current.EnsureResetAllowed();
	}
}
