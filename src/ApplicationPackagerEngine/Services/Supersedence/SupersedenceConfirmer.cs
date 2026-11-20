namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>The three answers of the confirmation: apply the supersedence, package without it, or stop the whole run.</summary>
public enum SupersedenceAnswer
{
    Yes,
    Skip,
    No
}

/// <summary>What the confirmation shows: the application that is being packaged and the older version that it would supersede.</summary>
public sealed record SupersedencePrompt(string NewTitle, string NewVersion, string OldTitle, string OldVersion, bool Uninstall);

/// <summary>An answer, and whether it also applies to the other applications of the same run.</summary>
public sealed record SupersedenceDecision(SupersedenceAnswer Answer, bool Remember);

/// <summary>
/// The confirmation before a supersedence is applied. One instance serves one package run (one or many applications): "Remember this"
/// keeps the answer for the rest of that run and no further confirmation is shown. A run that has no one to ask answers Skip.
/// </summary>
public sealed class SupersedenceConfirmer
{
    private readonly Func<SupersedencePrompt, Task<SupersedenceDecision>>? _ask;
    private readonly Action? _stopRun;
    private SupersedenceAnswer? _remembered;

    public SupersedenceConfirmer(Func<SupersedencePrompt, Task<SupersedenceDecision>>? ask, Action? stopRun = null)
    {
        _ask = ask;
        _stopRun = stopRun;
    }

    /// <summary>A confirmer that answers without asking: Yes for a command line with --yes, Skip for a run that has no one to ask.</summary>
    public static SupersedenceConfirmer Fixed(SupersedenceAnswer answer) =>
        new(_ => Task.FromResult(new SupersedenceDecision(answer, false)));

    /// <summary>True once the answer No was given in this run.</summary>
    public bool Declined { get; private set; }

    /// <summary>The answer of this run so far (the remembered one), or null when the next application asks again.</summary>
    public SupersedenceAnswer? Remembered => _remembered;

    /// <summary>
    /// <see cref="ConfirmAsync"/> for a run that must not fail because the question failed: a dialog that throws (anything but a cancellation)
    /// is logged in one line and counts as Skip, so the application is packaged without the supersedence.
    /// </summary>
    public async Task<SupersedenceAnswer> ConfirmSafeAsync(SupersedencePrompt prompt, Action<string>? log)
    {
        try { return await ConfirmAsync(prompt).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke($"Supersedence not applied   : the confirmation failed ({ex.Message}); the application is packaged without it.");
            return SupersedenceAnswer.Skip;
        }
    }

    public async Task<SupersedenceAnswer> ConfirmAsync(SupersedencePrompt prompt)
    {
        if (Declined) return SupersedenceAnswer.No;
        if (_remembered is { } remembered) return remembered;
        if (_ask is null) return SupersedenceAnswer.Skip;

        var decision = await _ask(prompt);
        if (decision.Remember) _remembered = decision.Answer;
        if (decision.Answer == SupersedenceAnswer.No)
        {
            Declined = true;
            _stopRun?.Invoke();
        }
        return decision.Answer;
    }
}
