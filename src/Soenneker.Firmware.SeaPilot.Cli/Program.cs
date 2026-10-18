using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soenneker.Firmware.SeaPilot.Registrars;

namespace Soenneker.Firmware.SeaPilot.Cli;

internal static class Program
{
    private const string Help = """
        SeaPilot — Seagate firmware discovery, downloads and updates

        seapilot drives
        seapilot lookup <serial>
        seapilot download <serial> <directory>
        seapilot cached <serial>
        seapilot update-all
        seapilot --version

        update-all discovers drives, prepares shared cached firmware, then
        automatically updates eligible Seagate drives without prompts.
        Requires administrator/root device access. Back up data before flashing.
        Ctrl+C stops preparation or subsequent drives; active writes finish.
        Exit codes: 0 success, 1 failure, 2 usage error, 130 cancelled.

        SEAPILOT_CACHE_DIRECTORY overrides the persistent firmware cache location.
        Chromium and native tools are installed on demand; internet is required
        for lookups. Linux may require Playwright browser system dependencies.
        Extract the entire release archive before running seapilot.
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || (args.Length == 1 && args[0] is "--help" or "-h"))
        {
            Console.WriteLine(Help);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--version")
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version);
            return 0;
        }
        using ILoggerFactory loggerFactory = LoggerFactory.Create(logging => logging
                .SetMinimumLevel(LogLevel.Warning)
                .AddFilter("Soenneker.Firmware.SeaPilot", LogLevel.Information)
                .AddSimpleConsole(console =>
                {
                    console.SingleLine = true;
                    console.TimestampFormat = "HH:mm:ss ";
                })
                .AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace));
        ILogger logger = loggerFactory.CreateLogger("Soenneker.Firmware.SeaPilot.Cli.Program");
        bool valid = args[0] switch
        {
            "drives" or "update-all" => args.Length == 1,
            "lookup" or "cached" => args.Length == 2,
            "download" => args.Length == 3,
            _ => false
        };
        if (!valid || args.Skip(1).Any(string.IsNullOrWhiteSpace))
        {
            logger.LogError("Invalid arguments.");
            Console.WriteLine(Help);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            logger.LogInformation("Cancellation requested. If a firmware write is active, waiting for it to finish.");
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ILoggerFactory>(loggerFactory);
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSeaPilotAsSingleton(options =>
            {
                string? directory = Environment.GetEnvironmentVariable("SEAPILOT_CACHE_DIRECTORY");
                if (!string.IsNullOrWhiteSpace(directory)) options.CacheDirectory = Path.GetFullPath(directory);
            });
            services.AddSingleton<SeaPilotCommand>();
            await using ServiceProvider provider = services.BuildServiceProvider();
            return await provider.GetRequiredService<SeaPilotCommand>().Run(args, cancellation.Token);
        }
        catch (OperationCanceledException) { logger.LogInformation("Cancelled. Previously completed updates remain applied."); return 130; }
        catch (Exception exception) { logger.LogError(exception, "SeaPilot command failed: {Message}", exception.Message); return 1; }
        finally { Console.CancelKeyPress -= handler; }
    }

}



