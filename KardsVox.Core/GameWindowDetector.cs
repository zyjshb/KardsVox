using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace KardsVox.Core;

internal static class GameWindowDetector
{
	private delegate bool EnumWindowsCallback(nint window, nint parameter);

	private struct NativeRect
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private static readonly string[] GameProcessNames = new string[2] { "kards-Win64-Shipping", "kards" };

	public static bool TryGetForegroundClient(out Rectangle screenBounds)
	{
		screenBounds = Rectangle.Empty;
		nint window = GetForegroundWindow();
		if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window))
		{
			return false;
		}
		GetWindowThreadProcessId(window, out var pid);
		try
		{
			using Process process = Process.GetProcessById((int)pid);
			if (!GameProcessNames.Contains<string>(process.ProcessName, StringComparer.OrdinalIgnoreCase))
			{
				return false;
			}
		}
		catch (ArgumentException)
		{
			return false;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
		catch (Win32Exception)
		{
			return false;
		}
		if (!GetClientRect(window, out var client))
		{
			return false;
		}
		Point topLeft = new Point(0, 0);
		if (!ClientToScreen(window, ref topLeft))
		{
			return false;
		}
		int width = client.Right - client.Left;
		int height = client.Bottom - client.Top;
		if (width < 320 || height < 240)
		{
			return false;
		}
		screenBounds = new Rectangle(topLeft.X, topLeft.Y, width, height);
		return true;
	}

	public static bool TryGetClientForProcess(int processId, out Rectangle screenBounds)
	{
		Rectangle largest = Rectangle.Empty;
		EnumWindows(delegate(nint window, nint _)
		{
			if (!IsWindowVisible(window) || IsIconic(window))
			{
				return true;
			}
			GetWindowThreadProcessId(window, out var processId2);
			if (processId2 != (uint)processId)
			{
				return true;
			}
			if (!TryGetClientBounds(window, out var screenBounds2))
			{
				return true;
			}
			if ((long)screenBounds2.Width * (long)screenBounds2.Height > (long)largest.Width * (long)largest.Height)
			{
				largest = screenBounds2;
			}
			return true;
		}, IntPtr.Zero);
		screenBounds = largest;
		return !largest.IsEmpty;
	}

	public static bool TryGetAnyGameClient(out Rectangle screenBounds)
	{
		Rectangle largest = Rectangle.Empty;
		foreach (string name in GameProcessNames)
		{
			try
			{
				foreach (Process process in Process.GetProcessesByName(name))
				{
					using (process)
					{
						if (TryGetClientForProcess(process.Id, out Rectangle candidate)
							&& (long)candidate.Width * candidate.Height > (long)largest.Width * largest.Height)
						{
							largest = candidate;
						}
					}
				}
			}
			catch (Win32Exception)
			{
			}
		}

		screenBounds = largest;
		return !largest.IsEmpty;
	}

	private static bool TryGetClientBounds(nint window, out Rectangle screenBounds)
	{
		screenBounds = Rectangle.Empty;
		if (!GetClientRect(window, out var client))
		{
			return false;
		}
		Point topLeft = new Point(0, 0);
		if (!ClientToScreen(window, ref topLeft))
		{
			return false;
		}
		int width = client.Right - client.Left;
		int height = client.Bottom - client.Top;
		if (width < 320 || height < 240)
		{
			return false;
		}
		screenBounds = new Rectangle(topLeft.X, topLeft.Y, width, height);
		return true;
	}

	public static bool Exists()
	{
		HashSet<uint> gameProcessIds = new HashSet<uint>();
		string[] gameProcessNames = GameProcessNames;
		foreach (string name in gameProcessNames)
		{
			try
			{
				Process[] processesByName = Process.GetProcessesByName(name);
				foreach (Process process in processesByName)
				{
					using (process)
					{
						gameProcessIds.Add((uint)process.Id);
					}
				}
			}
			catch (Win32Exception)
			{
			}
		}
		if (gameProcessIds.Count == 0)
		{
			return false;
		}
		if (HasVisibleWindow(gameProcessIds, IntPtr.Zero))
		{
			return true;
		}
		nint inputDesktop = OpenInputDesktop(0u, inherit: false, 65u);
		if (inputDesktop == IntPtr.Zero)
		{
			return false;
		}
		try
		{
			return HasVisibleWindow(gameProcessIds, inputDesktop);
		}
		finally
		{
			CloseDesktop(inputDesktop);
		}
	}

	private static bool HasVisibleWindow(HashSet<uint> gameProcessIds, nint desktop)
	{
		bool foundVisibleWindow = false;
		EnumWindowsCallback callback = delegate(nint window, nint _)
		{
			if (!IsWindowVisible(window))
			{
				return true;
			}
			GetWindowThreadProcessId(window, out var processId);
			if (!gameProcessIds.Contains(processId))
			{
				return true;
			}
			foundVisibleWindow = true;
			return false;
		};
		if (desktop == IntPtr.Zero)
		{
			EnumWindows(callback, IntPtr.Zero);
		}
		else
		{
			EnumDesktopWindows(desktop, callback, IntPtr.Zero);
		}
		return foundVisibleWindow;
	}

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsIconic(nint window);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetClientRect(nint window, out NativeRect rect);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ClientToScreen(nint window, ref Point point);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumDesktopWindows(nint desktop, EnumWindowsCallback callback, nint parameter);

	[DllImport("user32.dll")]
	private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseDesktop(nint desktop);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWindowVisible(nint window);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
