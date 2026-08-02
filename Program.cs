using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Threading;

namespace DisplayWakeFix
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
        }
    }

    class TrayApp : Form
    {
        // --- Win32 interop ---
        [DllImport("user32.dll")]
        static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, int Flags);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hWnd); // minimized check

        [DllImport("user32.dll")]
        static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        const int WM_POWERBROADCAST = 0x0218;
        const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const byte VK_CONTROL = 0x11;
        const byte VK_LWIN = 0x5B;
        const byte VK_SHIFT = 0x10;
        const byte VK_B = 0x42;

        // GUID_CONSOLE_DISPLAY_STATE: 0 = off, 1 = on, 2 = dimmed
        static Guid GUID_CONSOLE_DISPLAY_STATE = new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        const int SW_SHOWMINIMIZED = 2;
        const int SW_SHOWNORMAL = 1;
        const int SW_SHOWMAXIMIZED = 3;

        [StructLayout(LayoutKind.Sequential)]
        struct POWERBROADCAST_SETTING
        {
            public Guid PowerSetting;
            public uint DataLength;
            public byte Data;
        }

        Dictionary<IntPtr, WINDOWPLACEMENT> lastGoodLayout = new Dictionary<IntPtr, WINDOWPLACEMENT>();
        NotifyIcon trayIcon;
        bool displayCurrentlyOn = true;

        public TrayApp()
        {
            // hide the form itself, we only want the tray icon
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            this.Opacity = 0;
            this.Load += (s, e) => this.Hide();

            trayIcon = new NotifyIcon();
            try
            {
                trayIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                trayIcon.Icon = SystemIcons.Application;
            }
            trayIcon.Visible = true;
            trayIcon.Text = "Display Wake Fix";
            var menu = new ContextMenuStrip();
            menu.Items.Add("Restore windows now", null, (s, e) => RestoreLayout());
            menu.Items.Add("Move all windows to main screen", null, (s, e) => MoveAllWindowsToPrimary());
            menu.Items.Add("by |¥|@µ®¡", null, (s, e) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://mauweb.net") { UseShellExecute = true }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => { trayIcon.Visible = false; Application.Exit(); });
            trayIcon.ContextMenuStrip = menu;

            RegisterPowerSettingNotification(this.Handle, ref GUID_CONSOLE_DISPLAY_STATE, DEVICE_NOTIFY_WINDOW_HANDLE);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_POWERBROADCAST && m.WParam.ToInt32() == 0x8013 /* PBT_POWERSETTINGCHANGE */)
            {
                var setting = (POWERBROADCAST_SETTING)Marshal.PtrToStructure(m.LParam, typeof(POWERBROADCAST_SETTING));
                if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
                {
                    bool nowOn = setting.Data != 0;
                    if (!nowOn && displayCurrentlyOn)
                    {
                        // displays just went off: snapshot one last time
                        SnapshotLayout();
                    }
                    else if (nowOn && !displayCurrentlyOn)
                    {
                        // displays just came back: fix it
                        OnDisplaysWokeUp();
                    }
                    displayCurrentlyOn = nowOn;
                }
            }
            base.WndProc(ref m);
        }

        void SnapshotLayout()
        {
            var current = new Dictionary<IntPtr, WINDOWPLACEMENT>();
            EnumWindows((hWnd, lParam) =>
            {
                // note: no !IsIconic check here on purpose. rcNormalPosition
                // (which monitor/coords the window restores to) is valid
                // and meaningful even while the window is minimized.
                if (IsWindowVisible(hWnd) && GetWindowTextLength(hWnd) > 0)
                {
                    WINDOWPLACEMENT wp = new WINDOWPLACEMENT();
                    wp.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                    if (GetWindowPlacement(hWnd, ref wp))
                        current[hWnd] = wp;
                }
                return true;
            }, IntPtr.Zero);

            if (current.Count > 0)
                lastGoodLayout = current;
        }

        void OnDisplaysWokeUp()
        {
            // run on a background thread so we don't block the message loop
            var t = new Thread(() =>
            {
                Thread.Sleep(1500);
                ForceDriverReset();
                Thread.Sleep(2500);
                RestoreLayout();
            });
            t.IsBackground = true;
            t.Start();
        }

        void ForceDriverReset()
        {
            // simulates Ctrl+Win+Shift+B
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
            keybd_event(VK_B, 0, 0, UIntPtr.Zero);
            Thread.Sleep(50);
            keybd_event(VK_B, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        void RestoreLayout()
        {
            foreach (var kv in lastGoodLayout)
            {
                IntPtr hWnd = kv.Key;
                WINDOWPLACEMENT saved = kv.Value;
                if (!IsWindowVisible(hWnd)) continue;

                // read the window's current placement so we know its
                // CURRENT show state (minimized/normal/maximized) - we
                // never want to change that, only which monitor/coords
                // it lives at.
                WINDOWPLACEMENT current = new WINDOWPLACEMENT();
                current.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                if (!GetWindowPlacement(hWnd, ref current)) continue;

                WINDOWPLACEMENT target = current;
                target.rcNormalPosition = saved.rcNormalPosition;

                SetWindowPlacement(hWnd, ref target);

                // if the window is currently on-screen (normal or maximized,
                // not minimized), also nudge it directly so it snaps into
                // place immediately rather than waiting for next restore.
                if (current.showCmd != SW_SHOWMINIMIZED)
                {
                    RECT r = saved.rcNormalPosition;
                    SetWindowPos(hWnd, IntPtr.Zero, r.Left, r.Top,
                        r.Right - r.Left, r.Bottom - r.Top,
                        SWP_NOZORDER | SWP_NOACTIVATE);
                }
            }
        }

        void MoveAllWindowsToPrimary()
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;

            int cascadeX = 0;
            int cascadeY = 0;
            const int cascadeStep = 32;

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd) || GetWindowTextLength(hWnd) == 0)
                    return true;

                WINDOWPLACEMENT current = new WINDOWPLACEMENT();
                current.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                if (!GetWindowPlacement(hWnd, ref current))
                    return true;

                // keep each window's existing size (clamped to fit the
                // primary monitor), just relocate it there.
                RECT normal = current.rcNormalPosition;

                // figure out which monitor this window currently lives on.
                // for a minimized window, its on-screen rect is just the
                // taskbar icon area, not meaningful, so use rcNormalPosition
                // (the "restores to" rect) instead in that case.
                Rectangle testRect;
                if (current.showCmd == SW_SHOWMINIMIZED)
                {
                    testRect = new Rectangle(normal.Left, normal.Top,
                        normal.Right - normal.Left, normal.Bottom - normal.Top);
                }
                else
                {
                    RECT liveRect;
                    if (GetWindowRect(hWnd, out liveRect))
                        testRect = new Rectangle(liveRect.Left, liveRect.Top,
                            liveRect.Right - liveRect.Left, liveRect.Bottom - liveRect.Top);
                    else
                        testRect = new Rectangle(normal.Left, normal.Top,
                            normal.Right - normal.Left, normal.Bottom - normal.Top);
                }

                // already on the main screen: leave it exactly where it is.
                if (testRect.Width > 0 && testRect.Height > 0 && Screen.FromRectangle(testRect).Primary)
                    return true;

                int width = Math.Min(normal.Right - normal.Left, work.Width);
                int height = Math.Min(normal.Bottom - normal.Top, work.Height);
                if (width <= 0) width = Math.Min(800, work.Width);
                if (height <= 0) height = Math.Min(600, work.Height);

                if (cascadeX + width > work.Width) cascadeX = 0;
                if (cascadeY + height > work.Height) cascadeY = 0;

                int x = work.X + cascadeX;
                int y = work.Y + cascadeY;

                WINDOWPLACEMENT target = current;
                target.rcNormalPosition = new RECT
                {
                    Left = x,
                    Top = y,
                    Right = x + width,
                    Bottom = y + height
                };

                SetWindowPlacement(hWnd, ref target);

                // if it's currently visible on-screen (not minimized), also
                // move it directly so it snaps over immediately.
                if (current.showCmd != SW_SHOWMINIMIZED)
                {
                    SetWindowPos(hWnd, IntPtr.Zero, x, y, width, height,
                        SWP_NOZORDER | SWP_NOACTIVATE);
                }

                cascadeX += cascadeStep;
                cascadeY += cascadeStep;

                return true;
            }, IntPtr.Zero);
        }
    }
}
