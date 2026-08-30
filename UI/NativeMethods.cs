using System;
using System.Runtime.InteropServices;

namespace CodexUsageMonitor.UI
{
    internal static class NativeMethods
    {
        public const int GwlExStyle = -20;
        public const int WsExTransparent = 0x00000020;
        public const int WsExLayered = 0x00080000;
        public const int WmHotkey = 0x0312;
        public const int WmDisplayChange = 0x007E;

        public const uint ModAlt = 0x0001;
        public const uint ModControl = 0x0002;

        public const int HotkeyTogglePassthrough = 1;
        public const int HotkeyOpacityUp = 2;
        public const int HotkeyOpacityDown = 3;
        public const int HotkeyExit = 4;

        public static readonly IntPtr HwndTopMost = new IntPtr(-1);
        public const uint SwpNoSize = 0x0001;
        public const uint SwpNoMove = 0x0002;
        public const uint SwpNoActivate = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}
