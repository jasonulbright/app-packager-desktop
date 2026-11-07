using Xunit;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A test that opens a WPF window takes the focus of the computer it runs on. On the dev PC that steals the keyboard
/// from the person who works there. Such a test runs only when the run opts in, and never on the dev PC.
/// </summary>
/// <remarks>
/// The opt-in is the environment variable <see cref="OptInVariable"/> set to 1. The CI workflow and the lab run
/// scripts set it. The dev PC (<see cref="RefusedComputer"/>) refuses even with the opt-in. Without the opt-in the
/// tests are skipped with the reason of <see cref="SkipReason(string?, string)"/>, never failed.
/// </remarks>
internal static class WindowTestGuard
{
    public const string OptInVariable = "APD_ALLOW_WINDOW_TESTS";

    public const string RefusedComputer = "AYW";

    /// <summary>Null when window tests may run; otherwise the reason they are skipped.</summary>
    public static string? SkipReason() => SkipReason(Environment.GetEnvironmentVariable(OptInVariable), Environment.MachineName);

    /// <summary>The decision for a given opt-in value and computer name.</summary>
    public static string? SkipReason(string? optIn, string computerName)
    {
        if (string.Equals(computerName?.Trim(), RefusedComputer, StringComparison.OrdinalIgnoreCase))
            return $"Window tests open windows and take the focus. They never run on the dev PC ({RefusedComputer}), with or without {OptInVariable}. Run them in CI or on a lab machine.";
        if (optIn?.Trim() != "1")
            return $"Window tests open windows and take the focus. They run only when {OptInVariable}=1 is set: CI and lab runs set it.";
        return null;
    }
}

/// <summary>A <see cref="FactAttribute"/> for a test that opens a window: skipped unless <see cref="WindowTestGuard"/> allows it.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowFactAttribute : FactAttribute
{
    public WindowFactAttribute() => Skip = WindowTestGuard.SkipReason();
}

/// <summary>A <see cref="TheoryAttribute"/> for a test that opens a window: skipped unless <see cref="WindowTestGuard"/> allows it.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowTheoryAttribute : TheoryAttribute
{
    public WindowTheoryAttribute() => Skip = WindowTestGuard.SkipReason();
}
