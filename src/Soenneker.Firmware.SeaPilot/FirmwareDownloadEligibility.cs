using System.IO;
using System.Threading.Tasks;
using System;
using System.Net.Http.Headers;

namespace Soenneker.Firmware.SeaPilot;

internal static class FirmwareDownloadEligibility
{
    internal static string? GetSkipReason(string filename, string? installed)
    {
        if (string.IsNullOrWhiteSpace(installed))
            return null;
        string target;
        try
        {
            target = FirmwarePackageResolver.GetRevision(filename);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        if (installed.Equals(target, StringComparison.OrdinalIgnoreCase))
            return $"Already running {target}; skipping download and update.";
        try
        {
            if (!FirmwarePackageResolver.NeedsUpdate(installed, target))
                return $"Installed {installed} is newer than offered {target}; skipping download and update.";
        }
        catch (InvalidDataException)
        {
        } // Unknown branches still require the normal applicability checks.

        return null;
    }

    internal static string? GetHeaderFilename(string? header)
    {
        if (!ContentDispositionHeaderValue.TryParse(header, out var value))
            return null;
        return (value.FileNameStar ?? value.FileName)?.Trim('"');
    }

    internal static void Check(string filename, string? installed)
    {
        if (GetSkipReason(filename, installed) is { } reason)
            throw new FirmwareDownloadSkippedException(reason);
    }

    internal static async Task CheckDownload(string filename, string? installed, Func<Task> cancel)
    {
        if (GetSkipReason(filename, installed) is not { } reason)
            return;
        await cancel();
        throw new FirmwareDownloadSkippedException(reason);
    }
}