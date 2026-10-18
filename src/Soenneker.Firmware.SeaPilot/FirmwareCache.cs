using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Soenneker.Firmware.SeaPilot.Dtos;
using Soenneker.Firmware.SeaPilot.Options;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.Json;
using Soenneker.Asyncs.Semaphores;

namespace Soenneker.Firmware.SeaPilot;

/// <summary>Persists completed firmware packages and verifies their checksums before reuse.</summary>
public sealed class FirmwareCache(
    IFileUtil fileUtil,
    IDirectoryUtil directoryUtil,
    FirmwareFileStore store,
    IOptions<SeaPilotOptions> options,
    ILogger<FirmwareCache> logger)
{
    // Serialize downloads across scoped and singleton instances to avoid duplicate transfers within a process.
    public static AsyncSemaphore DownloadGate { get; } = new(1, 1);

    private string Root => Path.GetFullPath(options.Value.CacheDirectory);

    public async Task<FirmwareDownload?> Find(FirmwareRelease firmware, string? filename,
        CancellationToken cancellationToken)
    {
        if (filename is not null)
        {
            FirmwareFileStore.ValidateFilename(filename);
            string directory = GetDirectory(filename);
            FirmwareDownload? saved = await Read(Path.Combine(directory, "receipt.json"), cancellationToken);
            if (saved is not null)
            {
                saved = saved with { Firmware = firmware };
                await WriteReceipt(saved, cancellationToken);
                return saved;
            }
            // A completed atomic binary may survive a failed receipt write. Validate it before recovering the receipt.
            string binary = Path.Combine(directory, filename);
            if (await fileUtil.Exists(binary, cancellationToken))
            {
                try
                {
                    FirmwareDownload orphan =
                        await store.Inspect(binary, filename, firmware, long.MaxValue, cancellationToken);
                    await WriteReceipt(orphan, cancellationToken);
                    return orphan;
                }
                catch (InvalidDataException)
                {
                    await Quarantine(binary, cancellationToken);
                }
            }

            return null;
        }

        foreach (FirmwareDownload cached in await GetAll(null, cancellationToken))
            if (cached.Firmware.DownloadUri == firmware.DownloadUri)
            {
                FirmwareDownload reused = cached with { Firmware = firmware };
                await WriteReceipt(reused, cancellationToken);
                return reused;
            }
        return null;
    }

    public async Task<IReadOnlyList<FirmwareDownload>> GetAll(string? serial, CancellationToken cancellationToken)
    {
        var result = new List<FirmwareDownload>();
        if (!await directoryUtil.Exists(Root, cancellationToken))
            return result;
        foreach (string receipt in await directoryUtil.GetFilesByExtension(Root, "json", recursive: true,
                     cancellationToken: cancellationToken))
        {
            if (!Path.GetFileName(receipt).Equals("receipt.json", StringComparison.Ordinal)) continue;
            FirmwareDownload? download = await Read(receipt, cancellationToken, serial);
            if (download is not null)
                result.Add(download);
        }

        return result.AsReadOnly();
    }

    public async Task<FirmwareDownload> Store(string source, string filename, FirmwareRelease firmware,
        CancellationToken cancellationToken)
    {
        FirmwareFileStore.ValidateFilename(filename);
        string directory = GetDirectory(filename);
        FirmwareDownload result = await store.Save(source, filename, directory, firmware,
            options.Value.MaximumDownloadBytes, cancellationToken);
        await WriteReceipt(result, cancellationToken);
        return result;
    }

    private string GetDirectory(string filename)
    {
        // Stable content identity: Seagate's opaque download query keys can change between serial lookups.
        string key =
            Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(filename)));
        return Path.Combine(Root, key);
    }

    private async Task WriteReceipt(FirmwareDownload download, CancellationToken cancellationToken)
    {
        string receipt = Path.Combine(Path.GetDirectoryName(download.FilePath)!, "receipt.json");
        var serials = new List<string>();
        if (await fileUtil.Exists(receipt, cancellationToken))
        {
            FirmwareCacheEntry? previous = JsonUtil.Deserialize(await fileUtil.Read(receipt, cancellationToken: cancellationToken),
                SeaPilotJsonContext.Default.FirmwareCacheEntry);
            if (previous is not null) serials.AddRange(previous.Serials.Append(previous.Firmware.SerialNumber));
        }
        serials.Add(download.Firmware.SerialNumber);
        var entry = new FirmwareCacheEntry
        {
            Firmware = download.Firmware, Filename = Path.GetFileName(download.FilePath), Length = download.Length,
            Sha256 = download.Sha256, Serials = serials.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
        await fileUtil.WriteAtomically(receipt,
            JsonUtil.Serialize(entry, SeaPilotJsonContext.Default.FirmwareCacheEntry),
            cancellationToken: cancellationToken);
    }

    private async Task<FirmwareDownload?> Read(string receipt, CancellationToken cancellationToken, string? serial = null)
    {
        if (!await fileUtil.Exists(receipt, cancellationToken))
            return null;
        string? binary = null;
        try
        {
            FirmwareCacheEntry entry =
                JsonUtil.Deserialize(await fileUtil.Read(receipt, cancellationToken: cancellationToken),
                    SeaPilotJsonContext.Default.FirmwareCacheEntry) ??
                throw new InvalidDataException("Empty cache receipt.");
            FirmwareFileStore.ValidateFilename(entry.Filename);
            SeagateResponseParser.ValidateUri(entry.Firmware.DownloadUri);
            string directory = GetDirectory(entry.Filename);
            if (!Path.GetFullPath(receipt).Equals(Path.Combine(directory, "receipt.json"),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Cache identity does not match its directory.");
            if (serial is not null && !entry.Serials.Append(entry.Firmware.SerialNumber).Contains(serial, StringComparer.OrdinalIgnoreCase)) return null;
            binary = Path.Combine(directory, entry.Filename);
            if (!await fileUtil.Exists(binary, cancellationToken))
                return null;
            FirmwareDownload result = await store.Inspect(binary, entry.Filename, entry.Firmware, long.MaxValue,
                cancellationToken);
            if (result.Length != entry.Length ||
                !result.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Cached firmware checksum does not match its receipt.");
            return serial is null ? result : result with { Firmware = result.Firmware with { SerialNumber = serial } };
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException
                                              or NullReferenceException)
        {
            logger.LogWarning(exception, "Ignoring invalid firmware cache receipt {Receipt}", receipt);
            if (binary is not null && await fileUtil.Exists(binary, cancellationToken))
                await Quarantine(binary, cancellationToken);
            await Quarantine(receipt, cancellationToken);
            return null;
        }
    }

    private async Task Quarantine(string path, CancellationToken cancellationToken)
    {
        await fileUtil.Move(path, path + $".invalid.{Guid.NewGuid()}", cancellationToken: cancellationToken);
    }
}

