// 移植自 yijianhuanbao（源文件 SdResetRequest.cs，sha256 bed6aa31b8d7…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。

namespace SwitchCfwWizard.SdReset;

internal sealed record SdResetRequest(SdDevice Device, SdFormatOptions Options, bool MountOnly = false, uint ExpectedSignature = 0u);
