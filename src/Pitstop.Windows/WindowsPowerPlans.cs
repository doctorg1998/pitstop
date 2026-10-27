using System.Runtime.InteropServices;
using Pitstop.Core.Profiles;

namespace Pitstop.Windows;

/// <summary>Switches power plans through powrprof.dll (no admin, no powercfg.exe process per switch).</summary>
public sealed class WindowsPowerPlans : IPowerPlans
{
    public IRevertToken Activate(string planName)
    {
        var plans = List();
        var target = plans.FirstOrDefault(p => p.Name.Equals(planName, StringComparison.OrdinalIgnoreCase));
        if (target.Name is null)
            throw new InvalidOperationException(
                $"no power plan named '{planName}' on this PC. Available: {string.Join(", ", plans.Select(p => p.Name))}.");

        var previous = GetActive();
        if (previous == target.Id) return new NoOpToken($"power plan already '{target.Name}'");
        SetActive(target.Id);
        return new PlanToken(target.Name, previous);
    }

    public static IReadOnlyList<(Guid Id, string Name)> List()
    {
        var plans = new List<(Guid, string)>();
        for (uint i = 0; ; i++)
        {
            var size = (uint)Marshal.SizeOf<Guid>();
            var buffer = new byte[size];
            if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, buffer, ref size) != 0) break;
            var id = new Guid(buffer);
            plans.Add((id, FriendlyName(id)));
        }
        return plans;
    }

    public static Guid GetActive()
    {
        Check(PowerGetActiveScheme(IntPtr.Zero, out var ptr), "read the active power plan");
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    public static void SetActive(Guid id) => Check(PowerSetActiveScheme(IntPtr.Zero, ref id), "switch power plan");

    private static string FriendlyName(Guid id)
    {
        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref size);
        var buffer = new byte[size];
        if (PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return id.ToString();
        return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static void Check(uint result, string what)
    {
        if (result != 0) throw new InvalidOperationException($"couldn't {what} (Windows error {result}).");
    }

    private sealed class PlanToken(string name, Guid previous) : IRevertToken
    {
        private bool _done;
        public string Description => $"power plan '{name}'";
        public RestoreRecord? Record { get; } = new(RestoreKinds.PowerPlan, previous.ToString());

        public void Revert()
        {
            if (_done) return;
            SetActive(previous);
            _done = true;
        }
    }

    private const uint AccessScheme = 16;

    [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(IntPtr root, IntPtr schemeGuid, IntPtr subGroup, uint accessFlags, uint index, byte[]? buffer, ref uint bufferSize);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subGroup, IntPtr setting, byte[]? buffer, ref uint bufferSize);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr activeScheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
}
