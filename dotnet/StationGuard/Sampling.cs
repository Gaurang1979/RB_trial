using System.Diagnostics;

namespace StationGuard;

/// <summary>One reading of the station process and its PC.</summary>
public sealed record ResourceSample(
    DateTimeOffset At,
    long PrivateBytes,     // memory the process holds that no other process shares
    long WorkingSetBytes,  // memory in RAM now
    long ManagedHeapBytes, // .NET heap after the last collection
    long SystemLoadBytes,  // memory in use on the whole PC
    long SystemTotalBytes, // memory the PC has
    double CpuPct,         // this process, all cores together = 100
    int Handles,
    int Threads,
    long DiskFreeBytes,    // on the drive of the data folder
    long DiskTotalBytes,
    double? SystemCpuPct = null); // the whole PC, all cores together = 100; null when the system cannot tell yet

/// <summary>Where samples come from; a fake in tests.</summary>
public interface IResourceProbe
{
    ResourceSample Read();
}

/// <summary>Reads the current process and PC. Cheap (well under a millisecond).</summary>
public sealed class ProcessProbe(string dataFolder, TimeProvider? time = null) : IResourceProbe
{
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly SystemCpu _systemCpu = new();
    TimeSpan _lastCpu;
    long _lastTicks;

    public ResourceSample Read()
    {
        using var p = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        var now = _time.GetTimestamp();
        var cpu = p.TotalProcessorTime;
        double cpuPct = 0;
        if (_lastTicks != 0)
        {
            var wall = _time.GetElapsedTime(_lastTicks, now).TotalMilliseconds;
            if (wall > 0) cpuPct = Math.Clamp((cpu - _lastCpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100, 0, 100);
        }
        _lastCpu = cpu;
        _lastTicks = now;
        long free = 0, total = 0;
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dataFolder))!);
            free = d.AvailableFreeSpace;
            total = d.TotalSize;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
        // The PC's memory now: the GC's figure is only updated at a collection (and 0 before the
        // first), and with reused frame buffers collections are rare.
        var (load, sysTotal) = SystemMemory.Read() ?? (gc.MemoryLoadBytes, gc.TotalAvailableMemoryBytes);
        // macOS (development) gives no private bytes: the working set stands in for them
        var priv = p.PrivateMemorySize64 > 0 ? p.PrivateMemorySize64 : p.WorkingSet64;
        return new ResourceSample(_time.GetUtcNow(), priv, p.WorkingSet64, gc.HeapSizeBytes,
            load, sysTotal, cpuPct, p.HandleCount, p.Threads.Count, free, total, _systemCpu.Read());
    }
}

/// <summary>macOS system calls, for the development Mac.</summary>
static class Mac
{
    [System.Runtime.InteropServices.DllImport("/usr/lib/libSystem.dylib")]
    internal static extern uint mach_host_self();

    [System.Runtime.InteropServices.DllImport("/usr/lib/libSystem.dylib")]
    internal static extern int host_statistics(uint host, int flavor, uint[] info, ref uint count);

    [System.Runtime.InteropServices.DllImport("/usr/lib/libSystem.dylib")]
    internal static extern int host_statistics64(uint host, int flavor, uint[] info, ref uint count);

    [System.Runtime.InteropServices.DllImport("/usr/lib/libSystem.dylib")]
    internal static extern int sysctlbyname(string name, out long value, ref nint size, IntPtr newp, nint newlen);

    static uint _host;
    // one send right for the life of the program (each call would take another)
    internal static uint Host => _host != 0 ? _host : _host = mach_host_self();
}

/// <summary>
/// The whole PC's CPU use between two reads, from the system's own busy and idle times
/// (Windows GetSystemTimes; macOS host CPU ticks, for development; Linux /proc/stat).
/// </summary>
public sealed class SystemCpu
{
    readonly Func<(ulong Busy, ulong Total)?> _times;
    (ulong Busy, ulong Total)? _last;

    public SystemCpu() : this(Times) { }

    /// <summary>With the system's (busy, total) time counters given; for tests.</summary>
    public SystemCpu(Func<(ulong Busy, ulong Total)?> times) => _times = times;

    /// <summary>Percent busy since the last read; null on the first read or when the system cannot tell.</summary>
    public double? Read()
    {
        var now = _times();
        var last = _last;
        _last = now;
        if (now is not { } n || last is not { } l || n.Total <= l.Total || n.Busy < l.Busy) return null;
        return Math.Clamp(100.0 * (n.Busy - l.Busy) / (n.Total - l.Total), 0, 100);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    /// <summary>(busy, total) time counters of the whole PC since it started, or null.</summary>
    public static (ulong Busy, ulong Total)? Times()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // kernel time includes idle time
                if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
                var total = kernel + user;
                return total >= idle ? (total - idle, total) : null;
            }
            if (OperatingSystem.IsMacOS())
            {
                // HOST_CPU_LOAD_INFO: ticks in user, system, idle, nice
                var t = new uint[4];
                uint count = 4;
                if (Mac.host_statistics(Mac.Host, 3, t, ref count) != 0) return null;
                ulong busy = (ulong)t[0] + t[1] + t[3];
                return (busy, busy + t[2]);
            }
            if (File.Exists("/proc/stat"))
            {
                // cpu  user nice system idle iowait irq softirq steal …
                var first = File.ReadLines("/proc/stat").FirstOrDefault();
                if (first is null || !first.StartsWith("cpu ")) return null;
                var v = first.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
                    .Select(x => ulong.TryParse(x, out var n) ? n : 0).ToArray();
                if (v.Length < 4) return null;
                ulong total = 0;
                foreach (var x in v) total += x;
                var idle = v[3] + (v.Length > 4 ? v[4] : 0);
                return (total - idle, total);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }
}

/// <summary>The whole PC's memory in use and in total, read live from the system.</summary>
public static class SystemMemory
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);

    /// <summary>(in use, total) in bytes, or null when the system cannot tell.</summary>
    public static (long Load, long Total)? Read()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var m = new MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref m) ? ((long)(m.TotalPhys - m.AvailPhys), (long)m.TotalPhys) : null;
            }
            if (File.Exists("/proc/meminfo"))
            {
                long total = 0, avail = -1;
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2 || !long.TryParse(parts[1], out var kb)) continue;
                    if (parts[0] == "MemTotal:") total = kb * 1024;
                    else if (parts[0] == "MemAvailable:") avail = kb * 1024;
                }
                return total > 0 && avail >= 0 ? (total - avail, total) : null;
            }
            if (OperatingSystem.IsMacOS()) return MacOs();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    // macOS, for development: in use = all pages but the free, inactive and speculative ones
    // (vm_statistics64, HOST_VM_INFO64); total from hw.memsize.
    static (long, long)? MacOs()
    {
        nint size = sizeof(long);
        if (Mac.sysctlbyname("hw.memsize", out var total, ref size, IntPtr.Zero, 0) != 0 || total <= 0) return null;
        var v = new uint[38];
        uint count = (uint)v.Length;
        if (Mac.host_statistics64(Mac.Host, 4, v, ref count) != 0) return null;
        long page = Environment.SystemPageSize;
        var available = ((long)v[0] + v[2] + v[23]) * page; // free, inactive, speculative
        return (Math.Clamp(total - available, 0, total), total);
    }
}
