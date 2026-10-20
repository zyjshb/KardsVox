using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KardsVox.Core;

// Win32 window coordinates and CopyFromScreen must use the same physical pixel space.
// The monitor and hotkey threads are set explicitly because dotnet.exe can otherwise
// receive virtualized coordinates on a display with 150% scaling.
internal readonly struct DpiAwareness : IDisposable
{
    private static readonly IntPtr PerMonitorV2 = new(-4);
    private readonly IntPtr previous;

    private DpiAwareness(IntPtr previous) => this.previous = previous;

    public static DpiAwareness UsePhysicalPixels()
    {
        IntPtr previous = SetThreadDpiAwarenessContext(PerMonitorV2);
        if (previous == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enable physical-pixel screen capture");
        return new DpiAwareness(previous);
    }

    public void Dispose()
    {
        if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
}
