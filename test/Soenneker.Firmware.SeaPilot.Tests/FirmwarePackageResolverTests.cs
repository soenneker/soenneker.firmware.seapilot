using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Soenneker.Firmware.SeaPilot.Dtos;
using Soenneker.OpenSeaChest.Util.Dtos;
using Soenneker.Tests.HostedUnit;

namespace Soenneker.Firmware.SeaPilot.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class FirmwarePackageResolverTests(Host host) : HostedUnitTest(host)
{
    [Test]
    public void SelectsCurrentImageAndExcludesPreviousAndSubrelease()
    {
        string selected = FirmwarePackageResolver.SelectPrimaryImage([
            "firmware/previous firmware/Exos-SN03.LOD", "firmware/Exos-SN06.LOD",
            "firmware/subrelease firmware/Exos-SB02.LOD"]);
        if (selected != "firmware/Exos-SN06.LOD") throw new Exception("Incorrect primary image.");
        foreach (string[] entries in new[] { new[] { "firmware/A-SN06.LOD", "firmware/B-SN06.LOD" },
                     new[] { "firmware/previous firmware/Exos-SN03.LOD" }, new[] { "../Exos-SN06.LOD" } })
        {
            try { FirmwarePackageResolver.SelectPrimaryImage(entries); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Unsafe or ambiguous image set accepted.");
        }
    }

    [Test]
    public void SkipsCurrentAndNewerRevisionsAndRejectsUnknownBranches()
    {
        if (FirmwarePackageResolver.NeedsUpdate("SN06", "SN06") || FirmwarePackageResolver.NeedsUpdate("SN07", "SN06") ||
            !FirmwarePackageResolver.NeedsUpdate("SN04", "SN06")) throw new Exception("Revision comparison failed.");
        try { FirmwarePackageResolver.NeedsUpdate("SB02", "SN06"); }
        catch (InvalidDataException) { return; }
        throw new Exception("Unknown branch migration accepted.");
    }

    [Test]
    public void RejectsIncompatibleSectorSize()
    {
        var drive = new OpenSeaChestDrive { Handle = "PD0", LogicalBlockSizeBytes = 4096, Details = new Dictionary<string, string>(), RawDeviceInfo = [] };
        try { FirmwarePackageResolver.ValidateSectorSize(drive, "Exos-512E-SN06.LOD"); }
        catch (InvalidDataException) { return; }
        throw new Exception("Incompatible sector size accepted.");
    }

    [Test]
    public async Task ExtractsAndRetainsOnlySelectedImageForReuse()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"seapilot-package-{Guid.NewGuid()}");
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "Exos-SN06.zip");
        try
        {
            using (ZipArchive archive = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                using (Stream current = archive.CreateEntry("firmware/Exos-SN06.LOD").Open()) current.Write([1, 2, 3, 4]);
                using (Stream previous = archive.CreateEntry("firmware/previous firmware/Exos-SN03.LOD").Open()) previous.Write([5, 6, 7, 8]);
            }
            var release = new FirmwareRelease { SerialNumber = "PACKAGE", Title = "Exos", DownloadUri = new Uri("https://www.seagate.com/Exos-SN06.zip") };
            FirmwareDownload package = await Resolve<FirmwareFileStore>(true).Inspect(source, Path.GetFileName(source), release, long.MaxValue, default);
            var resolver = Resolve<FirmwarePackageResolver>(true);
            FirmwareImageSelection first = await resolver.Prepare(package, default);
            FirmwareImageSelection second = await resolver.Prepare(package, default);
            if (first != second || first.TargetRevision != "SN06" || first.Sha256.Length != 64 || !File.Exists(first.FilePath))
                throw new Exception("Extracted image was not retained and reused.");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task OfficialSeagatePackageLayout()
    {
        string? path = Environment.GetEnvironmentVariable("SEAPILOT_TEST_PACKAGE");
        Skip.Unless(path is not null, "Set SEAPILOT_TEST_PACKAGE to an official downloaded ZIP.");
        var release = new FirmwareRelease { SerialNumber = "FIXTURE", Title = "Official package", DownloadUri = new Uri("https://www.seagate.com/fixture.zip") };
        FirmwareDownload package = await Resolve<FirmwareFileStore>(true).Inspect(path!, Path.GetFileName(path!), release, long.MaxValue, default);
        FirmwareImageSelection image = await Resolve<FirmwarePackageResolver>(true).Prepare(package, default);
        if (image.TargetRevision != "TN04" || !File.Exists(image.FilePath)) throw new Exception("Official package resolution failed.");
        Console.WriteLine($"Prepared {image.FilePath}, SHA256 {image.Sha256}");
    }
}
