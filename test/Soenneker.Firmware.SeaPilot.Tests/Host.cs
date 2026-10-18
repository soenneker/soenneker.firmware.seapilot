using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Soenneker.TestHosts.Unit;
using Soenneker.Utils.Test;
using Soenneker.Firmware.SeaPilot.Registrars;
using System;
using System.IO;

namespace Soenneker.Firmware.SeaPilot.Tests;

public sealed class Host : UnitTestHost
{
    public override Task InitializeAsync()
    {
        SetupIoC(Services);

        return base.InitializeAsync();
    }

    private static void SetupIoC(IServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.AddSerilog(dispose: false);
        });

        IConfiguration config = TestUtil.BuildConfig();
        services.AddSingleton(config);

        services.AddSeaPilotAsScoped(options => options.CacheDirectory = Environment.GetEnvironmentVariable("SEAPILOT_TEST_CACHE_DIRECTORY")
            ?? Path.Combine(Path.GetTempPath(), $"seapilot-test-cache-{Guid.NewGuid()}"));
    }
}
