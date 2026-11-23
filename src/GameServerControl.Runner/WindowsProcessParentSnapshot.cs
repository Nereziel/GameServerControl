using System.ComponentModel;
using System.Runtime.InteropServices;
using GameServerControl.Shared;

namespace GameServerControl.Runner;

/// <summary>
/// Reads the Windows process parent graph before a managed game process stops.
/// The snapshot grants no kill rights by itself: callers must additionally
/// verify executable location, process creation time and PID at cleanup time.
/// </summary>
internal static class WindowsProcessParentSnapshot
{
    private const uint Th32csSnapprocess = 0x00000002;

    public static IReadOnlySet<int> DescendantsOf(int rootPid)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
        if (snapshot == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify process ancestry.");

        try
        {
            var process = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32FirstW(snapshot, ref process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate process ancestry.");

            var entries = new List<ProcessCleanupScope.ParentLink>();
            do
            {
                entries.Add(new ProcessCleanupScope.ParentLink(
                    (int)process.ProcessId, (int)process.ParentProcessId));
                process.Size = (uint)Marshal.SizeOf<ProcessEntry>();
            }
            while (Process32NextW(snapshot, ref process));

            return ProcessCleanupScope.DescendantIds(rootPid, entries);
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
