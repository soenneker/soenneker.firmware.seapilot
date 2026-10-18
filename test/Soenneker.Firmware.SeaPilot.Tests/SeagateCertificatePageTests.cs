using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace Soenneker.Firmware.SeaPilot.Tests;

public sealed class SeagateCertificatePageTests
{
    [Test]
    public async Task ReadsOnlyExactOfferingVersionWithoutDownloading()
    {
        var uri = new Uri("https://apps1.seagate.com/downloads/certificate.html?action=performDownload&key=123");
        Uri certificate = SeagateCertificatePage.GetPageUri(uri)!;
        if (certificate.Query != "?key=123") throw new Exception("Download action was retained.");
        using IPlaywright playwright = await Playwright.CreateAsync();
        Skip.Unless(File.Exists(playwright.Chromium.ExecutablePath), "Install Chromium for certificate parsing tests.");
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        IPage page = await browser.NewPageAsync();
        bool downloaded = false;
        page.Download += (_, _) => downloaded = true;
        await page.SetContentAsync("""
            <form id="manageCertificateDownloadForm"><table>
            <thead><tr><th>Name</th><th>Version</th><th>Actions</th></tr></thead><tbody>
            <tr><td>Other</td><td>SN05</td><td><a href="?action=performDownload&amp;key=1234">Download</a></td></tr>
            <tr><td>Firmware</td><td>SN04</td><td><a href="javascript:popUp('0.6','0.6','','/downloads/certificate.html;jsessionid=test?action=performDownload&amp;key=123');">Download</a></td></tr>
            </tbody></table></form><footer>Version 2.10.0</footer>
            """);
        string? revision = await SeagateCertificatePage.ReadRevision(page, certificate);
        if (revision != "SN04" || downloaded) throw new Exception("Incorrect page metadata or unexpected download.");
        if (FirmwareDownloadEligibility.GetSkipReason($"firmware-{revision}.zip", "SN04") is null)
            throw new Exception("Same revision was not skipped.");
        await page.EvaluateAsync("document.querySelector('tbody').appendChild(document.querySelector('tbody tr:last-child').cloneNode(true))");
        if (await SeagateCertificatePage.ReadRevision(page, certificate) is not null)
            throw new Exception("Ambiguous offering accepted.");
    }
}
