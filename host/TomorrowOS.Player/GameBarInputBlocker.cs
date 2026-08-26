using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TomorrowOS.Player;

/// <summary>
/// Best-effort Win+G block via WH_KEYBOARD_LL. Win+G is owned by the GamingOverlay
/// package; registry background disable in <see cref="GameOverlayPolicy"/> is primary.
/// </summary>
internal sealed class GameBarInputBlocker : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;
    private const int WmQuit = 0x0012;

    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;
    private const int VkG = 0x47;
    private const int VkMenu = 0x12;

    private static GameBarInputBlocker? _activeInstance;
    private static IntPtr _hookId = IntPtr.Zero;
    private static readonly LowLevelKeyboardProc StaticHookProc = StaticHookCallback;
    private static GCHandle _hookProcHandle;

    private Thread? _hookThread;
    private uint _hookThreadId;
    private volatile bool _running;
    private volatile bool _winHeld;
    private bool _disposed;

    private readonly ManualResetEventSlim _hookReady = new(false);

    public event Action? ShortcutBlocked;

    public bool IsHookActive => _hookId != IntPtr.Zero;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _activeInstance = this;
        _running = true;
        _hookReady.Reset();
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "TomorrowOS.GameBarInputBlocker"
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();

        if (!_hookReady.Wait(TimeSpan.FromSeconds(3)))
        {
            TryLog("GameBar input blocker: hook thread did not become ready.");
        }
        else if (!IsHookActive)
        {
            var err = Marshal.GetLastWin32Error();
            TryLog($"GameBar input blocker: SetWindowsHookEx failed (error {err}). Using registry block only.");
        }
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _winHeld = false;

        if (_hookThreadId != 0)
        {
            PostThreadMessage(_hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        _hookThread?.Join(TimeSpan.FromSeconds(2));
        _hookThread = null;
        _hookThreadId = 0;

        if (_activeInstance == this)
        {
            _activeInstance = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _hookReady.Dispose();
        _disposed = true;
    }

    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();

        if (!_hookProcHandle.IsAllocated)
        {
            _hookProcHandle = GCHandle.Alloc(StaticHookProc);
        }

        try
        {
            var module = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName ?? "TomorrowOS.Player.exe");
            if (module == IntPtr.Zero)
            {
                module = GetModuleHandle(null!);
            }

            _hookId = SetWindowsHookEx(WhKeyboardLl, StaticHookProc, module, 0);
            if (_hookId == IntPtr.Zero)
            {
                Marshal.GetLastWin32Error();
            }
        }
        catch (Exception ex)
        {
            TryLog("GameBar input blocker: hook install exception: " + ex.Message);
            _hookId = IntPtr.Zero;
        }

        _hookReady.Set();

        while (_running && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private static IntPtr StaticHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (_activeInstance == null || nCode < 0)
        {
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        try
        {
            if (_activeInstance.TryConsumeBlockedKey(wParam, lParam))
            {
                return (IntPtr)1;
            }
        }
        catch
        {
            // ignore
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool TryConsumeBlockedKey(IntPtr wParam, IntPtr lParam)
    {
        var vk = ReadVkCode(lParam);
        var keyDown = IsKeyDown(wParam);
        var keyUp = IsKeyUp(wParam);

        if (vk is VkLwin or VkRwin)
        {
            if (keyDown)
            {
                _winHeld = true;
                NotifyBlocked();
                return true;
            }

            if (keyUp)
            {
                _winHeld = false;
            }

            return false;
        }

        if (!keyDown)
        {
            return false;
        }

        if (_winHeld || IsWinPhysicallyDown())
        {
            if (vk == VkG)
            {
                NotifyBlocked();
                return true;
            }

            if (IsAltPhysicallyDown() && vk is 0x52 or 0x42 or 0x54)
            {
                NotifyBlocked();
                return true;
            }
        }

        return false;
    }

    private void NotifyBlocked()
    {
        try
        {
            ShortcutBlocked?.Invoke();
        }
        catch
        {
            // ignore
        }
    }

    private static int ReadVkCode(IntPtr lParam) => Marshal.ReadInt32(lParam);

    private static bool IsKeyDown(IntPtr wParam) =>
        wParam == (IntPtr)WmKeydown || wParam == (IntPtr)WmSyskeydown;

    private static bool IsKeyUp(IntPtr wParam) =>
        wParam == (IntPtr)WmKeyup || wParam == (IntPtr)WmSyskeyup;

    private static bool IsWinPhysicallyDown() =>
        (GetAsyncKeyState(VkLwin) & 0x8000) != 0 ||
        (GetAsyncKeyState(VkRwin) & 0x8000) != 0;

    private static bool IsAltPhysicallyDown() => (GetAsyncKeyState(VkMenu) & 0x8000) != 0;

    private static void TryLog(string message)
    {
        try
        {
            AppPaths.EnsureDirectories();
            File.AppendAllText(
                Path.Combine(AppPaths.LogDirectory, "player.log"),
                $"[{DateTime.Now:O}] {message}\n");
        }
        catch
        {
            // ignore
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hHook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hHook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
