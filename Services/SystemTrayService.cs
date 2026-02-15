using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using WinRT.Interop;

namespace GameLauncher.Services
{
    public class SystemTrayService : IDisposable
    {
        private readonly Window _window;
        private AppWindow? _appWindow;
        private IntPtr _hWnd;
        private bool _disposed;
        private bool _isMinimizedToTray;
        private TrayIcon? _trayIcon;

        public event EventHandler? TrayIconClicked;

        public SystemTrayService(Window window)
        {
            _window = window;
            InitializeWindow();
        }

        private void InitializeWindow()
        {
            _hWnd = WindowNative.GetWindowHandle(_window);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
        }

        public void MinimizeToTray()
        {
            if (_isMinimizedToTray)
                return;

            _appWindow?.Hide();
            _isMinimizedToTray = true;

            if (_trayIcon == null)
            {
                _trayIcon = new TrayIcon(_hWnd);
                _trayIcon.DoubleClick += (s, e) =>
                {
                    TrayIconClicked?.Invoke(this, EventArgs.Empty);
                    RestoreFromTray();
                };
            }
            _trayIcon.Show();
        }

        public void RestoreFromTray()
        {
            if (!_isMinimizedToTray)
                return;

            _trayIcon?.Hide();
            _appWindow?.Show();
            _isMinimizedToTray = false;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _trayIcon?.Dispose();
            _disposed = true;
        }

        private class TrayIcon : IDisposable
        {
            private const int WM_TRAYMESSAGE = 0x800;
            private const int NIF_ICON = 0x00000002;
            private const int NIF_MESSAGE = 0x00000001;
            private const int NIF_TIP = 0x00000004;
            private const int NIM_ADD = 0x00000000;
            private const int NIM_DELETE = 0x00000002;
            private const int WM_LBUTTONDBLCLK = 0x0203;

            private IntPtr _messageWindow;
            private IntPtr _parentWindow;
            private uint _uId = 1;
            private bool _disposed;
            private WNDCLASSW _windowClass;

            public event EventHandler? DoubleClick;

            public TrayIcon(IntPtr parentWindow)
            {
                _parentWindow = parentWindow;
                CreateMessageWindow();
            }

            private void CreateMessageWindow()
            {
                _windowClass = new WNDCLASSW();
                _windowClass.lpfnWndProc = WndProc;
                _windowClass.hInstance = Marshal.GetHINSTANCE(typeof(TrayIcon).Module);
                _windowClass.lpszClassName = "GameLauncherTrayWindow_" + Guid.NewGuid().ToString("N");

                RegisterClassW(ref _windowClass);

                _messageWindow = CreateWindowExW(
                    0,
                    _windowClass.lpszClassName,
                    "GameLauncher Tray",
                    0, 0, 0, 0, 0,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    _windowClass.hInstance,
                    IntPtr.Zero);
            }

            public void Show()
            {
                var data = new NOTIFYICONDATAA();
                data.cbSize = (uint)Marshal.SizeOf(data);
                data.hWnd = _messageWindow;
                data.uID = _uId;
                data.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
                data.uCallbackMessage = WM_TRAYMESSAGE;
                data.hIcon = LoadDefaultIcon();
                data.szTip = "GameLauncher";

                Shell_NotifyIconA(NIM_ADD, ref data);
            }

            public void Hide()
            {
                var data = new NOTIFYICONDATAA();
                data.cbSize = (uint)Marshal.SizeOf(data);
                data.hWnd = _messageWindow;
                data.uID = _uId;

                Shell_NotifyIconA(NIM_DELETE, ref data);
            }

            private IntPtr LoadDefaultIcon()
            {
                return LoadIconW(IntPtr.Zero, 32512);
            }

            private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
            {
                if (msg == WM_TRAYMESSAGE)
                {
                    int mouseMessage = (int)((ulong)lParam & 0xFFFF);
                    if (mouseMessage == WM_LBUTTONDBLCLK)
                    {
                        DoubleClick?.Invoke(this, EventArgs.Empty);
                    }
                }

                return DefWindowProcW(hWnd, msg, wParam, lParam);
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                Hide();
                if (_messageWindow != IntPtr.Zero)
                {
                    DestroyWindow(_messageWindow);
                }
                _disposed = true;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
            private struct NOTIFYICONDATAA
            {
                public uint cbSize;
                public IntPtr hWnd;
                public uint uID;
                public uint uFlags;
                public uint uCallbackMessage;
                public IntPtr hIcon;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
                public string szTip;
                public uint dwState;
                public uint dwStateMask;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
                public string szInfo;
                public uint uTimeoutOrVersion;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
                public string szInfoTitle;
                public uint dwInfoFlags;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct WNDCLASSW
            {
                public uint style;
                public WNDPROC lpfnWndProc;
                public int cbClsExtra;
                public int cbWndExtra;
                public IntPtr hInstance;
                public IntPtr hIcon;
                public IntPtr hCursor;
                public IntPtr hbrBackground;
                public string lpszMenuName;
                public string lpszClassName;
            }

            private delegate IntPtr WNDPROC(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

            [DllImport("user32.dll")]
            private static extern bool DestroyWindow(IntPtr hWnd);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

            [DllImport("user32.dll")]
            private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr LoadIconW(IntPtr hInstance, uint lpIconName);

            [DllImport("shell32.dll", CharSet = CharSet.Ansi)]
            private static extern bool Shell_NotifyIconA(uint dwMessage, ref NOTIFYICONDATAA lpData);
        }
    }
}
