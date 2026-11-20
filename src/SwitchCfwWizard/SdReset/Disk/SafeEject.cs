// 移植自 yijianhuanbao（源文件 SafeEject.cs，sha256 1a6d9cd510d5…）。
// 2026-11-20 修改：原版只走「锁卷 + 卸载 + IOCTL_STORAGE_EJECT_MEDIA」，
// 在 USB 读卡器上最后一步多半直接返回「不支持」—— 实测（.workbuddy-ai/scratch/eject-probe）
// H:\ 上卷能锁、能卸载，失败的是 EJECT_MEDIA。现在改成**先走 Windows 自己那条路**
// （CM_Request_Device_Eject，也就是「安全删除硬件」用的那个），失败再退回原来的 IOCTL 流程。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SwitchCfwWizard.SdReset;

/// <summary>
/// 安全移除可移动卷/设备。
///
/// 原程序通过解包并调用内嵌的 RemoveDrive V3.4.6 (x64) exe 实现，移植时换成了进程内实现。
/// 那条实现（锁卷 → 卸载 → IOCTL_STORAGE_EJECT_MEDIA）在**光驱**上是对的，
/// 在 USB 读卡器 / U 盘上经常在最后一步收到 ERROR_INVALID_FUNCTION —— 因为这类设备的
/// 驱动不实现 EJECT_MEDIA，Windows 自己的「安全删除硬件」也不是用它：
///
///   现在的主路径（Step 3）：
///     IOCTL_STORAGE_GET_DEVICE_NUMBER  → 拿到 PhysicalDriveN
///     SetupAPI 枚举 GUID_DEVINTERFACE_DISK，按设备号匹配 → 取 devnode 实例 ID
///     CM_Locate_DevNode + CM_Request_Device_Eject  → 由 PnP 管理器请求移除
///     （失败会返回 veto 类型与**占用的进程名**，这比「代码 1」有用得多）
///
///   兜底路径（Step 4）：原来的 flush → 锁卷 → 卸载 → IOCTL_STORAGE_EJECT_MEDIA。
///
/// 两条路都失败时，异常消息里会带上**每一步的错误码**，而不是一句「可能被占用」。
/// </summary>
internal static class SafeEject
{
    private const uint GenericRead = 0x80000000u;
    private const uint GenericWrite = 0x40000000u;
    private const uint FileShareRead = 0x00000001u;
    private const uint FileShareWrite = 0x00000002u;
    private const uint OpenExisting = 3u;

    private const uint FsctlLockVolume = 0x00090018u;
    private const uint FsctlUnlockVolume = 0x0009001Cu;
    private const uint FsctlDismountVolume = 0x00090020u;
    private const uint IoctlStorageEjectMedia = 0x002D4804u;
    private const uint IoctlStorageMediaRemoval = 0x002D4808u;
    private const uint IoctlStorageGetDeviceNumber = 0x002D1080u;

    private const uint SemFailCriticalErrors = 0x0001u;
    private const uint SemNoOpenFileErrorBox = 0x8000u;

    private static readonly Guid DiskInterfaceClass = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

    /// <summary>
    /// 对盘符根路径（如 "H:\"）执行安全移除。失败时抛 IOException，
    /// 消息里带「哪一步、什么错误码、谁占着」。
    /// </summary>
    internal static void Eject(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new IOException("弹出失败，盘符无效。");
        }

        string spec = root.TrimEnd('\\', '/');
        if (spec.Length == 0)
        {
            throw new IOException("弹出失败，盘符无效。");
        }

        // 打开空的读卡器时不要弹系统错误框（原版 main 里同样设置）
        uint previousMode = SetErrorMode(SemFailCriticalErrors | SemNoOpenFileErrorBox);
        try
        {
            EjectCore(spec);
        }
        finally
        {
            SetErrorMode(previousMode);
        }
    }

    private static void EjectCore(string spec)
    {
        var trail = new List<string>();
        string volume = ToVolumePath(spec);

        // ── ① 打开卷并取设备号（只读动作，不需要锁，也不会影响别的程序）──
        using (SafeFileHandle vol = CreateFile(volume, GenericRead | GenericWrite,
                   FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
        {
            if (!vol.IsInvalid)
            {
                uint? number = TryGetDeviceNumber(vol);
                if (number is uint n)
                {
                    // ── ② 尽量把缓冲区刷下去、把卷卸下来（做不到不致命，后面 CM 也能弹）──
                    PrepareVolume(vol, trail);

                    // ── ③ 主路径：让 PnP 管理器把设备移除（Windows「安全删除硬件」走的也是它）──
                    if (TryRequestDeviceEject(n, trail))
                    {
                        return;
                    }

                    // ── ④ 兜底：老的 IOCTL 流程 ──
                    if (TryEjectMedia(vol, trail))
                    {
                        return;
                    }
                }
                else
                {
                    trail.Add("取设备号失败");
                }
            }
            else
            {
                trail.Add($"打不开卷 {volume}（{Describe(Marshal.GetLastWin32Error())}）");
            }
        }

        throw new IOException("弹出失败：" + string.Join("；", trail));
    }

    // ── ① 设备号 ──────────────────────────────────────────────────

    private static uint? TryGetDeviceNumber(SafeFileHandle vol)
    {
        var buf = new byte[12];
        if (!DeviceIoControl(vol, IoctlStorageGetDeviceNumber, null, 0, buf, buf.Length, out _, IntPtr.Zero))
        {
            return null;
        }

        return BitConverter.ToUInt32(buf, 4);
    }

    // ── ② 刷盘 + 锁卷 + 卸载（尽力而为）────────────────────────────

    /// <summary>
    /// 刷盘 / 锁卷 / 卸载。**任何一步失败都只记一笔，不抛** ——
    /// 这几步是为了「少丢数据」，不是「能不能弹出」的前置条件；
    /// 拿它当门槛正是原来那句「代码 1」的来源（锁卷在别的程序开着文件时必然失败）。
    /// </summary>
    private static void PrepareVolume(SafeFileHandle vol, List<string> trail)
    {
        if (!FlushFileBuffers(vol))
        {
            trail.Add($"刷盘失败（{Describe(Marshal.GetLastWin32Error())}）");
        }

        if (!DeviceIoControl(vol, FsctlLockVolume, null, 0, null, 0, out _, IntPtr.Zero))
        {
            // 常见于资源管理器正停在卡上 / 杀软正在扫；不影响后面 CM 那条路
            trail.Add($"锁卷失败（{Describe(Marshal.GetLastWin32Error())}，不影响弹出）");
            return;
        }

        if (DeviceIoControl(vol, FsctlDismountVolume, null, 0, null, 0, out _, IntPtr.Zero))
        {
            DeviceIoControl(vol, FsctlLockVolume, null, 0, null, 0, out _, IntPtr.Zero);
            DeviceIoControl(vol, FsctlUnlockVolume, null, 0, null, 0, out _, IntPtr.Zero);
        }
        else
        {
            trail.Add($"卸载卷失败（{Describe(Marshal.GetLastWin32Error())}，不影响弹出）");
            DeviceIoControl(vol, FsctlUnlockVolume, null, 0, null, 0, out _, IntPtr.Zero);
        }
    }

    // ── ③ CM_Request_Device_Eject ─────────────────────────────────

    private static bool TryRequestDeviceEject(uint diskNumber, List<string> trail)
    {
        string? instance = FindDiskInstance(diskNumber);
        if (instance is null)
        {
            trail.Add($"没找到 PhysicalDrive{diskNumber} 的设备节点");
            return false;
        }

        if (CM_Locate_DevNodeW(out uint devInst, instance, 0) != 0)
        {
            trail.Add($"定位设备节点失败（{instance}）");
            return false;
        }

        var vetoName = new StringBuilder(260);
        int cr = CM_Request_Device_EjectW(devInst, out int vetoType, vetoName, vetoName.Capacity, 0);

        if (cr == 0)
        {
            return true;
        }

        string veto = DescribeVeto(vetoType, vetoName.ToString());
        trail.Add($"系统拒绝移除（{DescribeConfigRet(cr)}{(veto.Length > 0 ? "，" + veto : string.Empty)}）");
        return false;
    }

    /// <summary>
    /// veto 类型 → 给人看的一句话。<paramref name="name"/> 是 PnP 管理器填回来的
    /// **占用者名字**（常常就是进程名，如 explorer.exe），这是最有用的信息。
    ///
    /// 纯函数：回归可以直接断言，不必插一张卡。
    /// </summary>
    internal static string DescribeVeto(int vetoType, string name)
    {
        string who = string.IsNullOrWhiteSpace(name) ? string.Empty : "：" + name.Trim();

        return vetoType switch
        {
            0 => string.Empty,                                   // TypeUnknown：没有 veto
            1 => "有旧式（非 PnP）设备依赖它" + who,
            2 => "有程序正准备关闭，稍等再试" + who,
            3 => "有程序正在使用它" + who,                          // WindowsApp：最常见，名字就是进程
            4 => "还有未关闭的文件句柄占用它" + who,                  // OutstandingOpen
            5 => "设备本身拒绝移除" + who,
            6 => "驱动拒绝移除" + who,
            7 => "设备请求非法" + who,
            8 => "电源不足，无法安全移除" + who,
            9 => "该设备不可禁用（不能这样移除）" + who,
            _ => $"系统拒绝移除（veto={vetoType}）" + who,
        };
    }

    /// <summary>CONFIGRET → 短说明。只覆盖本场景会遇到的几个，其余原样带码。</summary>
    internal static string DescribeConfigRet(int cr) => cr switch
    {
        0 => "成功",
        3 => "内存不足",
        4 => "参数错误",
        13 => "设备节点不存在（卡可能已经拔了）",
        30 => "设备节点无效",
        31 => "设备节点被禁用",
        32 => "拒绝访问（需要管理员权限）",
        33 => "设备不可移除",
        37 => "设备不支持弹出",
        _ => $"CR={cr}",
    };

    private static string Describe(int error)
        => $"错误 {error}（{new Win32Exception(error).Message}）";

    /// <summary>
    /// 按「物理盘号」找对应的磁盘设备节点实例 ID（USBSTOR\DISK&amp;… 这种）。
    ///
    /// ⚠️ 匹配必须用 IOCTL_STORAGE_GET_DEVICE_NUMBER 返回的**设备号**，不能靠盘符拼路径 ——
    /// 一个盘上可能有好几个分区，盘符只对应分区。
    /// </summary>
    private static string? FindDiskInstance(uint diskNumber)
    {
        var guid = DiskInterfaceClass;
        IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero,
            0x12 /* DIGCF_PRESENT | DIGCF_DEVICEINTERFACE */);

        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            var iface = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };

            for (uint index = 0;
                 SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref iface);
                 index++)
            {
                // ⚠️ 这一句的返回值**必须忽略**：缓冲区为 null 时它必然失败（ERROR_INSUFFICIENT_BUFFER），
                //    目的只是把需要的长度取回来。写成 `if (!call || needed <= 0) continue;` 会让
                //    每一个候选都被跳过 —— 表现就是「没找到 PhysicalDriveN 的设备节点」，
                //    而单独写的探针（忽略了返回值）明明找得到。
                SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, out int needed, IntPtr.Zero);
                if (needed <= 0)
                {
                    continue;
                }

                IntPtr detail = Marshal.AllocHGlobal(needed);
                IntPtr infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SpDevInfoData>());
                try
                {
                    // cbSize 在 64 位上是 8（4 字节 cbSize + 对齐），不能写死 4 或 6
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    Marshal.StructureToPtr(new SpDevInfoData { cbSize = Marshal.SizeOf<SpDevInfoData>() },
                        infoPtr, false);

                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref iface, detail, needed, out _, infoPtr))
                    {
                        continue;
                    }

                    string? path = Marshal.PtrToStringUni(detail + 4);
                    if (path is null)
                    {
                        continue;
                    }

                    using SafeFileHandle handle = CreateFile(path, 0,
                        FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

                    if (handle.IsInvalid)
                    {
                        continue;
                    }

                    var buf = new byte[12];
                    if (!DeviceIoControl(handle, IoctlStorageGetDeviceNumber, null, 0,
                            buf, buf.Length, out _, IntPtr.Zero))
                    {
                        continue;
                    }

                    if (BitConverter.ToUInt32(buf, 4) != diskNumber)
                    {
                        continue;
                    }

                    var info = Marshal.PtrToStructure<SpDevInfoData>(infoPtr);
                    var sb = new StringBuilder(512);
                    if (SetupDiGetDeviceInstanceIdW(set, ref info, sb, sb.Capacity, out _))
                    {
                        return sb.ToString();
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(infoPtr);
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return null;
    }

    // ── ④ 兜底：老的 IOCTL 流程 ───────────────────────────────────

    /// <summary>
    /// 兜底路径：直接对**卷句柄**发 IOCTL_STORAGE_EJECT_MEDIA。
    ///
    /// ⚠️ 原版是对 <c>\\.\PhysicalDriveN</c> 发的，而不是卷句柄。那条路在**非管理员**进程里
    /// 直接用 Access Denied 失败（实测错误 5）—— 于是「兜底」在普通用户下根本没跑起来过，
    /// 报出来的却是别的原因。卷句柄我们本来就拿着（还是 RW 打开的），足够发这个 IOCTL。
    /// </summary>
    private static bool TryEjectMedia(SafeFileHandle vol, List<string> trail)
    {
        // EJECT_MEDIA 要求 1 字节输入缓冲；MEDIA_REMOVAL 需要 4 字节结构，这里保持原版行为
        // （只发 0 字节），它本来就基本不会成功。
        byte[] one = new byte[1];
        bool ejected = DeviceIoControl(vol, IoctlStorageEjectMedia, one, 1, null, 0, out _, IntPtr.Zero);
        DeviceIoControl(vol, IoctlStorageMediaRemoval, null, 0, null, 0, out _, IntPtr.Zero);

        if (!ejected)
        {
            trail.Add($"IOCTL_STORAGE_EJECT_MEDIA 失败（{Describe(Marshal.GetLastWin32Error())}，"
                      + "这类读卡器多半不支持该指令）");
        }

        return ejected;
    }

    // ── 路径转换 ──────────────────────────────────────────────────

    private static string ToVolumePath(string spec)
    {
        if (spec.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            spec.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            spec.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase))
        {
            return spec;
        }

        if (spec.Length == 2 && spec[1] == ':')
        {
            return "\\\\.\\" + char.ToUpperInvariant(spec[0]) + ":";
        }

        // 挂载点：转成 \\?\Volume{GUID}\
        var sb = new StringBuilder(1024);
        if (GetVolumeNameForVolumeMountPoint(spec + "\\", sb, sb.Capacity))
        {
            return sb.ToString();
        }

        return spec;
    }

    // ── P/Invoke ─────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
        byte[]? input, int inputSize, byte[]? output, int outputSize, out int returned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mount,
        StringBuilder name, int size);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator,
        IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo,
        ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set,
        ref SpDeviceInterfaceData data, IntPtr detail, int detailSize, out int required, IntPtr devInfo);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set,
        ref SpDevInfoData infoData, StringBuilder instanceId, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Request_Device_EjectW(uint devInst, out int vetoType,
        StringBuilder vetoName, int vetoNameLen, int flags);
}
