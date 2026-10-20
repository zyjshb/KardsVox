using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KardsVox.Core;

internal readonly struct DpiAwareness : IDisposable
{
	private static readonly nint PerMonitorV2 = new IntPtr(-4);

	private readonly nint previous;

	private DpiAwareness(nint previous)
	{
		this.previous = previous;
	}

	public static DpiAwareness UsePhysicalPixels()
	{
		nint num = SetThreadDpiAwarenessContext(PerMonitorV2);
		if (num == IntPtr.Zero)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enable physical-pixel screen capture");
		}
		return new DpiAwareness(num);
	}

	public void Dispose()
	{
		if (previous != IntPtr.Zero)
		{
			SetThreadDpiAwarenessContext(previous);
		}
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);
}
