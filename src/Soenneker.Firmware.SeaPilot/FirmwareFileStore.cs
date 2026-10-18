using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Firmware.SeaPilot.Dtos;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;

namespace Soenneker.Firmware.SeaPilot;

/// <summary>Validates and publishes firmware packages using the shared filesystem utilities.</summary>
public sealed class FirmwareFileStore(IFileUtil fileUtil, IDirectoryUtil directoryUtil)
{
    public async Task ValidateRawImage(string path, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".lod", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".bin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Select an extracted .lod or .bin firmware image, not a package or installer.");
        await using FileStream input = fileUtil.OpenRead(path);
        var header = new byte[4];
        int count = await input.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, cancellationToken);
        if (count < 4 || (header[0] == 'M' && header[1] == 'Z') || (header[0] == 'P' && header[1] == 'K') ||
            (header[0] == 0x7f && header[1] == 'E' && header[2] == 'L' && header[3] == 'F'))
            throw new InvalidDataException("The selected file is empty, truncated, an archive or an executable.");
    }

    public async Task<FirmwareDownload> Inspect(string source, string filename, FirmwareRelease firmware,
        long maximumBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFilename(filename);
        long length = await fileUtil.GetSize(source, cancellationToken) ??
                      throw new FileNotFoundException("Firmware package is missing.", source);
        if (length == 0 || length > maximumBytes)
            throw new InvalidDataException("The firmware package is empty or exceeds the configured download limit.");
        await using FileStream input = fileUtil.OpenRead(source);
        var header = new byte[512];
        int count = await input.ReadAsync(header, cancellationToken);
        string text = Encoding.UTF8.GetString(header, 0, count).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (text.StartsWith('<') || text.StartsWith('{') || text.StartsWith('['))
            throw new InvalidDataException("Seagate returned a web/error document instead of a firmware package.");
        if (Path.GetExtension(filename).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            if (count < 4 || header[0] != 'P' || header[1] != 'K' || header[2] != 3 || header[3] != 4)
                throw new InvalidDataException("Seagate returned an invalid ZIP header.");
            input.Position = 0;
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count == 0)
                throw new InvalidDataException("Seagate returned an empty firmware archive.");
        }

        input.Position = 0;
        string digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
        return new FirmwareDownload { Firmware = firmware, FilePath = source, Length = length, Sha256 = digest };
    }

    public async Task<FirmwareDownload> Save(string source, string filename, string directory,
        FirmwareRelease firmware, long maximumBytes, CancellationToken cancellationToken)
    {
        FirmwareDownload original = await Inspect(source, filename, firmware, maximumBytes, cancellationToken);
        string destination = Path.Combine(directory, filename);
        if (await fileUtil.Exists(destination, cancellationToken))
        {
            try
            {
                FirmwareDownload existing =
                    await Inspect(destination, filename, firmware, maximumBytes, cancellationToken);
                if (existing.Sha256 == original.Sha256)
                    return existing;
            }
            catch (InvalidDataException exception)
            {
                throw new IOException("The destination contains a different file; it will not be overwritten.",
                    exception);
            }

            throw new IOException("The destination contains a different file; it will not be overwritten.");
        }

        await directoryUtil.Create(directory, cancellationToken: cancellationToken);
        string temporary = Path.Combine(directory, $".{Guid.NewGuid()}.partial");
        try
        {
            await fileUtil.Copy(source, temporary, cancellationToken: cancellationToken);
            FirmwareDownload copied = await Inspect(temporary, filename, firmware, maximumBytes, cancellationToken);
            if (copied.Sha256 != original.Sha256)
                throw new IOException("Firmware file changed while it was being copied.");
            cancellationToken.ThrowIfCancellationRequested();
            // IFileUtil.Move overwrites. The final atomic publication must reject a concurrent destination creation.
            File.Move(temporary, destination, overwrite: false);
            return copied with { FilePath = destination };
        }
        finally
        {
            await fileUtil.Delete(temporary, cancellationToken: CancellationToken.None);
        }
    }

    public static void ValidateFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename is "." or ".." ||
            filename.Equals("receipt.json", StringComparison.OrdinalIgnoreCase) ||
            filename.IndexOfAny(['/', '\\', ':']) >= 0 || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Seagate supplied an unsafe download filename.");
    }
}
