using System;

namespace SwitchCfwWizard.SdReset;

/// <summary>
/// 「拖动分配容量」的全部换算（纯函数，离线可测）。
///
/// 为什么单独抽出来：这条交互的两个方向都很容易写反 ——
///   ① 拖动位置 → emuMMC 容量（手指往右拖 = 主分区变大 = emuMMC 变小）；
///   ② emuMMC 容量 → 分隔条位置（重绘时用）。
/// 两者必须互为逆运算，任何一侧写错，界面上都只是「拖起来有点怪」，
/// 而真正写进卡里的分区表却是另一套尺寸 —— 这种错只能靠离线用例钉住。
///
/// 约束来自 hekate 的布局（与 <see cref="SdLayout"/> 同一套常量，这里刻意重复写死，
/// 因为这里是**交互边界**：要让「拖不出来非法值」这件事在拖动时就成立，而不是等到点了下一步才报错）：
///   · emuMMC 缩小容量：4～24 GiB，步长 1 GiB；
///   · 主数据分区必须 &gt; 2 GiB（源项目同一条约束，见 SdLayout.Create）；
///   · 可分配区按 32768 扇区对齐。
/// </summary>
internal static class SdDragPlan
{
    internal const int MinEmuGiB = 4;

    internal const int MaxEmuGiB = 24;

    private const long AlignmentSectors = 32768L;

    /// <summary>主分区至少要留下的扇区数：源项目要求 <c>num5 &gt; 4194304</c>（= 2 GiB），所以下界取 4194305。</summary>
    private const long MinMainSectors = 4194305L;

    private const long EmuOverheadMiB = 32L;

    private const long SectorsPerMiB = 2048L;

    internal static long DiskSectors(long diskBytes) => diskBytes / 512;

    /// <summary>这张卡最多能分出多大的缩小版 emuMMC（GiB）。返回 0 = 卡太小，做不了。</summary>
    internal static int MaxAllowedGiB(long diskBytes)
    {
        if (diskBytes < 536870912 || diskBytes % 512 != 0)
        {
            return 0;
        }

        var aligned = DiskSectors(diskBytes) / AlignmentSectors * AlignmentSectors;
        var maxEmuSectors = aligned - AlignmentSectors - MinMainSectors;
        if (maxEmuSectors <= 0)
        {
            return 0;
        }

        var gib = (int)((maxEmuSectors / SectorsPerMiB - EmuOverheadMiB) / 1024L);
        return Math.Clamp(gib, 0, MaxEmuGiB);
    }

    /// <summary>分隔条位置（0～1，按整盘扇区算）→ emuMMC 容量（GiB，已夹到合法区间）。</summary>
    internal static int GiBFromFraction(long diskBytes, double fraction)
    {
        var max = MaxAllowedGiB(diskBytes);
        if (max < MinEmuGiB)
        {
            return 0;
        }

        var sectors = DiskSectors(diskBytes);
        var aligned = sectors / AlignmentSectors * AlignmentSectors;
        var boundary = (long)Math.Round(Math.Clamp(fraction, 0d, 1d) * sectors);
        var emuSectors = aligned - boundary;
        var gib = (int)Math.Round((emuSectors / (double)SectorsPerMiB - EmuOverheadMiB) / 1024d);
        return Math.Clamp(gib, MinEmuGiB, max);
    }

    /// <summary>emuMMC 容量（GiB）→ 分隔条位置（0～1）。与 <see cref="GiBFromFraction"/> 互为逆运算。</summary>
    internal static double FractionForGiB(long diskBytes, int gib)
    {
        var sectors = DiskSectors(diskBytes);
        if (sectors <= 0)
        {
            return 1d;
        }

        var aligned = sectors / AlignmentSectors * AlignmentSectors;
        var emuSectors = (Math.Clamp(gib, 0, MaxEmuGiB) * 1024L + EmuOverheadMiB) * SectorsPerMiB;
        return Math.Clamp((aligned - emuSectors) / (double)sectors, 0d, 1d);
    }

    /// <summary>夹到这张卡真正能用的区间；卡太小返回 0。</summary>
    internal static int ClampGiB(long diskBytes, int gib)
    {
        var max = MaxAllowedGiB(diskBytes);
        return max < MinEmuGiB ? 0 : Math.Clamp(gib, MinEmuGiB, max);
    }
}
