using System;
using System.IO;
using Soenneker.Firmware.SeaPilot.Dtos;

namespace Soenneker.Firmware.SeaPilot.Tests;

public sealed class FirmwareReleaseSelectorTests
{
    private static FirmwareRelease Release(string key) => new()
    {
        SerialNumber = "EXAMPLE", Title = "Exos", DownloadUri = new Uri("https://apps1.seagate.com/downloads/certificate.html?key=" + key)
    };

    [Test]
    public void SelectsNewestRegardlessOfOrderAndComparesNumerically()
    {
        FirmwareRelease older = Release("1"), newer = Release("2");
        foreach (var offers in new[] { new[] { (older, "SN05"), (newer, "SN06") }, new[] { (newer, "SN06"), (older, "SN05") } })
            if (FirmwareReleaseSelector.Select(offers, "SN01").Release != newer) throw new Exception("Wrong offering.");
        if (FirmwareReleaseSelector.Select([(older, "SN99"), (newer, "SN100")], null).Release != newer)
            throw new Exception("Revisions must be compared numerically.");
        if (FirmwareReleaseSelector.Select([(older, "SB99"), (newer, "SN06")], "SN07").Release != newer)
            throw new Exception("Wrong firmware branch.");
    }

    [Test]
    public void RejectsUnknownRevisionsBranchesAndLatestTies()
    {
        foreach (Action select in new Action[]
        {
            () => FirmwareReleaseSelector.Select([(Release("1"), "unknown"), (Release("2"), "SN06")], "SN01"),
            () => FirmwareReleaseSelector.Select([(Release("1"), "SB99"), (Release("2"), "SN06")], null),
            () => FirmwareReleaseSelector.Select([(Release("1"), "SB99")], "SN01"),
            () => FirmwareReleaseSelector.Select([(Release("1"), "SN06"), (Release("2"), "SN06")], "SN01")
        })
        {
            try { select(); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Ambiguous offering accepted.");
        }
    }

    [Test]
    public void AllowsDuplicateLinksAndOlderTies()
    {
        FirmwareRelease newest = Release("3");
        if (FirmwareReleaseSelector.Select([(Release("1"), "SN05"), (Release("2"), "SN05"),
            (newest, "SN06"), (newest, "SN06")], null).Release != newest)
            throw new Exception("Unambiguous latest offering rejected.");
    }
}
