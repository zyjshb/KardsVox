using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KardsVox.Core;

internal enum VoxHotkey { Play = 1, CaptureBoard, CaptureLobby, ToggleAuto, Debug }

internal sealed class InputDesktopHotkey : IDisposable
{
    private const uint NoRepeat = 0x4000;
    private const uint HotkeyMessage = 0x0312;
    private const uint QuitMessage = 0x0012;
    private const uint DesktopAccess = 0x43; // READOBJECTS | CREATEWINDOW | ENUMERATE

    private readonly Action<VoxHotkey> onPressed;
    private readonly ManualResetEventSlim ready = new(false);
    private Thread? thread;
    private Exception? startError;
    private uint threadId;

    public InputDesktopHotkey(Action<VoxHotkey> onPressed) => this.onPressed = onPressed;

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "KardsVox hotkeys" };
        thread.Start();
        ready.Wait();
        if (startError is not null) { thread.Join(); throw startError; }
    }

    public void WaitForExit() => thread?.Join();

    public void Stop()
    {
        if (threadId != 0) PostThreadMessage(threadId, QuitMessage, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        Stop();
        thread?.Join();
        ready.Dispose();
    }

    private void Run()
    {
        IntPtr desktop = IntPtr.Zero;
        var registered = new List<int>();
        try
        {
            desktop = OpenInputDesktop(0, false, DesktopAccess);
            if (desktop == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Windows input desktop");
            if (!SetThreadDesktop(desktop))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach hotkeys to the input desktop");
            using var dpi = DpiAwareness.UsePhysicalPixels();

            threadId = GetCurrentThreadId();
            foreach (VoxHotkey key in Enum.GetValues<VoxHotkey>())
            {
                int id = (int)key;
                uint virtualKey = (uint)(0x74 + id); // F6 through F10
                if (!RegisterHotKey(IntPtr.Zero, id, NoRepeat, virtualKey))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not register F{5 + id}; another app may use it");
                registered.Add(id);
            }
            ready.Set();

            while (true)
            {
                int result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result == 0) break;
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "Hotkey message loop failed");
                if (message.message == HotkeyMessage && Enum.IsDefined(typeof(VoxHotkey), (int)message.wParam))
                {
                    try { onPressed((VoxHotkey)(int)message.wParam); }
                    catch (Exception ex) { Console.Error.WriteLine($"Hotkey failed: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex)
        {
            if (!ready.IsSet) { startError = ex; ready.Set(); }
            else Console.Error.WriteLine($"Hotkey listener stopped: {ex.Message}");
        }
        finally
        {
            foreach (int id in registered) UnregisterHotKey(IntPtr.Zero, id);
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
            ready.Set();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMessage
    {
        public IntPtr window;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public WindowPoint point;
        public uint privateData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPoint { public int x; public int y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out WindowMessage message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
