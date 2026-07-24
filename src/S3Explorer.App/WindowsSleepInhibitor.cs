using System.ComponentModel;
using System.Runtime.InteropServices;

namespace S3Explorer.App;

internal static class WindowsSleepInhibitor
{
    private const uint ContextVersion = 0;
    private const uint SimpleString = 1;
    private static readonly object Sync = new();
    private static IntPtr _requestHandle;
    private static int _leaseCount;

    public static IDisposable Acquire()
    {
        if (!OperatingSystem.IsWindows())
            return NoopLease.Instance;

        lock (Sync)
        {
            if (_leaseCount == 0)
                CreateRequest();
            _leaseCount++;
        }
        return new Lease();
    }

    private static void CreateRequest()
    {
        var reason = Marshal.StringToHGlobalUni("S3 Explorer 正在传输文件");
        try
        {
            var context = new ReasonContext
            {
                Version = ContextVersion,
                Flags = SimpleString,
                SimpleReasonString = reason
            };
            _requestHandle = PowerCreateRequest(ref context);
            if (_requestHandle == IntPtr.Zero || _requestHandle == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建系统电源请求。");
            if (!PowerSetRequest(_requestHandle, PowerRequestType.SystemRequired))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(_requestHandle);
                _requestHandle = IntPtr.Zero;
                throw new Win32Exception(error, "无法阻止系统在传输期间进入睡眠。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(reason);
        }
    }

    private static void Release()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Sync)
        {
            if (_leaseCount == 0) return;
            _leaseCount--;
            if (_leaseCount != 0 || _requestHandle == IntPtr.Zero) return;
            PowerClearRequest(_requestHandle, PowerRequestType.SystemRequired);
            CloseHandle(_requestHandle);
            _requestHandle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
    }

    private enum PowerRequestType
    {
        DisplayRequired = 0,
        SystemRequired = 1,
        AwayModeRequired = 2,
        ExecutionRequired = 3
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Release();
        }
    }

    private sealed class NoopLease : IDisposable
    {
        public static NoopLease Instance { get; } = new();
        public void Dispose() { }
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr powerRequest, PowerRequestType requestType);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
