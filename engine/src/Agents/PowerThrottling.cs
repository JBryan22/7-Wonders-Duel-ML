using System.Runtime.InteropServices;

namespace SevenWondersDuel.Agents;

/// <summary>
/// Windows 11 power throttling ("EcoQoS", what Task Manager calls efficiency mode): by default Windows decides per
/// process, and a windowless background process, like the training loop started from the "Start training" shortcut
/// and its <c>7wd selfplay</c> / <c>match</c> children, can be put on the efficient cores at low clocks. That cut
/// Self-Play from ~1,500 to ~320 positions/s (ticket 28). <see cref="OptOut"/> tells Windows this process always wants
/// full speed. Nothing to do on Linux / macOS.
/// </summary>
public static class PowerThrottling
{
    const int ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS
    const uint CurrentVersion = 1;
    public const uint ExecutionSpeed = 0x1; // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
    const uint IgnoreTimerResolution = 0x4; // PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION

    [StructLayout(LayoutKind.Sequential)]
    struct State
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref State info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetProcessInformation(IntPtr process, int infoClass, ref State info, uint size);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    /// <summary>Opt this process out of power throttling (Windows); true if Windows accepted it, false elsewhere.</summary>
    public static bool OptOut()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            // Control = the policies we decide ourselves; State 0 = throttling off for them.
            var state = new State { Version = CurrentVersion, ControlMask = ExecutionSpeed | IgnoreTimerResolution, StateMask = 0 };
            return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<State>());
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            return false; // Windows before 8
        }
    }

    /// <summary>
    /// This process's (ControlMask, StateMask) on Windows, null elsewhere. Execution speed in ControlMask with it absent
    /// from StateMask means opted out; absent from ControlMask means Windows decides.
    /// </summary>
    public static (uint Control, uint State)? Current()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var state = new State { Version = CurrentVersion };
        return GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<State>())
            ? (state.ControlMask, state.StateMask)
            : null;
    }
}
