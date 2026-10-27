using System.Runtime.InteropServices;
using Pitstop.Core.Profiles;

namespace Pitstop.Windows;

/// <summary>
/// Lists audio devices with the documented Core Audio API and sets the default device with IPolicyConfig.
/// IPolicyConfig is undocumented, but it's what the Sound control panel itself uses and has been stable
/// since Windows 7. All three roles (console, multimedia, communications) are switched together.
/// </summary>
public sealed class WindowsAudioDevices : IAudioDevices
{
    public IRevertToken SetDefaultOutput(string nameContains) => SetDefault(DataFlow.Render, nameContains, "output");
    public IRevertToken SetDefaultInput(string nameContains) => SetDefault(DataFlow.Capture, nameContains, "input");

    public static IReadOnlyList<(string Id, string Name)> List(DataFlow flow)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(flow, DeviceStateActive, out var collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
            var devices = new List<(string, string)>();
            for (uint i = 0; i < count; i++)
            {
                Marshal.ThrowExceptionForHR(collection.Item(i, out var device));
                devices.Add((DeviceId(device), FriendlyName(device)));
                Marshal.ReleaseComObject(device);
            }
            Marshal.ReleaseComObject(collection);
            return devices;
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    public static string? GetDefaultId(DataFlow flow, Role role)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0) return null;
            try { return DeviceId(device); }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    public static void SetDefaultId(string deviceId, Role role)
    {
        var policy = (IPolicyConfig)new PolicyConfigComObject();
        try { Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, role)); }
        finally { Marshal.ReleaseComObject(policy); }
    }

    private static IRevertToken SetDefault(DataFlow flow, string nameContains, string kind)
    {
        var devices = List(flow);
        var matches = devices.Where(d => d.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            throw new InvalidOperationException(
                $"no connected {kind} device matches '{nameContains}'. Connected: {string.Join(", ", devices.Select(d => d.Name))}.");
        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"'{nameContains}' matches more than one {kind} device ({string.Join(", ", matches.Select(d => d.Name))}). Use more of the name.");

        var target = matches[0];
        var previous = Roles.ToDictionary(r => r, r => GetDefaultId(flow, r));
        if (previous.Values.All(id => id == target.Id)) return new NoOpToken($"{kind} already '{target.Name}'");

        foreach (var role in Roles) SetDefaultId(target.Id, role);
        return new AudioToken($"{kind} '{target.Name}'", previous);
    }

    private static readonly Role[] Roles = { Role.Console, Role.Multimedia, Role.Communications };

    private sealed class AudioToken(string description, Dictionary<Role, string?> previous) : IRevertToken
    {
        private bool _done;
        public string Description { get; } = description;

        public RestoreRecord? Record { get; } = new(RestoreKinds.AudioDefault,
            string.Join(";", previous.Where(p => p.Value is not null).Select(p => $"{(int)p.Key}={p.Value}")));

        public void Revert()
        {
            if (_done) return;
            foreach (var (role, id) in previous)
                if (id is not null) SetDefaultId(id, role);
            _done = true;
        }
    }

    /// <summary>Restores a journal record written by <see cref="AudioToken"/>.</summary>
    public static void Restore(string value)
    {
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && int.TryParse(part[..eq], out var role)) SetDefaultId(part[(eq + 1)..], (Role)role);
        }
    }

    private static string DeviceId(IMMDevice device)
    {
        Marshal.ThrowExceptionForHR(device.GetId(out var ptr));
        try { return Marshal.PtrToStringUni(ptr)!; }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    private static string FriendlyName(IMMDevice device)
    {
        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0 /* STGM_READ */, out var store));
        try
        {
            var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
            try { return value.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.pointer) ?? "" : ""; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private const uint DeviceStateActive = 1;

    public enum DataFlow { Render = 0, Capture = 1 }
    public enum Role { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant { public ushort vt; public ushort r1, r2, r3; public IntPtr pointer; public IntPtr pad; }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pvar);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumeratorComObject { }
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] private class PolicyConfigComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    // Only SetDefaultEndpoint is called; the earlier methods are declared to keep the vtable order right.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
