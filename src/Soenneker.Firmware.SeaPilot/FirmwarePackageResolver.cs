using System;
using Soenneker.Locks.Files;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Soenneker.Compression.Zip.Abstract;
using Soenneker.Compression.Zip.Options;
using Soenneker.Firmware.SeaPilot.Dtos;
using Soenneker.Firmware.SeaPilot.Options;
using Soenneker.OpenSeaChest.Util.Dtos;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;

namespace Soenneker.Firmware.SeaPilot;

/// <summary>Resolves the primary firmware image in a serial-specific Seagate ZIP offering.</summary>
public sealed class FirmwarePackageResolver(IZipUtil zipUtil, IFileUtil files, IDirectoryUtil directories,
    FirmwareFileStore store, IOptions<SeaPilotOptions> options)
{
    public async Task<FirmwareImageSelection> Prepare(FirmwareDownload package, CancellationToken token)
    {
        if (!Path.GetExtension(package.FilePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Automatic updates require a Seagate ZIP package; executable installers are not supported.");
        await directories.Create(options.Value.CacheDirectory, cancellationToken: token);
        using FileLockLease lease = await new FileLock(Path.Combine(options.Value.CacheDirectory, "cache.lock")).Acquire(token);
        string entry;
        string revision;
        await using (FileStream input = files.OpenRead(package.FilePath))
        using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
        {
            entry = SelectPrimaryImage(archive.Entries.Select(e => e.FullName).ToArray());
            revision = GetRevision(entry);
            if (!revision.Equals(GetRevision(package.FilePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Package and primary image revisions disagree.");
        }
        string temporary = await directories.CreateTempDirectory(cancellationToken: token);
        try
        {
            await zipUtil.Extract(package.FilePath, temporary, new ZipExtractionOptions
            {
                MaximumEntryCount = 10000,
                MaximumEntrySize = options.Value.MaximumDownloadBytes,
                MaximumTotalUncompressedSize = checked(options.Value.MaximumDownloadBytes * 4)
            }, token);
            string source = Path.Combine(temporary, entry.Replace('/', Path.DirectorySeparatorChar));
            await store.ValidateRawImage(source, token);
            FirmwareDownload image = await store.Save(source, Path.GetFileName(source),
                Path.Combine(options.Value.CacheDirectory, "images", package.Sha256), package.Firmware,
                options.Value.MaximumDownloadBytes, token);
            return new FirmwareImageSelection { FilePath = image.FilePath, TargetRevision = revision, Sha256 = image.Sha256 };
        }
        finally { await directories.Delete(temporary, cancellationToken: CancellationToken.None); }
    }

    public static string SelectPrimaryImage(string[] entries)
    {
        // Seagate's current image sits directly in firmware/. Nested previous/subrelease images are not upgrade candidates.
        string[] images = entries.Where(path => path.EndsWith(".lod", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).Where(path =>
            !path.Contains('\\') && (path.Split('/') is [_, _] parts && parts[0].Equals("firmware", StringComparison.OrdinalIgnoreCase) ||
                                    !path.Contains('/'))).ToArray();
        return images.Length == 1 ? images[0] : throw new InvalidDataException("Package must contain exactly one primary raw image at its root or directly in firmware/. Nested previous/subrelease images are excluded.");
    }

    public static string GetRevision(string filename)
    {
        string name = Path.GetFileNameWithoutExtension(filename);
        Match match = Regex.Match(name, @"-([A-Za-z]{1,4}[0-9]{2,8})$", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() :
            throw new InvalidDataException("The package does not identify a supported firmware revision in its filename.");
    }

    public static bool NeedsUpdate(string installed, string target)
    {
        if (installed.Equals(target, StringComparison.OrdinalIgnoreCase)) return false;
        Match oldVersion = Regex.Match(installed, @"^([A-Za-z]{1,4})([0-9]{2,8})$", RegexOptions.CultureInvariant);
        Match newVersion = Regex.Match(target, @"^([A-Za-z]{1,4})([0-9]{2,8})$", RegexOptions.CultureInvariant);
        if (!oldVersion.Success || !newVersion.Success ||
            !oldVersion.Groups[1].Value.Equals(newVersion.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cannot establish an automatic upgrade path between these firmware branches.");
        return long.Parse(newVersion.Groups[2].Value) > long.Parse(oldVersion.Groups[2].Value);
    }

    public static void ValidateSectorSize(OpenSeaChestDrive drive, string image)
    {
        string name = Path.GetFileName(image).ToUpperInvariant();
        uint? required = name.Contains("-512E-") || name.Contains("-512N-") || name.Contains("-5XXE-") ? 512u :
            name.Contains("-4KN-") ? 4096u : null;
        if (required is not null && drive.LogicalBlockSizeBytes != required)
            throw new InvalidDataException("Drive logical sector size is unknown or does not match the firmware image.");
    }
}

