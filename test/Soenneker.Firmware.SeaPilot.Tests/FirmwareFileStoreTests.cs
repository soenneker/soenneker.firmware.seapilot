using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Firmware.SeaPilot.Dtos;

namespace Soenneker.Firmware.SeaPilot.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class FirmwareFileStoreTests(Host host) : Soenneker.Tests.HostedUnit.HostedUnitTest(host)
{
    private static FirmwareRelease Release() => new() { SerialNumber = "ABC123", Title = "Test", DownloadUri = new Uri("https://apps1.seagate.com/firmware.zip") };

    [Test]
    public async Task SavesExactBytesAndChecksumWithoutOverwriting()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"seapilot-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "input.zip");
            using (ZipArchive archive = await ZipFile.OpenAsync(source, ZipArchiveMode.Create))
            using (Stream stream = await archive.CreateEntry("firmware.lod").OpenAsync()) stream.Write([1, 2, 3, 4]);
            byte[] bytes = await File.ReadAllBytesAsync(source);
            string output = Path.Combine(directory, "output");
            FirmwareDownload result = await Resolve<FirmwareFileStore>(true).Save(source, "firmware.zip", output, Release(), 10000, default);
            if (result.Sha256 != Convert.ToHexStringLower(SHA256.HashData(bytes)) || result.Length != bytes.Length ||
                !(await File.ReadAllBytesAsync(result.FilePath)).SequenceEqual(bytes)) throw new Exception("Downloaded bytes or checksum changed.");
            await File.WriteAllTextAsync(result.FilePath, "existing content");
            try { await Resolve<FirmwareFileStore>(true).Save(source, "firmware.zip", output, Release(), 10000, default); }
            catch (IOException)
            {
                if (await File.ReadAllTextAsync(result.FilePath) != "existing content" || Directory.GetFiles(output).Length != 1)
                    throw new Exception("Existing file changed or a partial file leaked.");
                return;
            }
            throw new Exception("Overwrote an existing file.");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    [Arguments("../escape.zip", "binary", 1000)]
    [Arguments("..\\escape.zip", "binary", 1000)]
    [Arguments("file.zip", "<html>error</html>", 1000)]
    [Arguments("file.zip", "{\"error\":true}", 1000)]
    [Arguments("file.zip", "garbage", 1000)]
    [Arguments("file.lod", "", 1000)]
    [Arguments("file.lod", "too large", 2)]
    public async Task InvalidFilesNeverReachDestination(string filename, string content, long maximum)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"seapilot-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source");
            string output = Path.Combine(directory, "output");
            await File.WriteAllTextAsync(source, content);
            try { await Resolve<FirmwareFileStore>(true).Save(source, filename, output, Release(), maximum, default); }
            catch (InvalidDataException)
            {
                if (Directory.Exists(output)) throw new Exception("Invalid file created destination output.");
                return;
            }
            throw new Exception("Expected file rejection.");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task CancelledSaveCreatesNoFiles()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        try { await Resolve<FirmwareFileStore>(true).Save("unused", "unused.zip", "unused", Release(), 100, cancellation.Token); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation before file access.");
    }
}
