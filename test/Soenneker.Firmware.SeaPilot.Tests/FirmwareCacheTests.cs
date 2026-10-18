using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Soenneker.Firmware.SeaPilot.Abstract;
using Soenneker.Firmware.SeaPilot.Dtos;
using Soenneker.Tests.HostedUnit;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.Directory.Abstract;

namespace Soenneker.Firmware.SeaPilot.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class FirmwareCacheTests(Host host) : HostedUnitTest(host)
{
    [Test]
    public async Task ReusesVerifiedBinaryOfflineAfterDestinationFailure()
    {
        var files = Resolve<IFileUtil>(true);
        var directories = Resolve<IDirectoryUtil>(true);
        string root = await directories.CreateTempDirectory();
        var firmware = new FirmwareRelease { SerialNumber = "CACHE" + Random.Shared.NextInt64(1, long.MaxValue), Title = "Current firmware",
            DownloadUri = new Uri("https://apps1.seagate.com/downloads/certificate.html?action=performDownload&key=offline-test") };
        try
        {
            string source = Path.Combine(root, "source.zip");
            using (ZipArchive archive = await ZipFile.OpenAsync(source, ZipArchiveMode.Create))
            using (Stream stream = await archive.CreateEntry("firmware.lod").OpenAsync()) stream.Write([1, 2, 3, 4]);
            var cache = Resolve<FirmwareCache>(true);
            FirmwareDownload saved = await cache.Store(source, "firmware.zip", firmware, default);
            await files.Delete(source);
            string blocked = Path.Combine(root, "blocked");
            await files.Write(blocked, "not a directory");
            var pilot = Resolve<ISeaPilot>(true);
            try { await pilot.DownloadFirmware(firmware, blocked); throw new Exception("Expected destination failure."); }
            catch (IOException) { }
            if (!await files.Exists(saved.FilePath)) throw new Exception("Destination failure removed cached binary.");
            FirmwareDownload retry = await pilot.DownloadFirmware(firmware, Path.Combine(root, "output"));
            FirmwareDownload repeated = await pilot.DownloadFirmware(firmware, Path.Combine(root, "output"));
            if (retry.Sha256 != saved.Sha256 || repeated.FilePath != retry.FilePath) throw new Exception("Cache replay was not idempotent.");
            if ((await pilot.GetCachedFirmware(firmware.SerialNumber)).Count != 1) throw new Exception("Offline inventory lost cached binary.");
            // A name or new URL cannot associate unverified bytes with another offering.
            FirmwareRelease otherDrive = firmware with { SerialNumber = firmware.SerialNumber + "OTHER",
                DownloadUri = new Uri("https://apps1.seagate.com/downloads/certificate.html?key=another-drive") };
            if (await cache.Find(otherDrive, "firmware.zip", default) is not null)
                throw new Exception("Filename alone authorized cached bytes for another offering.");
            FirmwareDownload shared = await cache.Store(saved.FilePath, "firmware.zip", otherDrive, default);
            if (shared.FilePath != saved.FilePath || (await pilot.GetCachedFirmware(otherDrive.SerialNumber)).Count != 1 ||
                (await pilot.GetCachedFirmware(firmware.SerialNumber)).Count != 1)
                throw new Exception("Verified identical content did not retain serial associations.");
        }
        finally { await directories.Delete(root); }
    }

    [Test]
    public async Task CorruptCachedBinaryIsNeverReused()
    {
        var files = Resolve<IFileUtil>(true);
        var directories = Resolve<IDirectoryUtil>(true);
        string root = await directories.CreateTempDirectory();
        var firmware = new FirmwareRelease { SerialNumber = "CACHE" + Random.Shared.NextInt64(1, long.MaxValue), Title = "Firmware", DownloadUri = new Uri("https://apps1.seagate.com/test.bin") };
        try
        {
            string source = Path.Combine(root, "test.bin");
            await files.Write(source, new byte[] { 1, 2, 3, 4 });
            var cache = Resolve<FirmwareCache>(true);
            FirmwareDownload saved = await cache.Store(source, "test.bin", firmware, default);
            await files.Write(saved.FilePath, new byte[] { 4, 3, 2, 1 });
            if (await cache.Find(firmware, "test.bin", default) is not null) throw new Exception("Checksum mismatch was ignored.");
            FirmwareDownload recovered = await cache.Store(source, "test.bin", firmware, default);
            if (recovered.Sha256 != saved.Sha256) throw new Exception("Corruption recovery failed.");
        }
        finally { await directories.Delete(root); }
    }

    [Test]
    public async Task CompletedBinarySurvivesMissingReceipt()
    {
        var files = Resolve<IFileUtil>(true);
        var directories = Resolve<IDirectoryUtil>(true);
        string root = await directories.CreateTempDirectory();
        var firmware = new FirmwareRelease { SerialNumber = "CACHE" + Random.Shared.NextInt64(1, long.MaxValue), Title = "Firmware", DownloadUri = new Uri("https://apps1.seagate.com/orphan.bin") };
        try
        {
            string source = Path.Combine(root, "orphan.bin");
            await files.Write(source, new byte[] { 1, 2, 3, 4 });
            var cache = Resolve<FirmwareCache>(true);
            FirmwareDownload saved = await cache.Store(source, "orphan.bin", firmware, default);
            await files.Delete(Path.Combine(Path.GetDirectoryName(saved.FilePath)!, "receipt.json"));
            FirmwareDownload? recovered = await cache.Find(firmware, "orphan.bin", default);
            if (recovered is not null) throw new Exception("An orphan authorized unverified bytes.");
            FirmwareDownload stored = await cache.Store(source, "orphan.bin", firmware, default);
            if (stored.Sha256 != saved.Sha256) throw new Exception("Validated content did not recover its receipt.");
        }
        finally { await directories.Delete(root); }
    }
    [Test]
    public async Task SameFilenameWithDifferentContentCannotReuseOldPackage()
    {
        var cache = Resolve<FirmwareCache>(true);
        string root = Path.Combine(Path.GetTempPath(), $"seapilot-content-{Guid.NewGuid()}");
        Directory.CreateDirectory(root);
        try
        {
            var first = new FirmwareRelease { SerialNumber = $"A{Random.Shared.NextInt64(1, long.MaxValue)}", Title = "Firmware",
                DownloadUri = new Uri("https://apps1.seagate.com/first.zip") };
            var second = first with { SerialNumber = first.SerialNumber + "B", DownloadUri = new Uri("https://apps1.seagate.com/second.zip") };
            string source = Path.Combine(root, "source.bin");
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            FirmwareDownload old = await cache.Store(source, "same.bin", first, default);
            if (await cache.Find(second, "same.bin", default) is not null) throw new Exception("Name collision reused old bytes.");
            await File.WriteAllBytesAsync(source, [5, 6, 7, 8]);
            FirmwareDownload current = await cache.Store(source, "same.bin", second, default);
            if (old.FilePath == current.FilePath || old.Sha256 == current.Sha256) throw new Exception("Different content overwrote old package.");
            if ((await cache.Find(second, "same.bin", default))?.Sha256 != current.Sha256) throw new Exception("Wrong offering replayed.");
            if ((await cache.Find(first, "same.bin", default))?.Sha256 != old.Sha256) throw new Exception("First offering was lost.");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task ReissuedContentAtSameUrlCannotReplayAnArbitraryVersion()
    {
        var cache = Resolve<FirmwareCache>(true);
        var firmware = new FirmwareRelease { SerialNumber = $"REISSUED{Random.Shared.NextInt64(1, long.MaxValue)}",
            Title = "Firmware", DownloadUri = new Uri("https://apps1.seagate.com/reissued.bin") };
        string source = Path.Combine(Path.GetTempPath(), $"seapilot-reissue-{Guid.NewGuid()}.bin");
        try
        {
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            await cache.Store(source, "reissued.bin", firmware, default);
            await File.WriteAllBytesAsync(source, [5, 6, 7, 8]);
            await cache.Store(source, "reissued.bin", firmware, default);
            if (await cache.Find(firmware, "reissued.bin", default) is not null)
                throw new Exception("An ambiguous URL replayed arbitrary historical bytes.");
        }
        finally { File.Delete(source); }
    }

}
