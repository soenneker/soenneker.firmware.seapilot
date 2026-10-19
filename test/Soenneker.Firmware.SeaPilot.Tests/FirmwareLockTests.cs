using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Soenneker.Firmware.SeaPilot.Options;
using Soenneker.Firmware.SeaPilot.Abstract;
using Soenneker.Locks.Files;
using Soenneker.Tests.HostedUnit;

namespace Soenneker.Firmware.SeaPilot.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class FirmwareLockTests(Host host) : HostedUnitTest(host)
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task OperationsWaitForAnotherProcessAndCancellationDoesNotReleaseItsLock(bool update, bool terminateOwner)
    {
        string cacheDirectory = Resolve<IOptions<SeaPilotOptions>>(true).Value.CacheDirectory;
        string lockPath = update ? Resolve<FirmwareUpdateLock>(true).FilePath : Path.Combine(cacheDirectory, "cache.lock");
        try { Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!); }
        catch (UnauthorizedAccessException) { Skip.Unless(false, "Machine-wide update lock requires administrator/root access."); }
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = output.Parent!.Name;
        string tests = output.Parent.Parent!.Parent!.Parent!.FullName;
        string probe = Path.Combine(tests, "Soenneker.Firmware.SeaPilot.LockProbe", "bin", configuration, "net10.0", "Soenneker.Firmware.SeaPilot.LockProbe.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add(lockPath);
        using Process process = Process.Start(start)!;
        try
        {
            string? ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (ready != "LOCKED") throw new Exception("Lock probe failed: " + await process.StandardError.ReadToEndAsync());
            var cache = Resolve<FirmwareCache>(true);
            using var cancellation = new CancellationTokenSource();
            Task pending = update ? Resolve<ISeaPilot>(true).UpdateAllDriveFirmwares(cancellation.Token).AsTask() : cache.GetAll("LOCKTEST", cancellation.Token);
            if (pending.IsCompleted) throw new Exception("Cache ignored another process's lock.");
            await cancellation.CancelAsync();
            try { await pending; throw new Exception("Lock wait ignored cancellation."); }
            catch (OperationCanceledException) { }
            using var secondCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task second = update ? WaitForUpdateLock(secondCancellation.Token) : cache.GetAll("LOCKTEST", secondCancellation.Token);
            if (second.IsCompleted) throw new Exception("Cancellation released another process's lease.");
            if (terminateOwner) process.Kill(entireProcessTree: true);
            else
            {
                await process.StandardInput.WriteLineAsync("release");
                await process.StandardInput.FlushAsync();
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await second;
            if (!terminateOwner && process.ExitCode != 0) throw new Exception("Lock probe failed.");
            if (!File.Exists(lockPath)) throw new Exception("Lock file was deleted.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
    [Test]
    public async Task CancellationReleasesOwnedLeaseAfterOperationUnwinds()
    {
        string path = Path.Combine(Path.GetTempPath(), $"seapilot-lock-{Guid.NewGuid()}");
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Operation()
        {
            await using FileLockLease lease = await new FileLock(path).Acquire(cancellation.Token);
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, cancellation.Token);
        }
        try
        {
            Task operation = Operation();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancellation.CancelAsync();
            try { await operation; throw new Exception("Operation ignored cancellation."); }
            catch (OperationCanceledException) { }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using FileLockLease next = await new FileLock(path).Acquire(deadline.Token);
            if (!File.Exists(path)) throw new Exception("Lock file must persist across leases.");
        }
        finally { File.Delete(path); }
    }

    private async Task WaitForUpdateLock(CancellationToken token)
    {
        await using FileLockLease lease = await Resolve<FirmwareUpdateLock>(true).Acquire(token);
    }

}
