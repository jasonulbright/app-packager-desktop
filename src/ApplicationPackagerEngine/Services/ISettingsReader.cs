namespace ApplicationPackagerEngine.Services;

/// <summary>Read-only settings access for shared execution services.</summary>
public interface ISettingsReader<out TSettings>
{
    TSettings Load();
}

/// <summary>Settings access needed by a non-interactive build run.</summary>
public interface IRunSettingsReader<TSettings> : ISettingsReader<TSettings>
{
    void ApplyRunOverrides(Action<TSettings> overrides);
    void ReadFileOncePerRun();
    string? TakeLoadWarning();
}
