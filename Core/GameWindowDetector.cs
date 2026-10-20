using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace KardsVox.Core;

internal static class GameWindowDetector
{
    private static readonly string[] GameProcessNames = ["kards-Win64-Shipping", "kards"];

    // Capture only the visible client area of the foreground game window.
    public static bool TryGetForegroundClient(out Rectangle screenBounds)
    {
        screenBounds = Rectangle.Empty;
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window)) return false;
        GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (!GameProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) return false;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }

        if (!GetClientRect(window, out var client)) return false;
        var topLeft = new Point(0, 0);
        if (!ClientToScreen(window, ref topLeft)) return false;
        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;
        if (width < 320 || height < 240) return false;
        screenBounds = new Rectangle(topLeft.X, topLeft.Y, width, height);
        return true;
    }

    // Finds the visible game client by PID. During Unreal map transitions the
    // foreground window can briefly change even though the game is still the
    // visible full-screen surface, so the intro detector must not depend on
    // GetForegroundWindow for that short interval.
    public static bool TryGetClientForProcess(int processId, out Rectangle screenBounds)
    {
        Rectangle largest = Rectangle.Empty;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window)) return true;
            GetWindowThreadProcessId(window, out uint candidatePid);
            if (candidatePid != (uint)processId) return true;
            if (!TryGetClientBounds(window, out Rectangle bounds)) return true;
            if ((long)bounds.Width * bounds.Height > (long)largest.Width * largest.Height) largest = bounds;
            return true;
        }, IntPtr.Zero);
        screenBounds = largest;
        return !largest.IsEmpty;
    }

    private static bool TryGetClientBounds(IntPtr window, out Rectangle screenBounds)
    {
        screenBounds = Rectangle.Empty;
        if (!GetClientRect(window, out var client)) return false;
        var topLeft = new Point(0, 0);
        if (!ClientToScreen(window, ref topLeft)) return false;
        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;
        if (width < 320 || height < 240) return false;
        screenBounds = new Rectangle(topLeft.X, topLeft.Y, width, height);
        return true;
    }

    public static bool Exists()
    {
        var gameProcessIds = new HashSet<uint>();

        foreach (var name in GameProcessNames)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        gameProcessIds.Add((uint)process.Id);
                    }
                }
            }
            catch (Win32Exception)
            {
                // The game may have exited while processes were being listed.
            }
        }

        if (gameProcessIds.Count == 0) return false;

        if (HasVisibleWindow(gameProcessIds, IntPtr.Zero)) return true;

        // Console runners may be attached to a desktop other than the interactive one.
        var inputDesktop = OpenInputDesktop(0, false, 0x41); // READOBJECTS | ENUMERATE
        if (inputDesktop == IntPtr.Zero) return false;

        try
        {
            return HasVisibleWindow(gameProcessIds, inputDesktop);
        }
        finally
        {
            CloseDesktop(inputDesktop);
        }
    }

    private static bool HasVisibleWindow(HashSet<uint> gameProcessIds, IntPtr desktop)
    {
        bool foundVisibleWindow = false;
        EnumWindowsCallback callback = (window, _) =>
        {
            if (!IsWindowVisible(window)) return true;

            GetWindowThreadProcessId(window, out var processId);
            if (!gameProcessIds.Contains(processId)) return true;

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

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDesktopWindows(IntPtr desktop, EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
