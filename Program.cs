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
        System.Windows.Forms.Timer snapshotTimer;
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
            menu.Items.Add("Exit", null, (s, e) => { trayIcon.Visible = false; Application.Exit(); });
            trayIcon.ContextMenuStrip = menu;

            // snapshot window positions every 5 minutes while displays are on
            snapshotTimer = new System.Windows.Forms.Timer();
            snapshotTimer.Interval = 300000;
            snapshotTimer.Tick += (s, e) => SnapshotLayout();
            snapshotTimer.Start();

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
    }
}
