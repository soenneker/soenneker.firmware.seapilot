using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Firmware.SeaPilot.Abstract;
using Soenneker.Firmware.SeaPilot.Dtos;
using Soenneker.OpenSeaChest.Util.Dtos;
using Soenneker.Tests.HostedUnit;

namespace Soenneker.Firmware.SeaPilot.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class FirmwareUpdateTests(Host host) : HostedUnitTest(host)
{
    private static OpenSeaChestDrive Drive(string serial = "EXAMPLE", string model = "ST18000NM000J", string revision = "SN04", Exception? discoveryError = null) => new()
    {
        Handle = "PD0", SerialNumber = serial, ModelNumber = model, FirmwareRevision = revision,
        DiscoveryError = discoveryError, Details = new Dictionary<string, string>(), RawDeviceInfo = []
    };

    [Test]
    public void SameModelDoesNotAuthorizeAnotherSerialsOffering()
    {
        var release = new FirmwareRelease { SerialNumber = "FIRST", Title = "SN06", DownloadUri = new Uri("https://www.seagate.com/first-SN06.zip") };
        var lookup = new FirmwareLookup { SerialNumber = "FIRST", ModelNumber = "ST18000NM000J", IsRecognized = true, Releases = [release] };
        SeaPilot.ValidateLookupIdentity(Drive("FIRST"), lookup);
        foreach (FirmwareLookup wrong in new[] { lookup, lookup with { SerialNumber = "SECOND" } })
        {
            try { SeaPilot.ValidateLookupIdentity(Drive("SECOND"), wrong); }
            catch (InvalidDataException) { continue; }
            throw new Exception("A different serial's lookup or release was accepted for the same model.");
        }
    }

    [Test]
    public void SameModelCanHaveDifferentOrNoFirmwareOffers()
    {
        var first = new FirmwareLookup { SerialNumber = "FIRST", ModelNumber = "ST18000NM000J", IsRecognized = true,
            Releases = [new FirmwareRelease { SerialNumber = "FIRST", Title = "SN06", DownloadUri = new Uri("https://www.seagate.com/first-SN06.zip") }] };
        var second = first with { SerialNumber = "SECOND", Releases = [new FirmwareRelease { SerialNumber = "SECOND", Title = "SN04",
            DownloadUri = new Uri("https://www.seagate.com/second-SN04.zip") }] };
        SeaPilot.ValidateLookupIdentity(Drive("FIRST"), first);
        SeaPilot.ValidateLookupIdentity(Drive("SECOND"), second);
        SeaPilot.ValidateLookupIdentity(Drive("SECOND"), second with { Releases = [] });
        if (!FirmwarePackageResolver.NeedsUpdate("SN04", "SN06") || FirmwarePackageResolver.NeedsUpdate("SN04", "SN04"))
            throw new Exception("Each drive must compare its installed revision to its own offered revision.");
    }

    [Test]
    public async Task CancellationPreventsDiscoveryAndSelection()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        try
        {
            await Resolve<ISeaPilot>(true).UpdateAllDriveFirmwares(cancellation.Token);
        }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation before native discovery.");
    }

    [Test]
    public void ChangedDeviceCannotBeFlashed()
    {
        OpenSeaChestDrive original = Drive();
        SeaPilot.VerifyDeviceIdentity(original, [Drive()]);
        foreach (OpenSeaChestDrive[] current in new[] { Array.Empty<OpenSeaChestDrive>(), new[] { Drive("REPLACEMENT") },
                     new[] { Drive(model: "ST28000NM000C") }, new[] { Drive(revision: "SN06") } })
        {
            try { SeaPilot.VerifyDeviceIdentity(original, current); }
            catch (InvalidOperationException) { continue; }
            throw new Exception("Changed or missing device was accepted.");
        }
    }

    [Test]
    public void UnsupportedAndIncompleteDrivesAreExcluded()
    {
        if (!SeaPilot.IsSeagateDrive(Drive()) || SeaPilot.IsSeagateDrive(Drive(model: "WD_BLACK")) ||
            SeaPilot.IsSeagateDrive(Drive(serial: "")) || SeaPilot.IsSeagateDrive(Drive(revision: "")))
            throw new Exception("Incorrect drive eligibility.");
    }

    [Test]
    public async Task RejectsInstallersAndArchivesDisguisedAsImages()
    {
        var store = Resolve<FirmwareFileStore>(true);
        string path = Path.Combine(Path.GetTempPath(), $"seapilot-image-{Guid.NewGuid()}.bin");
        try
        {
            foreach (byte[] header in new byte[][] { [77, 90, 0, 0], [80, 75, 3, 4], [127, 69, 76, 70], [1] })
            {
                await File.WriteAllBytesAsync(path, header);
                try { await store.ValidateRawImage(path, CancellationToken.None); }
                catch (InvalidDataException) { continue; }
                throw new Exception("Non-image was accepted.");
            }
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            await store.ValidateRawImage(path, CancellationToken.None);
        }
        finally { File.Delete(path); }
    }
    [Test]
    public void DiscoveryFailureCannotAuthorizeFirmwareOperations()
    {
        OpenSeaChestDrive unavailable = Drive(discoveryError: new InvalidOperationException("Device unavailable"));
        if (SeaPilot.IsSeagateDrive(unavailable)) throw new Exception("Discovery error passed eligibility.");
        var identityRejected = false;
        try { SeaPilot.VerifyDeviceIdentity(Drive(), [unavailable]); }
        catch (InvalidOperationException) { identityRejected = true; }
        if (!identityRejected) throw new Exception("Discovery error passed pre-write identity check.");
        try { FirmwareUpdateWriter.Verify("EXAMPLE", "SN04", [unavailable]); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Discovery error passed active revision verification.");
    }

}

