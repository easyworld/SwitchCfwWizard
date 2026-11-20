// 移植自 yijianhuanbao（源文件 SdFormatOptions.cs，sha256 066da1324bc9…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
namespace SwitchCfwWizard.SdReset;

public sealed record SdFormatOptions(SdFileSystem FileSystem = SdFileSystem.Fat32, int EmuCount = 1, int EmuSizeGiB = 12, SdFullEmuSize FullSize = SdFullEmuSize.None);
