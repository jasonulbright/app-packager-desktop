namespace ApplicationPackagerEngine.Services.InstallerScripts;

/// <summary>
/// The SetRegView state of an NSIS script walked in code order. The script stores the view as
/// a flag value: KEY_WOW64_64KEY (256) for SetRegView 64, KEY_WOW64_32KEY (512) for SetRegView 32,
/// 0 for SetRegView default. "SetRegView lastused" swaps the current and the previous value.
/// </summary>
public sealed class NsisRegView
{
    private int _current;
    private int _previous;

    /// <summary>The value of the last SetRegView statement other than lastused; -1 when the script has none.</summary>
    public int LastSetValue { get; private set; } = -1;

    /// <summary>True when any statement selects the 32-bit view.</summary>
    public bool Selected32 { get; private set; }

    public void Set(int value)
    {
        _previous = _current;
        _current = value;
        LastSetValue = value;
        if (value == 512) Selected32 = true;
    }

    public void LastUsed() => (_current, _previous) = (_previous, _current);

    public bool Is64 => _current == 256;

    /// <summary>"64", "32" or empty for the default view.</summary>
    public string Current => _current switch { 256 => "64", 512 => "32", _ => "" };

    /// <summary>
    /// True when a write that code order places in the default view is in the 64-bit view: the
    /// script's last SetRegView is 64 and it never selects 32, so that statement runs in .onInit
    /// before any section. Valid after the whole script was walked.
    /// </summary>
    public bool InitSets64 => LastSetValue == 256 && !Selected32;

    /// <summary>Applies one SetFlag (opcode 13) entry; flag 12 is the registry view.</summary>
    public void Apply(int flag, int lastUsedParameter, Func<string> valueText)
    {
        if (flag != 12) return;
        if (lastUsedParameter != 0) { LastUsed(); return; }
        int.TryParse(valueText(), out var value);
        Set(value);
    }
}
