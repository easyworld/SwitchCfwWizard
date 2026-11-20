// 「把 out 复制进 TF 卡」的判定规则。
//
// 全部做成**纯函数**：这个功能里唯一不可逆的动作就是删除，
// 而删除的依据就是「这个名字算不算保留」—— 它必须能离线穷举，不能靠点一次窗口去试。

using System;
using System.Linq;

namespace SwitchCfwWizard.SdReset;

internal static class CardCopyPlan
{
	/// <summary>
	/// 卡上要保留的三个目录（用户 2026-11-20 指定）。
	/// 顺序即界面上的展示顺序；不存在就跳过，不当错误。
	/// </summary>
	internal static readonly string[] KeepNames = { "Nintendo", "emuMMC", "JKSV" };

	/// <summary>
	/// 这个名字在卡上是否要保留。
	///
	/// ⚠️ 必须是**整名相等**（忽略大小写），不能写成 <c>StartsWith</c>：
	/// <c>Nintendo_backup</c>、<c>emuMMC-old</c>、<c>JKSV2</c> 这些名字一旦被当同伙留下，
	/// 用户看到的就是「我选了清空，东西还在」—— 而这不是能用日志解释过去的事。
	/// </summary>
	internal static bool IsKept(string name)
		=> KeepNames.Any(keep => string.Equals(keep, name, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// 这一项在卡上是否要保留 —— **必须同时是目录**。
	///
	/// 用户 2026-11-20 明确要求「只保留三个文件夹」：卡根上若有个**同名文件**
	/// （比如手滑把 `Nintendo` 存成了一个无扩展名的文件），它不是那三个目录，
	/// 留着只会挡住新内容；而且 `Nintendo` 一旦是文件，`atmosphere` 之类照样进了
	/// 一张坏卡。所以名字对上还不够，类型也得对上。
	/// </summary>
	internal static bool IsKept(string name, bool isDirectory)
		=> isDirectory && IsKept(name);

	/// <summary>这一项是否要删除。与 <see cref="IsKept(string)"/> 严格互补。</summary>
	internal static bool ShouldDelete(string name) => !IsKept(name);

	/// <summary>
	/// 生成完之后**要不要打开输出目录**（用户 2026-11-20 明确要求：探测之后再决定）。
	///
	/// 三条路：
	///   · 没检测到可写的卡 → 打开（不然用户不知道东西生成在哪）；
	///   · 检测到卡、用户选「暂不复制」→ 打开（他自己拖进卡里）；
	///   · 检测到卡、用户选「复制进去」→ **不打开**（复制窗口接手，弹一个资源管理器只是干扰）。
	///
	/// 做成纯函数是为了能穷举：这条判定只有三种输入组合，而它影响的正是「用户眼前有没有窗口」，
	/// 靠点一次界面去验很不划算。
	/// </summary>
	internal static bool ShouldOpenOutputFolder(bool cardUsable, bool userChoseCopy)
		=> !(cardUsable && userChoseCopy);

	/// <summary>
	/// 复制前至少要有的空闲字节：out 的总大小 + 一点余量。
	/// 余量按 1% 或 16 MiB 取大者 —— FAT32 的簇对齐会让实际占用略大于文件总字节。
	/// </summary>
	internal static long RequiredFreeBytes(long outputBytes)
		=> outputBytes + Math.Max(outputBytes / 100, 16L * 1024 * 1024);

	/// <summary>
	/// 目标路径是否安全可写：必须是卡根之下，且不能靠 `..` 跑出去。
	/// <paramref name="relative"/> 来自我们自己的目录遍历，但复制这一步一旦写错就是把文件写到别处。
	///
	/// ⚠️ 卡根常常就是**盘符根**（`H:\`）。`TrimEndingDirectorySeparator("H:\")` 对根路径
	/// 会**原样返回**（根上的分隔符是路径的一部分），再无条件补一个分隔符就变成 `H:\\`，
	/// 于是 `H:\.wizard-manifest.json` 判成「不在卡内」—— 用户实际看到的就是
	/// 「目标路径不在卡内：H:\.wizard-manifest.json」。所以补分隔符前必须先看有没有。
	/// </summary>
	internal static bool IsInsideRoot(string root, string candidate)
	{
		var normalizedRoot = NormalizeRoot(root);
		var normalized = System.IO.Path.GetFullPath(candidate);

		return normalized.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>卡根的标准写法：绝对路径 + **恰好一个**结尾分隔符（`H:\` 不再重复补）。</summary>
	internal static string NormalizeRoot(string root)
	{
		var trimmed = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));

		return trimmed.EndsWith(System.IO.Path.DirectorySeparatorChar)
			? trimmed
			: trimmed + System.IO.Path.DirectorySeparatorChar;
	}
}
