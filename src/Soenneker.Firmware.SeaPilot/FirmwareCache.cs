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
using Soenneker.Locks.Files;

namespace Soenneker.Firmware.SeaPilot;

/// <summary>Persists completed firmware packages and verifies their checksums before reuse.</summary>
public sealed class FirmwareCache(
    IFileUtil fileUtil,
    IDirectoryUtil directoryUtil,
    FirmwareFileStore store,
    IOptions<SeaPilotOptions> options,
    ILogger<FirmwareCache> logger)
{
    private string Root => Path.GetFullPath(options.Value.CacheDirectory);

    private async Task<FileLockLease> Acquire(CancellationToken token)
    {
        await directoryUtil.Create(Root, cancellationToken: token);
        return await new FileLock(Path.Combine(Root, "cache.lock")).Acquire(token);
    }

    public async Task<FirmwareDownload?> Find(FirmwareRelease firmware, string? filename,
        CancellationToken cancellationToken)
    {
        await using FileLockLease lease = await Acquire(cancellationToken);
        // Offline replay is limited to an already recorded serial and exact offering URL.
        // Filename equality never establishes that two offerings have identical bytes.
        FirmwareDownload? match = null;
        foreach (FirmwareDownload cached in await GetAllCore(firmware.SerialNumber, cancellationToken))
            if (cached.Firmware.DownloadUri == firmware.DownloadUri &&
                (filename is null || Path.GetFileName(cached.FilePath) == filename))
            {
                if (match is not null && match.Sha256 != cached.Sha256) return null;
                match = cached with { Firmware = firmware };
            }
        return match;
    }

    internal async Task<FirmwareDownload?> FindOffering(FirmwareRelease firmware, string revision, CancellationToken token)
    {
        await using FileLockLease lease = await Acquire(token);
        if (string.IsNullOrWhiteSpace(firmware.Title) || string.IsNullOrWhiteSpace(firmware.Description)) return null;
        FirmwareDownload? match = null;
        foreach (FirmwareDownload download in await GetAllCore(null, token))
        {
            if (download.Length > options.Value.MaximumDownloadBytes ||
                download.Firmware.Title != firmware.Title || download.Firmware.Description != firmware.Description) continue;
            string cachedRevision;
            try { cachedRevision = FirmwarePackageResolver.GetRevision(Path.GetFileName(download.FilePath)); }
            catch (InvalidDataException) { continue; }
            if (!cachedRevision.Equals(revision, StringComparison.OrdinalIgnoreCase)) continue;
            // Certificate keys change between lookups. Match the offering metadata, not those transient URLs.
            // Conflicting cached content for one offering requires a fresh download.
            if (match is not null && match.Sha256 != download.Sha256) return null;
            match = download with { Firmware = firmware };
        }
        if (match is not null) await WriteReceipt(match, token);
        return match;
    }

    public async Task<FirmwareDownload?> FindValidated(FirmwareRelease firmware, FirmwareRemoteIdentity identity, CancellationToken token)
    {
        await using FileLockLease lease = await Acquire(token);
        if (identity.Length > options.Value.MaximumDownloadBytes) return null;
        foreach (FirmwareDownload download in await GetAllCore(null, token))
        {
            string receipt = Path.Combine(Path.GetDirectoryName(download.FilePath)!, "receipt.json");
            FirmwareCacheEntry? entry = JsonUtil.Deserialize(await fileUtil.Read(receipt, cancellationToken: token), SeaPilotJsonContext.Default.FirmwareCacheEntry);
            if (entry?.RemoteIdentity != identity || download.Length != identity.Length) continue;
            FirmwareDownload result = download with { Firmware = firmware };
            await WriteReceipt(result, token, identity);
            return result;
        }
        return null;
    }

    public async Task<IReadOnlyList<FirmwareDownload>> GetAll(string? serial, CancellationToken cancellationToken)
    {
        await using FileLockLease lease = await Acquire(cancellationToken);
        return await GetAllCore(serial, cancellationToken);
    }

    private async Task<IReadOnlyList<FirmwareDownload>> GetAllCore(string? serial, CancellationToken cancellationToken)
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
        CancellationToken cancellationToken, FirmwareRemoteIdentity? identity = null)
    {
        FirmwareFileStore.ValidateFilename(filename);
        await using FileLockLease lease = await Acquire(cancellationToken);
        FirmwareDownload inspected = await store.Inspect(source, filename, firmware, options.Value.MaximumDownloadBytes, cancellationToken);
        string directory = GetDirectory(filename, inspected.Sha256);
        // Validate/quarantine a prior entry before attempting recovery or publication.
        await Read(Path.Combine(directory, "receipt.json"), cancellationToken);
        string binary = Path.Combine(directory, filename);
        if (await fileUtil.Exists(binary, cancellationToken))
        {
            try
            {
                FirmwareDownload existing = await store.Inspect(binary, filename, firmware, options.Value.MaximumDownloadBytes, cancellationToken);
                if (existing.Sha256 != inspected.Sha256) await Quarantine(binary, cancellationToken);
            }
            catch (InvalidDataException) { await Quarantine(binary, cancellationToken); }
        }
        FirmwareDownload result = await store.Save(source, filename, directory, firmware,
            options.Value.MaximumDownloadBytes, cancellationToken);
        if (result.Sha256 != inspected.Sha256) throw new IOException("Firmware content changed before cache publication.");
        await WriteReceipt(result, cancellationToken, identity is not null && identity.Length == result.Length ? identity : null);
        return result;
    }

    private string GetDirectory(string filename, string digest)
    {
        // Both content and filename are part of the identity; same-name revisions can coexist.
        if (digest.Length != 64 || !digest.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Invalid cache digest.");
        string key =
            Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(filename)));
        return Path.Combine(Root, digest.ToLowerInvariant(), key);
    }

    private async Task WriteReceipt(FirmwareDownload download, CancellationToken cancellationToken, FirmwareRemoteIdentity? identity = null)
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
            RemoteIdentity = identity,
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
            string directory = GetDirectory(entry.Filename, entry.Sha256);
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

