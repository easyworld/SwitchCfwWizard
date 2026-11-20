// 移植自 yijianhuanbao（源文件 PathPolicy.cs，sha256 4e35235b2673…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SwitchCfwWizard.SdReset;

public static class PathPolicy
{
	public const int MaxDepth = 64;

	public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

	public static string Normalize(string text)
	{
		text = text.Trim(new char[4] { ' ', '\t', '\r', '\n' }).Replace('\\', '/');
		if (text.Length == 0)
		{
			return "";
		}
		string[] array = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (array.Length == 0 || array.Length > 64)
		{
			throw new InvalidDataException("规则不能指向根目录，目录层级不能超过 64 层。");
		}
		string[] array2 = array;
		foreach (string text2 in array2)
		{
			string text3 = text2.Split('.')[0];
			bool flag = ((text2 == "." || text2 == "..") ? true : false);
			if (!flag && text2.Length <= 255 && !text2.EndsWith(' ') && !text2.EndsWith('.') && !text2.Any((char c2) => c2 < ' ' || c2 == '\u007f' || ":*?\"<>|".Contains(c2)) && !new[] { "CON", "PRN", "AUX", "NUL" }.Contains(text3, Comparer))
			{
				if (text3.Length != 4 || (!text3.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && !text3.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
				{
					continue;
				}
				char c = text3[3];
				if (c < '1' || c > '9')
				{
					continue;
				}
			}
			throw new InvalidDataException("无效的相对路径：" + text);
		}
		string text4 = string.Join('/', array);
		if (Encoding.UTF8.GetByteCount(text4) + 4 >= 512)
		{
			throw new InvalidDataException("路径过长：" + text4);
		}
		return text4;
	}

	public static bool Within(string path, string parent)
	{
		if (!Comparer.Equals(path, parent))
		{
			return path.StartsWith(parent.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	public static bool WithinAbsolute(string path, string parent)
	{
		string text = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
		string text2 = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
		string value = (Path.EndsInDirectorySeparator(text2) ? text2 : (text2 + Path.DirectorySeparatorChar));
		if (!Comparer.Equals(text, text2))
		{
			return text.StartsWith(value, StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	public static string Combine(string root, string relative)
	{
		relative = Normalize(relative);
		string fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
		if (!WithinAbsolute(fullPath, root))
		{
			throw new InvalidDataException("路径超出目标目录。\n目标目录：" + root + "\n实际路径：" + fullPath);
		}
		return fullPath;
	}

	public static string Resolve(string root, string relative)
	{
		relative = Normalize(relative);
		string text = Path.GetFullPath(root);
		EnsureNotLink(text);
		List<string> list = new List<string>();
		string[] array = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
		foreach (string part in array)
		{
			string? text2 = (Directory.Exists(text) ? Directory.EnumerateFileSystemEntries(text).FirstOrDefault((string p) => Comparer.Equals(Path.GetFileName(p), part)) : null);
			if (text2 == null && OperatingSystem.IsWindows())
			{
				string text3 = Path.Combine(text, part);
				if (Exists(text3))
				{
					StringBuilder stringBuilder = new StringBuilder(32768);
					uint longPathName = GetLongPathName(text3, stringBuilder, (uint)stringBuilder.Capacity);
					if (longPathName == 0 || longPathName >= stringBuilder.Capacity)
					{
						throw new Win32Exception(Marshal.GetLastWin32Error());
					}
					text2 = stringBuilder.ToString();
				}
			}
			text = text2 ?? Path.Combine(text, part);
			EnsureNotLink(text);
			list.Add((text2 == null) ? part : Path.GetFileName(text2));
		}
		return Normalize(string.Join('/', list));
	}

	public static bool Exists(string path)
	{
		try
		{
			File.GetAttributes(path);
			return true;
		}
		catch (FileNotFoundException)
		{
			return false;
		}
		catch (DirectoryNotFoundException)
		{
			return false;
		}
	}

	public static void EnsureNotLink(string path)
	{
		if (Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
		{
			throw new InvalidDataException("不支持符号链接或目录联接：" + path);
		}
	}

	public static string SafePath(string root, string relative)
	{
		return Combine(root, Resolve(root, relative));
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetLongPathNameW", SetLastError = true)]
	private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint capacity);
}
