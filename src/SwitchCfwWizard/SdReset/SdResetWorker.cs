// 移植自 yijianhuanbao（源文件 SdResetWorker.cs，sha256 d53959d6ed65…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace SwitchCfwWizard.SdReset;

internal static class SdResetWorker
{
	private const string Prefix = "NsToolbox.SdReset.";

	internal static async Task ExecuteAsync(SdResetRequest request, Action<SdResetStatus> report)
	{
		string text = "NsToolbox.SdReset." + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
		using WindowsIdentity identity = WindowsIdentity.GetCurrent();
		SecurityIdentifier securityIdentifier = identity.User ?? throw new IOException("无法确认当前 Windows 用户。");
		PipeSecurity pipeSecurity = new PipeSecurity();
		pipeSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
		pipeSecurity.SetOwner(securityIdentifier);
		pipeSecurity.AddAccessRule(new PipeAccessRule(securityIdentifier, PipeAccessRights.FullControl, AccessControlType.Allow));
		pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
		using NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(text, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, pipeSecurity);
		ProcessStartInfo processStartInfo = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("无法定位格式化辅助进程。"))
		{
			UseShellExecute = true,
			Verb = "runas",
			WorkingDirectory = AppContext.BaseDirectory
		};
		processStartInfo.ArgumentList.Add("--sd-reset-worker");
		processStartInfo.ArgumentList.Add(text);
		processStartInfo.ArgumentList.Add(Environment.ProcessId.ToString());
		using Process worker = Process.Start(processStartInfo) ?? throw new IOException("无法启动格式化辅助进程。");
		using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2L));
		Task connect = pipe.WaitForConnectionAsync(timeout.Token);
		Task exited = worker.WaitForExitAsync();
		if (await Task.WhenAny(connect, exited) == exited && !pipe.IsConnected)
		{
			throw new IOException("格式化辅助进程未能启动，尚未操作 TF 卡。");
		}
		await connect;
		if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != worker.Id)
		{
			throw new IOException("格式化进程身份不匹配，已停止。");
		}
		using StreamReader reader = new StreamReader(pipe, null, detectEncodingFromByteOrderMarks: true, -1, leaveOpen: true);
		using StreamWriter writer = new StreamWriter(pipe, null, -1, leaveOpen: true)
		{
			AutoFlush = true
		};
		await writer.WriteLineAsync(JsonSerializer.Serialize(request));
		while (true)
		{
			string? text2 = await reader.ReadLineAsync();
			if (text2 == null)
			{
				break;
			}
			SdResetStatus sdResetStatus = JsonSerializer.Deserialize<SdResetStatus>(text2) ?? throw new IOException("格式化进程返回了无效状态。");
			report(sdResetStatus);
			if (sdResetStatus.Finished)
			{
				await exited;
				return;
			}
		}
		throw new IOException("格式化进程意外中断，请检查 TF 卡后重新执行完整操作。");
	}

	internal static int Run(string[] arguments)
	{
		if (arguments.Length != 3 || !arguments[1].StartsWith("NsToolbox.SdReset.", StringComparison.Ordinal) || arguments[1].Length != "NsToolbox.SdReset.".Length + 48 || !int.TryParse(arguments[2], out var result))
		{
			return 2;
		}
		using WindowsIdentity ntIdentity = WindowsIdentity.GetCurrent();
		if (!new WindowsPrincipal(ntIdentity).IsInRole(WindowsBuiltInRole.Administrator))
		{
			return 2;
		}
		try
		{
			StreamWriter writer;
			using (NamedPipeClientStream namedPipeClientStream = new NamedPipeClientStream(".", arguments[1], PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification))
			{
				namedPipeClientStream.Connect(15000);
				if (!GetNamedPipeServerProcessId(namedPipeClientStream.SafePipeHandle, out var pid) || pid != result)
				{
					return 2;
				}
				using Process process = Process.GetProcessById(result);
				if (!string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
				{
					return 2;
				}
				using StreamReader streamReader = new StreamReader(namedPipeClientStream, null, detectEncodingFromByteOrderMarks: true, -1, leaveOpen: true);
				writer = new StreamWriter(namedPipeClientStream, null, -1, leaveOpen: true)
				{
					AutoFlush = true
				};
				try
				{
					string text = streamReader.ReadLine() ?? throw new IOException("未收到分区计划。");
					if (text.Length > 32768)
					{
						return 2;
					}
					SdResetRequest request = JsonSerializer.Deserialize<SdResetRequest>(text) ?? throw new IOException("分区计划无效。");
					try
					{
						using Mutex mutex = new Mutex(initiallyOwned: false, "Global\\NsToolbox.SdReset");
						bool flag;
						try
						{
							flag = mutex.WaitOne(0);
						}
						catch (AbandonedMutexException)
						{
							flag = true;
						}
						if (!flag)
						{
							throw new IOException("另一个 TF 卡重置操作仍在执行，请等待完成后重试。");
						}
						try
						{
							SdResetEngine.Run(request, Send);
						}
						finally
						{
							mutex.ReleaseMutex();
						}
					}
					catch (Exception ex2)
					{
						Send(new SdResetStatus("操作未完成", Finished: true, ServerAddressPrivacy.Hide(ex2.Message)));
						return 1;
					}
					return 0;
				}
				finally
				{
					if (writer != null)
					{
						((IDisposable)writer).Dispose();
					}
				}
			}
			void Send(SdResetStatus value)
			{
				try
				{
					writer.WriteLine(JsonSerializer.Serialize(value));
				}
				catch (IOException)
				{
				}
			}
		}
		catch (Exception)
		{
			return 1;
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
