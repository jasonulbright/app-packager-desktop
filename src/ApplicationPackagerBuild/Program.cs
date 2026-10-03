using System.Text;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerBuild;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // A redirected stream otherwise takes the OEM code page, which drops characters
        // the run log carries; an attached console keeps its own code page.
        if (Console.IsOutputRedirected)
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        if (Console.IsErrorRedirected)
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });

        var parsed = BuildArguments.Parse(args);
        if (!parsed.Ok)
        {
            Console.Error.WriteLine(parsed.Error);
            return 1;
        }
        var options = parsed.Options!;
        if (options.Help)
        {
            Console.Out.Write(BuildArguments.Usage);
            Console.Out.WriteLine();
            return 0;
        }

        using var output = new ConsoleBuildOutput();
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        try
        {
            // Set before any service resolves a path: settings, the workbench and the logs all read it.
            if (options.DataRoot is not null)
                Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, Path.GetFullPath(options.DataRoot));

            output.OpenLog(options.LogFolder ?? Path.Combine(AppDataPaths.LegacyRoot, "Logs"));
            if (output.LogPath is not null) output.Info($"Run log: {output.LogPath}");

            await using var services = new ServiceCollection()
                .AddPackagerServices()
                .AddSingleton<IBuildOutput>(output)
                .AddSingleton<BuildRunner>()
                .BuildServiceProvider();
            var result = await services.GetRequiredService<BuildRunner>().RunAsync(options, cancel.Token);
            foreach (var line in result.SummaryLines()) output.Info(line);
            return result.ExitCode;
        }
        catch (Exception ex)
        {
            output.Error($"ERROR: {ex.Message}");
            return 1;
        }
    }
}
