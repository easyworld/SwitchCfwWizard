// 移植自 yijianhuanbao（源文件 PackageFile.cs，sha256 226a4598b4a0…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;

namespace SwitchCfwWizard.SdReset;

public record PackageFile(string Relative, long Length, DateTime Modified, string Hash);
