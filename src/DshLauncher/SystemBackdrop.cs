using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using System.Windows;

namespace DshLauncher;

internal enum SystemBackdropResult
{
    Enabled,
    Fallback
}

internal enum SystemBackdropReason
{
    Enabled,
    ForcedDisabled,
    HighContrast,
    TransparencyEffectsDisabled,
    DwmCompositionDisabled,
    UnsupportedWindows,
    SimulatedDwmFailure,
    InvalidWindowHandle,
    DwmApiUnavailable,
    DwmApiFailed
}

/// <summary>
/// Runtime inputs used by the pure backdrop decision. Keeping these values
/// explicit makes the fallback policy testable without creating an HWND.
/// </summary>
internal readonly record struct SystemBackdropEnvironment(
    int WindowsBuild,
    bool DwmCompositionEnabled,
    bool TransparencyEffectsEnabled,
    bool HighContrast,
    bool ForceDisabled = false,
    bool SimulateDwmFailure = false,
    bool IsWindows = true)
{
    public bool SystemTransparencyEnabled => TransparencyEffectsEnabled;

    public bool ForceDisable => ForceDisabled;

    public bool IsWindows11Supported => IsWindows && WindowsBuild >= SystemBackdrop.MinimumWindowsBuild;
}

internal sealed record SystemBackdropDecision(
    SystemBackdropResult Result,
    SystemBackdropReason ReasonCode,
    string Reason,
    int? HResult = null,
    bool OptionalAttributesApplied = false)
{
    public bool ShouldApply => Result == SystemBackdropResult.Enabled;

    public bool UseSystemBackdrop => ShouldApply;

    public bool IsFallback => !ShouldApply;

    public bool IsSuccess => ShouldApply;

    // Short aliases used by the window integration boundary.
    public bool Applied => ShouldApply;

    public string Detail => Reason;
}

/// <summary>
/// Pure capability and fallback policy for the Windows system backdrop.
/// </summary>
internal static class SystemBackdropPolicy
{
    public static SystemBackdropDecision Evaluate(SystemBackdropEnvironment environment)
    {
        if (environment.ForceDisabled)
        {
            return Fallback(
                SystemBackdropReason.ForcedDisabled,
                "System backdrop is forced off by the caller or environment.");
        }

        if (environment.HighContrast)
        {
            return Fallback(
                SystemBackdropReason.HighContrast,
                "System backdrop is disabled in high-contrast mode.");
        }

        if (!environment.IsWindows || !environment.IsWindows11Supported)
        {
            return Fallback(
                SystemBackdropReason.UnsupportedWindows,
                $"Windows build {environment.WindowsBuild} does not support the system backdrop.");
        }

        if (!environment.DwmCompositionEnabled)
        {
            return Fallback(
                SystemBackdropReason.DwmCompositionDisabled,
                "DWM composition is unavailable.");
        }

        if (!environment.TransparencyEffectsEnabled)
        {
            return Fallback(
                SystemBackdropReason.TransparencyEffectsDisabled,
                "Windows transparency effects are disabled.");
        }

        if (environment.SimulateDwmFailure)
        {
            return Fallback(
                SystemBackdropReason.SimulatedDwmFailure,
                "DWM failure was requested by the test environment.");
        }

        return new(
            SystemBackdropResult.Enabled,
            SystemBackdropReason.Enabled,
            "Windows 11 DWM system backdrop is available.");
    }

    internal static bool IsEnvironmentFlagEnabled(string? value) =>
        value is not null
        && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));

    private static SystemBackdropDecision Fallback(SystemBackdropReason reasonCode, string reason) =>
        new(SystemBackdropResult.Fallback, reasonCode, reason);
}

/// <summary>
/// Applies the Windows backdrop to a real HWND after the pure policy allows it.
/// Only documented DWM entry points are used.
/// </summary>
internal static class SystemBackdrop
{
    internal const int MinimumWindowsBuild = 22621;
    internal const int DwmWindowAttributeSystemBackdropType = 38;
    internal const int DwmSystemBackdropTypeMainWindow = 2;
    internal const int DwmSystemBackdropTypeNone = 1;

    internal const string ForceDisableEnvironmentVariable = "DSH_LAUNCHER_DISABLE_BACKDROP";
    internal const string SimulateDwmFailureEnvironmentVariable = "DSH_LAUNCHER_SIMULATE_DWM_FAILURE";

    private const string AlternateForceDisableEnvironmentVariable = "DSH_DISABLE_SYSTEM_BACKDROP";
    private const string AlternateForceDisableEnvironmentVariable2 = "DSH_FORCE_DISABLE_BACKDROP";
    private const string AlternateSimulationEnvironmentVariable = "DSH_SIMULATE_DWM_FAILURE";
    private const string AlternateSimulationEnvironmentVariable2 = "DSH_TEST_DWM_FAILURE";
    private const int DwmWindowAttributeUseImmersiveDarkMode = 20;
    private const int DwmWindowAttributeWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;

    public static SystemBackdropEnvironment DetectEnvironment()
    {
        var isWindows = OperatingSystem.IsWindows();
        if (!isWindows)
        {
            return new(
                WindowsBuild: 0,
                DwmCompositionEnabled: false,
                TransparencyEffectsEnabled: false,
                HighContrast: false,
                ForceDisabled: true,
                SimulateDwmFailure: false,
                IsWindows: false);
        }

        var forceDisabled =
            SystemBackdropPolicy.IsEnvironmentFlagEnabled(
                Environment.GetEnvironmentVariable(ForceDisableEnvironmentVariable))
            || SystemBackdropPolicy.IsEnvironmentFlagEnabled(
                Environment.GetEnvironmentVariable(AlternateForceDisableEnvironmentVariable))
            || SystemBackdropPolicy.IsEnvironmentFlagEnabled(
                Environment.GetEnvironmentVariable(AlternateForceDisableEnvironmentVariable2));
        var simulateDwmFailure =
            SystemBackdropPolicy.IsEnvironmentFlagEnabled(
                Environment.GetEnvironmentVariable(SimulateDwmFailureEnvironmentVariable))
            || SystemBackdropPolicy.IsEnvironmentFlagEnabled(
                Environment.GetEnvironmentVariable(AlternateSimulationEnvironmentVariable))
            || SystemBackdropPolicy.IsEnvironmentFlagEnabled(
                Environment.GetEnvironmentVariable(AlternateSimulationEnvironmentVariable2));

        return new(
            WindowsBuild: Environment.OSVersion.Version.Build,
            DwmCompositionEnabled: DetectDwmComposition(),
            TransparencyEffectsEnabled: ReadTransparencyEffectsEnabled(),
            HighContrast: SystemParameters.HighContrast,
            ForceDisabled: forceDisabled,
            SimulateDwmFailure: simulateDwmFailure,
            IsWindows: true);
    }

    public static SystemBackdropDecision Evaluate(SystemBackdropEnvironment environment) =>
        SystemBackdropPolicy.Evaluate(environment);

    public static SystemBackdropDecision Apply(
        IntPtr hwnd,
        SystemBackdropEnvironment? environment = null)
    {
        var capabilities = environment ?? DetectEnvironment();
        var policy = SystemBackdropPolicy.Evaluate(capabilities);
        if (!policy.ShouldApply)
        {
            return policy;
        }

        if (hwnd == IntPtr.Zero)
        {
            return Fallback(
                SystemBackdropReason.InvalidWindowHandle,
                "The system backdrop requires a valid window handle.");
        }

        var mainWindowValue = DwmSystemBackdropTypeMainWindow;
        try
        {
            var hResult = DwmSetWindowAttribute(
                hwnd,
                DwmWindowAttributeSystemBackdropType,
                ref mainWindowValue,
                sizeof(int));
            if (hResult != 0)
            {
                return Fallback(
                    SystemBackdropReason.DwmApiFailed,
                    $"DwmSetWindowAttribute failed for the system backdrop (HRESULT 0x{hResult:X8}).",
                    hResult);
            }

            // These attributes improve shell integration where available.
            // Their failure is deliberately non-critical: attribute 38 is the
            // capability signal and is the only call that controls the result.
            var optionalAttributesApplied =
                TrySetOptionalAttribute(hwnd, DwmWindowAttributeUseImmersiveDarkMode, 1)
                & TrySetOptionalAttribute(
                    hwnd,
                    DwmWindowAttributeWindowCornerPreference,
                    DwmWindowCornerPreferenceRound);
            var reason = optionalAttributesApplied
                ? "Windows 11 DWM system backdrop enabled."
                : "Windows 11 DWM system backdrop enabled; optional shell attributes were unavailable.";
            return new(
                SystemBackdropResult.Enabled,
                SystemBackdropReason.Enabled,
                reason,
                OptionalAttributesApplied: optionalAttributesApplied);
        }
        catch (DllNotFoundException)
        {
            return Fallback(
                SystemBackdropReason.DwmApiUnavailable,
                "The Windows DWM API is unavailable on this platform.");
        }
        catch (EntryPointNotFoundException)
        {
            return Fallback(
                SystemBackdropReason.DwmApiUnavailable,
                "The installed DWM API does not expose DwmSetWindowAttribute.");
        }
        catch (BadImageFormatException)
        {
            return Fallback(
                SystemBackdropReason.DwmApiUnavailable,
                "The installed DWM API has an incompatible architecture.");
        }
        catch (PlatformNotSupportedException)
        {
            return Fallback(
                SystemBackdropReason.DwmApiUnavailable,
                "The current platform does not support the Windows DWM API.");
        }
    }

    /// <summary>
    /// Window-facing entry point. A policy fallback also clears a previously
    /// applied backdrop when a valid HWND is available, so accessibility or
    /// environment changes do not leave stale material behind.
    /// </summary>
    internal static SystemBackdropDecision TryApply(IntPtr hwnd, bool highContrast)
    {
        var environment = DetectEnvironment() with { HighContrast = highContrast };
        var policy = SystemBackdropPolicy.Evaluate(environment);
        if (policy.ShouldApply)
        {
            return Apply(hwnd, environment);
        }

        return ClearExistingBackdrop(hwnd, policy);
    }

    private static bool DetectDwmComposition()
    {
        try
        {
            var hResult = DwmIsCompositionEnabled(out var enabled);
            return hResult == 0 && enabled;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TrySetOptionalAttribute(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static SystemBackdropDecision ClearExistingBackdrop(
        IntPtr hwnd,
        SystemBackdropDecision policy)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
        {
            return policy;
        }

        var none = DwmSystemBackdropTypeNone;
        try
        {
            // A failed clear is still a policy fallback. It must never turn a
            // safe unsupported path into an application failure, but the
            // diagnostic result must not claim success when HRESULT failed.
            var hResult = DwmSetWindowAttribute(
                hwnd,
                DwmWindowAttributeSystemBackdropType,
                ref none,
                sizeof(int));
            return hResult == 0
                ? policy with
                {
                    Reason = $"{policy.Reason} Existing DWM backdrop was cleared."
                }
                : policy with
                {
                    HResult = hResult,
                    Reason = $"{policy.Reason} Existing DWM backdrop could not be cleared "
                        + $"(HRESULT 0x{hResult:X8}); the opaque fallback remains active."
                };
        }
        catch (DllNotFoundException)
        {
            return policy;
        }
        catch (EntryPointNotFoundException)
        {
            return policy;
        }
        catch (BadImageFormatException)
        {
            return policy;
        }
        catch (PlatformNotSupportedException)
        {
            return policy;
        }

    }

    private static bool ReadTransparencyEffectsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("EnableTransparency");
            return value switch
            {
                null => true,
                int number => number != 0,
                long number => number != 0,
                string text => !text.Equals("0", StringComparison.OrdinalIgnoreCase)
                    && !text.Equals("false", StringComparison.OrdinalIgnoreCase),
                _ => true
            };
        }
        catch (SecurityException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static SystemBackdropDecision Fallback(
        SystemBackdropReason reasonCode,
        string reason,
        int? hResult = null) =>
        new(SystemBackdropResult.Fallback, reasonCode, reason, hResult);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmIsCompositionEnabled(
        [MarshalAs(UnmanagedType.Bool)] out bool enabled);
}

internal readonly record struct MotionDecision(
    bool ReducedMotion,
    bool AllowTranslation,
    bool AllowScale,
    TimeSpan Duration)
{
    public bool IsImmediate => Duration == TimeSpan.Zero;
}

/// <summary>
/// Pure reduced-motion policy. SystemParameters are only read by Current;
/// Evaluate is intentionally independent from WPF and can be unit-tested.
/// </summary>
internal static class MotionPolicy
{
    internal const string ForceDisableEnvironmentVariable = "DSH_DISABLE_ANIMATIONS";
    internal const string ForceReducedMotionEnvironmentVariable = "DSH_FORCE_REDUCED_MOTION";

    private const string AlternateForceDisableEnvironmentVariable = "DSH_REDUCED_MOTION";

    internal static MotionDecision Evaluate(
        bool clientAreaAnimation,
        bool highContrast,
        bool forceDisabled = false)
    {
        var reducedMotion = forceDisabled || highContrast || !clientAreaAnimation;
        return reducedMotion
            ? new(
                ReducedMotion: true,
                AllowTranslation: false,
                AllowScale: false,
                Duration: TimeSpan.Zero)
            : new(
                ReducedMotion: false,
                AllowTranslation: true,
                AllowScale: true,
                Duration: TimeSpan.FromMilliseconds(180));
    }

    internal static bool ShouldReduceMotion(
        bool clientAreaAnimationEnabled,
        bool highContrast,
        bool forceDisabled = false) =>
        Evaluate(clientAreaAnimationEnabled, highContrast, forceDisabled).ReducedMotion;

    internal static MotionDecision Current()
    {
        var forceDisabled =
            SystemBackdropPolicy.IsEnvironmentFlagEnabled(
                Environment.GetEnvironmentVariable(ForceDisableEnvironmentVariable))
            || SystemBackdropPolicy.IsEnvironmentFlagEnabled(
                Environment.GetEnvironmentVariable(ForceReducedMotionEnvironmentVariable))
            || SystemBackdropPolicy.IsEnvironmentFlagEnabled(
                Environment.GetEnvironmentVariable(AlternateForceDisableEnvironmentVariable));

        return Evaluate(
            clientAreaAnimation: SystemParameters.ClientAreaAnimation,
            highContrast: SystemParameters.HighContrast,
            forceDisabled: forceDisabled);
    }
}
