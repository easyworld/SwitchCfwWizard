// 移植自 yijianhuanbao（源文件 SdResetStatus.cs，sha256 7e46a8e19631…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
namespace SwitchCfwWizard.SdReset;

internal sealed record SdResetStatus(string Stage, bool Finished = false, string? Error = null, bool Written = false, string? Root = null, uint Signature = 0u);
