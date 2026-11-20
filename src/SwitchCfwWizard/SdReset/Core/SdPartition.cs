// 移植自 yijianhuanbao（源文件 SdPartition.cs，sha256 6486f1f4e4f4…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
namespace SwitchCfwWizard.SdReset;

public sealed record SdPartition(int Index, byte Type, long StartSector, long SectorCount)
{
	public long Offset => checked(StartSector * 512);

	public long Length => checked(SectorCount * 512);

	public long EndSector => checked(StartSector + SectorCount);
}
