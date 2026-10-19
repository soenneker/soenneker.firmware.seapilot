using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Firmware.SeaPilot.Dtos;
using Soenneker.OpenSeaChest.Util.Dtos;
using Soenneker.Tests.HostedUnit;

namespace Soenneker.Firmware.SeaPilot.Tests;

// Native adapter-boundary tests: real preparation, cache, file validation and writer;
// delegates stand in for hardware discovery and firmware writes, never DI application services.
[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class FirmwareUpdateWriterTests(Host host) : HostedUnitTest(host)
{
    private async Task<FirmwareUpdateResult> Prepare(string serial)
    {
        string source = Path.Combine(Path.GetTempPath(), $"seapilot-{Guid.NewGuid()}.zip");
        try
        {
            await using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
            await using (Stream stream = archive.CreateEntry("firmware/test-SN06.lod").Open()) stream.Write([1, 2, 3, 4, 5]);
            var release = new FirmwareRelease { SerialNumber = serial, Title = "Test", DownloadUri = new Uri("https://apps1.seagate.com/test-SN06.zip") };
            FirmwareDownload package = await Resolve<FirmwareCache>(true).Store(source, "test-SN06.zip", release, default);
            FirmwareImageSelection image = await Resolve<FirmwarePackageResolver>(true).Prepare(package, default);
            return new FirmwareUpdateResult { Drive = Drive(serial, "SN04"), Package = package, Image = image };
        }
        finally { File.Delete(source); }
    }

    private static OpenSeaChestDrive Drive(string serial, string revision, string? handle = null) => new()
    {
        Handle = handle ?? serial, SerialNumber = serial, ModelNumber = "ST18000NM000J", FirmwareRevision = revision,
        Details = new Dictionary<string, string>(), RawDeviceInfo = []
    };

    [Test]
    public async Task NativeFailureContinuesAndSuccessfulWriteIsVerified()
    {
        FirmwareUpdateResult first = await Prepare("WRITERFIRST");
        FirmwareUpdateResult second = await Prepare("WRITERSECOND");
        var prepared = new List<FirmwareUpdateResult> { first, second,
            new() { Drive = Drive("SKIP", "SN04"), SkipReason = "No offering" },
            new() { Drive = Drive("PREPFAIL", "SN04"), Error = new InvalidDataException("Bad package") } };
        var writes = 0;
        var active = false;
        var result = await Resolve<FirmwareUpdateWriter>(true).WriteAll(prepared,
            _ => Task.FromResult(new List<OpenSeaChestDrive> { first.Drive, active ? Drive("WRITERSECOND", "SN06", "NEW-HANDLE") : second.Drive }),
            (device, _, token) =>
            {
                if (token.CanBeCanceled) throw new Exception("Native writes must not be cancelled.");
                writes++;
                if (device == first.Drive.Handle) throw new IOException("Native failure");
                active = true;
                return Task.FromResult(new List<string> { "Completed" });
            }, default);
        if (writes != 2 || !result[0].FlashAttempted || result[0].WriteCompleted || result[0].Error is null ||
            !result[1].WriteCompleted || result[1].Verification?.IsActive != true || result[1].VerificationError is not null ||
            result[2].FlashAttempted || result[3].FlashAttempted)
            throw new Exception("Incorrect failure continuation or activation outcome.");
    }

    [Test]
    public async Task CancellationWaitsForActiveWriteAndPreventsNextDrive()
    {
        var results = new List<FirmwareUpdateResult> { await Prepare("CANCELFIRST"), await Prepare("CANCELSECOND") };
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        Task pending = Resolve<FirmwareUpdateWriter>(true).WriteAll(results,
            _ => Task.FromResult(new List<OpenSeaChestDrive> { results[0].Drive, results[1].Drive }),
            (_, _, token) => { if (token.CanBeCanceled) throw new Exception("Write token was cancellable."); writes++; entered.SetResult(); return finish.Task; }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        if (pending.IsCompleted) throw new Exception("Active write was abandoned.");
        finish.SetResult(["Completed; power cycle required"]);
        try { await pending; throw new Exception("Cancellation was not observed."); }
        catch (OperationCanceledException) { }
        if (writes != 1 || !results[0].WriteCompleted || results[0].Verification?.IsActive != false || results[1].FlashAttempted)
            throw new Exception("Cancellation lost write completion or started another drive.");
    }

    [Test]
    public async Task ChangedImageAndDevicePreventNativeWrites()
    {
        FirmwareUpdateResult prepared = await Prepare("CHANGED");
        var writes = 0;
        foreach (bool changeImage in new[] { true, false })
        {
            var result = await Resolve<FirmwareUpdateWriter>(true).WriteAll(
                [changeImage ? prepared with { Image = prepared.Image! with { Sha256 = new string('0', 64) } } : prepared],
                _ => Task.FromResult(new List<OpenSeaChestDrive> { Drive("REPLACEMENT", "SN04", prepared.Drive.Handle) }),
                (_, _, _) => { writes++; return Task.FromResult(new List<string>()); }, default);
            if (result[0].Error is null || result[0].FlashAttempted) throw new Exception("Changed identity was accepted.");
        }
        if (writes != 0) throw new Exception("An unsafe write started.");
    }

    [Test]
    public async Task VerificationFailurePreservesSuccessfulWrite()
    {
        FirmwareUpdateResult prepared = await Prepare("MISSINGAFTERWRITE");
        var written = false;
        var results = await Resolve<FirmwareUpdateWriter>(true).WriteAll([prepared],
            _ => Task.FromResult(written ? new List<OpenSeaChestDrive>() : new List<OpenSeaChestDrive> { prepared.Drive }),
            (_, _, _) => { written = true; return Task.FromResult(new List<string> { "Completed" }); }, default);
        if (!results[0].WriteCompleted || results[0].Error is not null || results[0].VerificationError is null || results[0].NativeOutput is null)
            throw new Exception("Write and verification outcomes were conflated.");
    }

    [Test]
    public void FollowUpVerificationUsesSerialAndRejectsConflictingIdentity()
    {
        if (!FirmwareUpdateWriter.Verify("SERIAL", "SN06", [Drive("SERIAL", "SN06", "NEW")]).IsActive)
            throw new Exception("A changed handle prevented verification.");
        try { FirmwareUpdateWriter.Verify("SERIAL", "SN06", [Drive("SERIAL", "SN06"), Drive("SERIAL", "SN04")]); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Conflicting revisions were accepted.");
    }
}
