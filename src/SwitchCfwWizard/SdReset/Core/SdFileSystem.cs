// 移植自 yijianhuanbao（源文件 SdFileSystem.cs，sha256 0872199842e5…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
namespace SwitchCfwWizard.SdReset;

public enum SdFileSystem
{
	Fat32,
	ExFat
}
