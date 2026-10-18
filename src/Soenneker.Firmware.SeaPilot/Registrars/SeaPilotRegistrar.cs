using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Firmware.SeaPilot.Abstract;
using System;
using Soenneker.Firmware.SeaPilot.Options;
using Soenneker.OpenSeaChest.Util.Registrars;
using Soenneker.Playwrights.Installation.Registrars;
using Soenneker.Utils.File.Registrars;
using Soenneker.Utils.Directory.Registrars;
using Soenneker.Compression.Zip.Registrars;

namespace Soenneker.Firmware.SeaPilot.Registrars;

/// <summary>
/// Seagate firmware discovery and downloads using OpenSeaChest and stealth browser automation.
/// </summary>
public static class SeaPilotRegistrar
{
    /// <summary>
    /// Adds <see cref="ISeaPilot"/> as a singleton service. <para/>
    /// </summary>
    public static IServiceCollection AddSeaPilotAsSingleton(this IServiceCollection services, Action<SeaPilotOptions>? configure = null)
    {
        services.AddOpenSeaChestUtilAsSingleton().AddPlaywrightInstallationUtilAsSingleton();
        services.AddFileUtilAsSingleton().AddDirectoryUtilAsSingleton();
        services.TryAddSingleton<FirmwareFileStore>();
        services.TryAddSingleton<FirmwareCache>();
        services.AddZipUtilAsSingleton();
        services.TryAddSingleton<FirmwarePackageResolver>();
        services.TryAddSingleton<FirmwareUpdateWriter>();
        services.TryAddSingleton<SeaPilotDriveDiscovery>();
        services.TryAddSingleton<FirmwareUpdateLock>();
        services.AddOptions<SeaPilotOptions>();
        if (configure is not null) services.Configure(configure);
        services.TryAddSingleton<ISeaPilot, SeaPilot>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="ISeaPilot"/> as a scoped service. <para/>
    /// </summary>
    public static IServiceCollection AddSeaPilotAsScoped(this IServiceCollection services, Action<SeaPilotOptions>? configure = null)
    {
        services.AddOpenSeaChestUtilAsScoped().AddPlaywrightInstallationUtilAsScoped();
        services.AddFileUtilAsScoped().AddDirectoryUtilAsScoped();
        services.TryAddScoped<FirmwareFileStore>();
        services.TryAddScoped<FirmwareCache>();
        services.AddZipUtilAsScoped();
        services.TryAddScoped<FirmwarePackageResolver>();
        services.TryAddScoped<FirmwareUpdateWriter>();
        services.TryAddScoped<SeaPilotDriveDiscovery>();
        services.TryAddSingleton<FirmwareUpdateLock>();
        services.AddOptions<SeaPilotOptions>();
        if (configure is not null) services.Configure(configure);
        services.TryAddScoped<ISeaPilot, SeaPilot>();

        return services;
    }
}
