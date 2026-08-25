using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EpsonPadReset;

/// <summary>
/// Bidirectional USBPRINT I/O matching PadZero usb_direct.py:
/// CreateFile without OVERLAPPED; reads use a worker thread + CancelIoEx timeout.
/// Overlapped I/O on usbprint.sys often returns no data on this stack.
/// </summary>
internal static class UsbPrint
{
    private const int DigcfPresent = 0x02;
    private const int DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x01;
    private const uint FileShareWrite = 0x02;
    private const uint OpenExisting = 3;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    // IOCTL_USBPRINT_GET_1284_ID / SOFT_RESET
    private const uint IoctlGet1284Id = 0x00220034;
    private const uint IoctlSoftReset = 0x00220040;

    private static Guid CreateUsbPrintGuid() =>
        new(0x28D78FAD, 0x5A12, 0x11D1, 0xAE, 0x5B, 0x00, 0x00, 0xF8, 0x03, 0xA8, 0xC2);

    public static IReadOnlyList<string> ListDevicePaths()
    {
        var paths = new List<string>();
        var guid = CreateUsbPrintGuid();
        var hdev = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (hdev == InvalidHandleValue)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevsW failed");

        try
        {
            for (uint idx = 0; ; idx++)
            {
                var iface = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(hdev, IntPtr.Zero, ref guid, idx, ref iface))
                    break;

                uint need = 0;
                SetupDiGetDeviceInterfaceDetailW(hdev, ref iface, IntPtr.Zero, 0, ref need, IntPtr.Zero);
                var buf = Marshal.AllocHGlobal((int)need);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(hdev, ref iface, buf, need, ref need, IntPtr.Zero))
                        continue;
                    var p = Marshal.PtrToStringUni(IntPtr.Add(buf, 4));
                    if (!string.IsNullOrWhiteSpace(p)) paths.Add(p);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(hdev); }

        return paths;
    }

    public sealed class Device : IDisposable
    {
        private IntPtr _handle;
        private readonly object _ioLock = new();

        public string Path { get; }
        public int ReadTimeoutMs { get; set; } = 1500;

        public Device(string path)
        {
            Path = path;
            // IMPORTANT: flags=0 like PadZero — FILE_FLAG_OVERLAPPED breaks reads on usbprint.sys here
            _handle = CreateFileW(path, GenericRead | GenericWrite,
                FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (_handle == InvalidHandleValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "CreateFileW failed (Epson-Treiber? USB? Spooler?)");
        }

        public string? TryGet1284Id()
        {
            var buf = new byte[2048];
            if (!DeviceIoControl(_handle, IoctlGet1284Id, IntPtr.Zero, 0, buf, (uint)buf.Length,
                    out var got, IntPtr.Zero) || got == 0)
                return null;
            // First 2 bytes are often length prefix (0xCB 0xCB or LE length)
            var start = 0;
            if (got >= 2 && buf[0] == 0xCB && buf[1] == 0xCB) start = 2;
            return System.Text.Encoding.ASCII.GetString(buf, start, (int)got - start).TrimEnd('\0');
        }

        public int Write(byte[] data)
        {
            lock (_ioLock)
            {
                if (!WriteFile(_handle, data, (uint)data.Length, out var wrote, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteFile failed");
                return (int)wrote;
            }
        }

        public byte[] Read(int size = 4096)
        {
            // Sync ReadFile can block forever — run on worker with CancelIoEx timeout
            byte[]? result = null;
            Exception? error = null;
            var done = new ManualResetEventSlim(false);

            var thread = new Thread(() =>
            {
                try
                {
                    var buf = new byte[size];
                    lock (_ioLock)
                    {
                        if (!ReadFile(_handle, buf, (uint)size, out var got, IntPtr.Zero))
                        {
                            var err = Marshal.GetLastWin32Error();
                            if (err != 995 && err != 0) // cancelled
                                error = new Win32Exception(err, "ReadFile failed");
                            return;
                        }
                        result = got == 0 ? Array.Empty<byte>() : buf.AsSpan(0, (int)got).ToArray();
                    }
                }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            })
            { IsBackground = true };

            thread.Start();
            if (!done.Wait(ReadTimeoutMs))
            {
                CancelIoEx(_handle, IntPtr.Zero);
                done.Wait(500);
                return Array.Empty<byte>();
            }

            if (error is not null) throw error;
            return result ?? Array.Empty<byte>();
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero && _handle != InvalidHandleValue)
            {
                CancelIoEx(_handle, IntPtr.Zero);
                CloseHandle(_handle);
                _handle = InvalidHandleValue;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr hDevInfo, IntPtr devInfo,
        ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr hDevInfo,
        ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, ref uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr hDevInfo);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
