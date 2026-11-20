// 移植自 yijianhuanbao（源文件 UpdateProgress.cs，sha256 51d824682e6d…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;

namespace SwitchCfwWizard.SdReset;

public record UpdateProgress(string Stage, string Path = "", long Done = 0L, long Total = 0L, int Files = 0, int TotalFiles = 0, string CountLabel = "文件", bool IsDownload = false)
{
	public int Percent
	{
		get
		{
			if (Total != 0L)
			{
				return (int)Math.Min(100m, (decimal)Done * 100m / (decimal)Total);
			}
			return 0;
		}
	}
}
