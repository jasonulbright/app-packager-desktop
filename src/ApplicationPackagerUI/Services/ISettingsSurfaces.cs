namespace ApplicationPackagerUI.Services;

/// <summary>The Packages page as the surfaces that save settings see it.</summary>
public interface IPublishActionHost
{
    /// <summary>Re-reads the stored settings into the publish buttons; enables a button whose missing setting was saved.</summary>
    void RefreshPublishActionState();

    /// <summary>Writes one line to the operation log.</summary>
    void AppendLog(string message);
}

/// <summary>The Settings page as the setup window sees it.</summary>
public interface ISettingsReload
{
    /// <summary>Re-reads the store after another surface has written to it.</summary>
    void ReloadFromStore();
}
