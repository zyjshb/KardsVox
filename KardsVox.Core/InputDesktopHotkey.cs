using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace KardsVox.Core;

internal sealed class InputDesktopHotkey : IDisposable
{
	private struct WindowMessage
	{
		public nint window;

		public uint message;

		public nint wParam;

		public nint lParam;

		public uint time;

		public WindowPoint point;

		public uint privateData;
	}

	private struct WindowPoint
	{
		public int x;

		public int y;
	}

	private const uint NoRepeat = 16384u;

	private const uint HotkeyMessage = 786u;

	private const uint QuitMessage = 18u;

	private const uint DesktopAccess = 67u;

	private readonly Action<VoxHotkey> onPressed;

	private readonly ManualResetEventSlim ready = new ManualResetEventSlim(initialState: false);

	private Thread? thread;

	private Exception? startError;

	private uint threadId;

	public InputDesktopHotkey(Action<VoxHotkey> onPressed)
	{
		this.onPressed = onPressed;
	}

	public void Start()
	{
		thread = new Thread(Run)
		{
			IsBackground = true,
			Name = "KardsVox hotkeys"
		};
		thread.Start();
		ready.Wait();
		if (startError != null)
		{
			thread.Join();
			throw startError;
		}
	}

	public void WaitForExit()
	{
		thread?.Join();
	}

	public void Stop()
	{
		if (threadId != 0)
		{
			PostThreadMessage(threadId, 18u, IntPtr.Zero, IntPtr.Zero);
		}
	}

	public void Dispose()
	{
		Stop();
		thread?.Join();
		ready.Dispose();
	}

	private void Run()
	{
		nint desktop = IntPtr.Zero;
		List<int> registered = new List<int>();
		try
		{
			desktop = OpenInputDesktop(0u, inherit: false, 67u);
			if (desktop == IntPtr.Zero)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Windows input desktop");
			}
			if (!SetThreadDesktop(desktop))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach hotkeys to the input desktop");
			}
			using (DpiAwareness.UsePhysicalPixels())
			{
				threadId = GetCurrentThreadId();
				VoxHotkey[] values = Enum.GetValues<VoxHotkey>();
				for (int i = 0; i < values.Length; i++)
				{
					int id = (int)values[i];
					uint virtualKey = (uint)(116 + id);
					if (!RegisterHotKey(IntPtr.Zero, id, 16384u, virtualKey))
					{
						throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not register F{5 + id}; another app may use it");
					}
					registered.Add(id);
				}
				ready.Set();
				while (true)
				{
					WindowMessage message;
					switch (GetMessage(out message, IntPtr.Zero, 0u, 0u))
					{
					case -1:
						throw new Win32Exception(Marshal.GetLastWin32Error(), "Hotkey message loop failed");
					case 0:
						return;
					}
					if (message.message == 786 && Enum.IsDefined(typeof(VoxHotkey), (int)message.wParam))
					{
						try
						{
							onPressed((VoxHotkey)message.wParam);
						}
						catch (Exception ex)
						{
							Console.Error.WriteLine("Hotkey failed: " + ex.Message);
						}
					}
				}
			}
		}
		catch (Exception ex2)
		{
			if (!ready.IsSet)
			{
				startError = ex2;
				ready.Set();
			}
			else
			{
				Console.Error.WriteLine("Hotkey listener stopped: " + ex2.Message);
			}
		}
		finally
		{
			foreach (int id2 in registered)
			{
				UnregisterHotKey(IntPtr.Zero, id2);
			}
			if (desktop != IntPtr.Zero)
			{
				CloseDesktop(desktop);
			}
			ready.Set();
		}
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetThreadDesktop(nint desktop);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseDesktop(nint desktop);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnregisterHotKey(nint window, int id);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern int GetMessage(out WindowMessage message, nint window, uint min, uint max);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();
}
