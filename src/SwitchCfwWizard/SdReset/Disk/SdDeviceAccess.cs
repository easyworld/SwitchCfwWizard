// 移植自 yijianhuanbao（源文件 SdDeviceAccess.cs，sha256 d29a26b07004…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.ComponentModel;
using System.Threading;

namespace SwitchCfwWizard.SdReset;

internal static class SdDeviceAccess
{
	internal static bool IsUnavailable(int code)
	{
		switch (code)
		{
		case 2:
		case 3:
		case 21:
		case 55:
		case 433:
		case 1167:
			return true;
		default:
			return false;
		}
	}

	internal static T WaitUntilReady<T>(Func<T> prepare, Action<int> retry, Action<int>? delay = null)
	{
		int[] array = new int[4] { 250, 500, 1000, 2000 };
		int num = 0;
		while (true)
		{
			try
			{
				return prepare();
			}
			catch (Win32Exception ex) when (IsUnavailable(ex.NativeErrorCode) && num < array.Length)
			{
				retry(num + 1);
				(delay ?? new Action<int>(Thread.Sleep))(array[num]);
			}
			num++;
		}
	}
}
