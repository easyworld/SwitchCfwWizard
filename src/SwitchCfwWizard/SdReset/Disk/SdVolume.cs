// 移植自 yijianhuanbao（源文件 SdVolume.cs，sha256 25eea763c938…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
namespace SwitchCfwWizard.SdReset;

internal sealed record SdVolume(string Name, int[] Disks, string[] Mounts, long Offset, long Length, string DeviceName = "");
