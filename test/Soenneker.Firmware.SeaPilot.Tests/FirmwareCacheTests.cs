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
            // Reissued keys still identify the same server-suggested package name.
            FirmwareDownload? refreshed = await cache.Find(firmware with { DownloadUri = new Uri("https://apps1.seagate.com/downloads/certificate.html?key=new-token") }, "firmware.zip", default);
            if (refreshed?.Sha256 != saved.Sha256) throw new Exception("Changing download tokens bypassed the cache.");
            FirmwareRelease otherDrive = firmware with { SerialNumber = firmware.SerialNumber + "OTHER", DownloadUri = new Uri("https://apps1.seagate.com/downloads/certificate.html?key=another-drive") };
            FirmwareDownload? shared = await cache.Find(otherDrive, "firmware.zip", default);
            if (shared?.FilePath != saved.FilePath || shared.Sha256 != saved.Sha256)
                throw new Exception("Different serial did not reuse the same cached binary.");
            if ((await pilot.GetCachedFirmware(otherDrive.SerialNumber)).Count != 1 ||
                (await pilot.GetCachedFirmware(firmware.SerialNumber)).Count != 1)
                throw new Exception("Shared package lost serial associations.");
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
            if (recovered?.Sha256 != saved.Sha256) throw new Exception("Completed orphan was downloaded again instead of recovered.");
        }
        finally { await directories.Delete(root); }
    }
}
