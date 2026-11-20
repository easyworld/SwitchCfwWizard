// 移植自 yijianhuanbao（源文件 ServerAddressPrivacy.cs，sha256 4c480a0aef09…）。
// 只改了命名空间；逻辑与原版逐行一致 —— 改动请同时对照源项目。
using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SwitchCfwWizard.SdReset;

public static class ServerAddressPrivacy
{
	private const string Hidden = "[地址已隐藏]";

	private static readonly ConcurrentDictionary<string, Regex> hosts;

	private static readonly Regex webAddress;

	static ServerAddressPrivacy()
	{
		hosts = new ConcurrentDictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
		webAddress = new Regex("(?i)(?<![a-z0-9_])(?:https?://|www\\.)[^\\s<>\"'，。；！？）】]+", RegexOptions.CultureInvariant);
		RegisterHost("dokiss.cn");
		RegisterHost("nswiki.cn");
	}

	public static void Register(Uri? address)
	{
		if (address is { IsAbsoluteUri: true })
		{
			RegisterHost(address.IdnHost);
			RegisterHost(address.Host);
		}
	}

	private static void RegisterHost(string host)
	{
		if (host.Length != 0)
		{
			hosts.GetOrAdd(host, (string value) => new Regex("(?<![a-z0-9_-])(?:[a-z0-9-]+\\.)*" + Regex.Escape(value) + "(?::\\d{1,5})?(?![a-z0-9_-]|\\.[a-z0-9_-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
		}
	}

	public static string Hide(string text)
	{
		string text2 = webAddress.Replace(text, "[地址已隐藏]");
		foreach (Regex value in hosts.Values)
		{
			text2 = value.Replace(text2, "[地址已隐藏]");
		}
		return text2;
	}
}
