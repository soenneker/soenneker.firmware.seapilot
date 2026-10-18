using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Locks.Files;

namespace Soenneker.Firmware.SeaPilot;

/// <summary>Serializes firmware updates across cooperating processes and cache locations on this machine.</summary>
public sealed class FirmwareUpdateLock
{
    /// <summary>Persistent machine-wide lock file. Its directory requires administrator/root write access.</summary>
    public string FilePath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Soenneker", "SeaPilot", "update.lock")
        : "/var/lib/soenneker/seapilot/update.lock";

    /// <summary>Waits for exclusive update access. Cancellation only affects acquisition; disposing the lease releases access.</summary>
    public ValueTask<FileLockLease> Acquire(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        return new FileLock(FilePath).Acquire(token);
    }
}
