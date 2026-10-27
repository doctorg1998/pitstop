using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Pitstop.Core.Profiles;

namespace Pitstop.Windows;

/// <summary>Mutes toast notifications for the current user via the documented per-user setting.</summary>
public sealed class WindowsNotificationToggle : INotificationToggle
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    private const string ValueName = "ToastEnabled";

    public IRevertToken Mute()
    {
        var previous = Read();
        if (previous == 0) return new NoOpToken("notifications already muted");
        Write(0);
        return new ToastToken(previous);
    }

    /// <summary>Returns the stored value, or null if the user never changed it (Windows treats that as on).</summary>
    public static int? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as int?;
    }

    public static void Write(int? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (value is null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        else key.SetValue(ValueName, value.Value, RegistryValueKind.DWord);
    }

    /// <summary>Restores a journal value: "absent" or the original number.</summary>
    public static void Restore(string value) => Write(int.TryParse(value, out var v) ? v : null);

    private sealed class ToastToken(int? previous) : IRevertToken
    {
        private bool _done;
        public string Description => "notifications muted";
        public RestoreRecord? Record { get; } = new(RestoreKinds.Toasts, previous?.ToString() ?? "absent");

        public void Revert()
        {
            if (_done) return;
            Write(previous);
            _done = true;
        }
    }
}

/// <summary>
/// Closes apps politely (WM_CLOSE to their windows, like clicking X) and launches apps. Never force-kills,
/// only touches the current user's own session, and refuses protected Windows processes.
/// </summary>
public sealed class WindowsAppControl : IAppControl
{
    public IRevertToken RequestClose(string exeName)
    {
        if (ProtectedProcesses.IsProtected(exeName)) throw new InvalidOperationException("protected Windows process");

        var name = Path.GetFileNameWithoutExtension(exeName);
        var session = Process.GetCurrentProcess().SessionId;
        var targets = Process.GetProcessesByName(name).Where(p => p.SessionId == session).ToList();
        if (targets.Count == 0) return new NoOpToken($"{exeName} wasn't running");

        string? relaunchPath = null;
        var asked = 0;
        foreach (var p in targets)
        {
            using (p)
            {
                try { relaunchPath ??= p.MainModule?.FileName; } catch (System.ComponentModel.Win32Exception) { }
                asked += PostCloseToWindows(p.Id);
            }
        }
        if (asked == 0)
            throw new InvalidOperationException("it has no window that accepts a close request, so it was left running");

        return new CloseToken(exeName, relaunchPath);
    }

    public IRevertToken Launch(string path)
    {
        using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return new NoOpToken($"launched {Path.GetFileName(path)}");
    }

    /// <summary>Posts WM_CLOSE to every top-level window of a process, including hidden tray-app windows.</summary>
    private static int PostCloseToWindows(int pid)
    {
        var count = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid && PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero)) count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }

    private sealed class CloseToken(string exe, string? relaunchPath) : IRevertToken
    {
        private bool _done;
        public string Description => $"closed {exe}";
        public RestoreRecord? Record => null; // after a crash we don't relaunch apps the user may not want back

        public void Revert()
        {
            if (_done || relaunchPath is null) return;
            _done = true;
            if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Length > 0) return; // user reopened it
            using var _ = Process.Start(new ProcessStartInfo(relaunchPath) { UseShellExecute = true });
        }
    }

    private const uint WmClose = 0x0010;
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>Restores settings recorded in the revert journal after an unclean exit.</summary>
public sealed class WindowsRestoreHandler : IRestoreHandler
{
    public void Restore(RestoreRecord record)
    {
        switch (record.Kind)
        {
            case RestoreKinds.PowerPlan: WindowsPowerPlans.SetActive(Guid.Parse(record.Value)); break;
            case RestoreKinds.AudioDefault: WindowsAudioDevices.Restore(record.Value); break;
            case RestoreKinds.Toasts: WindowsNotificationToggle.Restore(record.Value); break;
        }
    }
}
