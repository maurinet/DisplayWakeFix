# DisplayWakeFix

A small Windows 11 tray utility that fixes a common multi-monitor bug: after
the screensaver turns your displays off, they sometimes never wake back up
when you move the mouse or press a key. The only way to bring them back is
forcing a GPU driver reset with `Ctrl+Win+Shift+B`, and once that happens,
every open window has usually been scattered across the wrong monitors.

DisplayWakeFix automates both parts of that annoyance.
Fix windows restoring to the wrong screen after system wake up.

## The problem

On some Windows 11 systems, the GPU driver hangs or crashes when the display
power state changes (monitors turning off after idle, or via the screensaver
setting "turn off display"). The driver doesn't recover on its own. The
only fix is the undocumented `Ctrl+Win+Shift+B` shortcut, which forces
Windows to restart the graphics driver. When that recovery happens, Windows
frequently reflows every open window onto whatever monitor is still "active,"
so you end up with your entire desktop rearranged every time this happens.

This is a known class of driver/power-management bug, not a screensaver bug,
and it's inconsistent across GPU vendors, driver versions, and cable types
(HDMI vs DisplayPort). Updating drivers, disabling Fast Startup, and
adjusting GPU power management settings can reduce how often it happens, but
for a lot of affected systems nothing eliminates it outright.

## The solution

Rather than continuing to fight the driver bug directly, DisplayWakeFix
treats the symptom:

1. **Watches for the display power state change** using the
   `GUID_CONSOLE_DISPLAY_STATE` power setting notification, so it knows the
   instant your monitors go off, and the instant they come back on.
2. **Snapshots where every visible window lives the instant it detects the
   displays turning off**, right as the on-to-off transition happens. An
   earlier version of this app took that snapshot periodically (every 5
   minutes) while the display was on, but that had a subtle bug: if a
   driver hang or window shuffle happened *while the screens were already
   off* (which can last anywhere from a minute to overnight), the next
   periodic snapshot would capture that already-corrupted layout and
   silently overwrite the last good one. Snapshotting only at the
   on-to-off transition guarantees the saved layout is always the correct
   "before" state, never one captured during the window of vulnerability.
3. **On wake, automatically sends `Ctrl+Win+Shift+B` itself.** No more
   reaching for the keyboard shortcut by hand.
4. **A few seconds later, restores every window to its saved position and
   monitor**, including windows that are currently minimized. Windows
   tracks a "normal position" for every window (the rectangle it restores
   to) independently of whether it's currently minimized, maximized, or
   normal, so a minimized window can be corrected without being
   un-minimized, and a normal window snaps directly back into place.

It runs quietly in the system tray, with a right-click menu to force a
manual restore or exit.

## Requirements

- Windows 11 (should also work on Windows 10)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build it

## Building

```powershell
git clone https://github.com/maurinet/DisplayWakeFix.git

Run the build.bat file or build manually:

cd DisplayWakeFix
dotnet publish -c Release -r win-x64 --self-contained false -o dist
```

Your executable will be at `dist\DisplayWakeFix.exe`. The `-o dist` flag
keeps the output in one clean folder instead of scattering it across the
default `bin\Release\net8.0-windows\win-x64\` and
`bin\Release\net8.0-windows\win-x64\publish\` locations that a plain
`dotnet publish` produces.

## Running it

Double-click `DisplayWakeFix.exe`. It has no visible window, only a tray
icon. Right-click the tray icon for:

- **Restore windows now** — manually reapply the last saved layout
- **Move all windows to main screen** — relocate every open window that
  is currently on a *different* monitor onto your primary one (each
  keeping its original size and cascaded slightly so they don't fully
  overlap), including minimized windows. Windows already on the primary
  monitor are left exactly where they are. This is meant for exactly the
  situation where your secondary screens are off or unresponsive and you
  can't easily drag a window back yourself.
- **Exit**

### Run automatically at login

1. Press `Win+R`, type `shell:startup`, hit Enter.
2. Copy a shortcut to `DisplayWakeFix.exe` into that folder.

## Notes and limitations

- This does not patch the underlying GPU driver bug, that lives in your
  GPU vendor's driver. It automates the workaround and cleans up the
  side effect.
- If a snapshot ever catches windows mid-drag or in an unusual state, use
  **Restore windows now** from the tray menu to reapply the last good
  layout manually.
- Since the executable isn't code-signed, Windows SmartScreen or your
  antivirus may flag it on first run because it simulates keyboard input
  and repositions windows. You may need "More info" → "Run anyway," or
  to add an exclusion.
- If you notice windows landing on the correct monitor but slightly
  offset, that's typically a sign of a DPI-scaling mismatch between
  monitors running at different scale factors, worth opening an issue
  if you hit this.

## License

MIT — see [LICENSE](LICENSE).

## Author

[|¥|@µ®¡](https://mauweb.net)
