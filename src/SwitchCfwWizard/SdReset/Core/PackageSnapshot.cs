// 移植自 yijianhuanbao（源文件 PackageSnapshot.cs，sha256 448389758545…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System.Linq;

namespace SwitchCfwWizard.SdReset;

public record PackageSnapshot(string Root, string[] Directories, PackageFile[] Files)
{
	public long TotalBytes => Files.Sum((PackageFile f) => f.Length);
}
