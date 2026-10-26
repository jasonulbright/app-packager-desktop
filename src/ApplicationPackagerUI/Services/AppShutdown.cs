using Microsoft.Extensions.Hosting;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The last step of the application exit. After the host stops, the WPF dispatcher shutdown
/// detaches each text store from Text Services Framework; ITfThreadMgr.Deactivate in that
/// detach can block the UI thread with no end, and the process then runs on without a window.
/// The process therefore ends as soon as the host is stopped and disposed.
/// </summary>
public static class AppShutdown
{
    public static void Complete(IHost? host, TimeSpan stopTimeout, Action exit)
    {
        try
        {
            if (host is null) return;
            HostShutdown.StopAndWait(host.StopAsync, stopTimeout);
            host.Dispose();
        }
        finally
        {
            exit();
        }
    }
}
