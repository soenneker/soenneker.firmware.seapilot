using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Soenneker.Firmware.SeaPilot.Dtos;

namespace Soenneker.Firmware.SeaPilot;

/// <summary>Selects the newest unambiguous offering within a compatible firmware branch.</summary>
public static class FirmwareReleaseSelector
{
    public static (FirmwareRelease Release, string Revision) Select(
        IReadOnlyList<(FirmwareRelease Release, string Revision)> offerings, string? installedRevision)
    {
        static string Branch(string revision)
        {
            Match match = Regex.Match(revision, @"^([A-Za-z]{1,4})[0-9]{2,8}$", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() :
                throw new InvalidDataException("An offering has an unknown firmware revision; latest selection is ambiguous.");
        }

        string? branch = installedRevision is null ? null : Branch(installedRevision);
        var candidates = new List<(FirmwareRelease Release, string Revision)>();
        foreach (var offering in offerings)
        {
            string offeredBranch = Branch(offering.Revision);
            if (branch is null) branch = offeredBranch;
            if (offeredBranch != branch)
            {
                if (installedRevision is null)
                    throw new InvalidDataException("Cannot select the latest firmware across different branches without an installed revision.");
                continue;
            }
            candidates.Add(offering);
        }
        if (candidates.Count == 0)
            throw new InvalidDataException("No offering matches the installed firmware branch.");
        var latest = candidates[0];
        foreach (var candidate in candidates.Skip(1))
            if (FirmwarePackageResolver.NeedsUpdate(latest.Revision, candidate.Revision)) latest = candidate;
        if (candidates.Where(candidate => !FirmwarePackageResolver.NeedsUpdate(candidate.Revision, latest.Revision))
            .Select(candidate => candidate.Release.DownloadUri).Distinct().Count() != 1)
            throw new InvalidDataException("Multiple distinct packages offer the latest firmware revision.");
        return latest;
    }
}
