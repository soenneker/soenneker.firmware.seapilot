[![NuGet](https://img.shields.io/nuget/v/soenneker.firmware.seapilot.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.firmware.seapilot/)
[![Build](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.firmware.seapilot/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.firmware.seapilot/actions/workflows/publish-package.yml)
[![Downloads](https://img.shields.io/nuget/dt/soenneker.firmware.seapilot.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.firmware.seapilot/)

# Soenneker.Firmware.SeaPilot

Find, download, and apply Seagate drive firmware. Available as a standalone CLI and a .NET 10 library.

## CLI

Download your platform's archive from [Releases](https://github.com/soenneker/soenneker.firmware.seapilot/releases): Windows x64, Linux x64, or Linux ARM64. Extract the whole archive, including `.playwright` and `Resources`. No .NET installation is needed.

Run from the extracted directory:

```powershell
# Show attached drives and their current firmware
.\seapilot.exe drives

# Check firmware offered for a serial number
.\seapilot.exe lookup YOURSERIAL

# Download without flashing
.\seapilot.exe download YOURSERIAL .\firmware

# List verified downloads already cached for this serial
.\seapilot.exe cached YOURSERIAL
```

On Linux, use `./seapilot` instead of `.\seapilot.exe`.

`drives` reports inaccessible devices individually and continues with the others. Automatic updates skip those devices; they cannot pass the identity check required for a firmware write. If every discovered device is inaccessible, `drives` exits with code `1`. Scan failures and cancellation still stop the command.

Drive discovery and updates require administrator/root access. Lookups require internet access; Chromium installs automatically on first use. Linux may also need Playwright's browser system dependencies.

`download` requires exactly one offered package. No offering does not mean the drive is up to date. Downloads print the saved path, size, and SHA-256. Download logs show the offered revision when it can be read from the URL or filename, and the installed revision during updates. Progress shows a percentage when Chromium reports the total size; otherwise it shows bytes received. Package inspection confirms the target revision before flashing.

### Update all eligible drives

**This writes firmware without confirmation prompts. Back up data first and keep drives powered during writes.**

From an elevated PowerShell session:

```powershell
.\seapilot.exe update-all
```

On Linux:

```bash
sudo ./seapilot update-all
```

SeaPilot checks each drive's serial with Seagate, prepares the firmware, then updates drives one at a time. It revalidates each current offering against the cached resource URL, strong ETag, size, and local SHA-256 before reusing it, checks device identity and image checksums before writing, and skips identical or newer firmware in the same branch. Errors on one drive do not prevent updates to other eligible drives.

Automatic updates require a ZIP with one primary `.lod` or `.bin` image at its root or directly in `firmware/`, matching package/image revisions, and a newer revision in the same firmware branch. Ambiguous packages, executable installers, and unknown upgrade paths return errors.

Ctrl+C stops preparation or subsequent drives; an active write finishes first. Completed updates are not rolled back. After each write, SeaPilot checks the active revision. A completed write with a different active revision is reported as activation unverified. Read the native output for activation instructions; a power cycle may be required. Check again afterward:

```powershell
.\seapilot.exe verify YOURSERIAL SN06
```

Use the actual target revision. `verify` matches by serial even if the device handle changed, and exits `0` only when the target revision is active.

Updates take a machine-wide file lock at `%ProgramData%\Soenneker\SeaPilot\update.lock` on Windows or `/var/lib/soenneker/seapilot/update.lock` on Linux, independent of the cache location. A second update process waits; Ctrl+C cancels its wait. Cache operations and image preparation use `cache.lock` inside the cache directory. Do not delete or replace lock files while SeaPilot is running.

### Cache and exit codes

Packages persist under the current user's local application data directory in `Soenneker/Firmware/SeaPilot`. To use another directory:

```powershell
$env:SEAPILOT_CACHE_DIRECTORY = 'D:\SeaPilot\cache'
```

```bash
export SEAPILOT_CACHE_DIRECTORY=/path/to/cache
```

Running as a different account can change the default cache location. `cached` works offline. CLI downloads and automatic updates first request server metadata. Matching final URL, strong ETag, size, and local SHA-256 allow reuse without transferring the package again, including across serials. Changed or missing validators require a fresh download. Older cache entries without validators need one new download to record them. The library's `DownloadFirmware` can replay a verified package for the same serial and URL offline. Different destination files are not overwritten. SHA-256 checks detect changed files; they are not manufacturer signatures.

Exit codes: `0` success, `1` failure, `2` invalid arguments, `130` cancelled. Skipped drives or an observed revision that differs from the target alone do not make `update-all` fail; verification errors do. Use `--help` for usage and `--version` for the installed version.

## .NET library

Requires .NET 10.

```bash
dotnet add package Soenneker.Firmware.SeaPilot
```

Register and use `ISeaPilot`:

```csharp
using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Firmware.SeaPilot.Abstract;
using Soenneker.Firmware.SeaPilot.Registrars;

var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
services.AddSeaPilotAsSingleton();

await using var provider = services.BuildServiceProvider();
var pilot = provider.GetRequiredService<ISeaPilot>();

var lookup = await pilot.FindFirmware("YOURSERIAL");
foreach (var release in lookup.Releases)
    Console.WriteLine($"{release.Title}: {release.DownloadUri}");

// Requires exactly one offering. Does not flash the drive.
var download = await pilot.DownloadLatestFirmware("YOURSERIAL", "./firmware");
Console.WriteLine(download.FilePath);
```

In a hosted application, use its existing configuration and logging. `AddSeaPilotAsScoped()` is also available.

| Method | Use |
| --- | --- |
| `FindFirmware(serial)` | Check recognition status and current offerings. |
| `FindFirmwareForDevice(device)` | Look up one discovered device handle, including its installed revision. |
| `DownloadLatestFirmware(serial, directory)` | Download when exactly one package is offered. |
| `DownloadFirmware(release, directory)` | Download a release selected from the lookup results. |
| `GetCachedFirmware(serial)` | List verified cached downloads without a network request. |
| `VerifyFirmware(serial, targetRevision)` | Check the active revision after a write or power cycle. |
| `UpdateAllDriveFirmwares()` | Apply updates with the same behavior as CLI `update-all`. Inspect `Error`, `SkipReason`, `WriteCompleted`, `Verification`, `VerificationError`, and `NativeOutput`. |

All methods accept an optional `CancellationToken`. Network and lookup failures throw; automatic updates return per-drive errors, while cancellation and operation-level failures can still throw.

Configure options during registration:

```csharp
services.AddSeaPilotAsSingleton(options =>
{
    options.CacheDirectory = "./seapilot-cache";
    options.Headless = true;
    options.LookupTimeout = TimeSpan.FromMinutes(2);
    options.DownloadTimeout = TimeSpan.FromMinutes(30);
    options.MaximumDownloadBytes = 1024L * 1024 * 1024;
});
```

The values shown are the defaults except for the cache directory. Timeouts begin after browser installation. The size limit is checked after Chromium downloads to its temporary directory. `SEAPILOT_CACHE_DIRECTORY` applies only to the CLI; configure the library through these options.