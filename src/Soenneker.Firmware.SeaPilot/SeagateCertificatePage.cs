using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace Soenneker.Firmware.SeaPilot;

internal static class SeagateCertificatePage
{
    internal static Uri? GetPageUri(Uri download)
    {
        if (!download.Host.Equals("apps1.seagate.com", StringComparison.OrdinalIgnoreCase) ||
            download.AbsolutePath != "/downloads/certificate.html") return null;
        Match key = Regex.Match(download.Query, @"(?:[?&])key=([0-9]+)(?:&|$)");
        return key.Success ? new Uri($"https://apps1.seagate.com/downloads/certificate.html?key={key.Groups[1].Value}") : null;
    }

    internal static Task<string?> ReadRevision(IPage page, Uri certificate) => page.EvaluateAsync<string?>("""
        key => {
            const matches = [];
            for (const table of document.querySelectorAll('#manageCertificateDownloadForm table')) {
                const headers = [...table.querySelectorAll('thead th')];
                const index = headers.findIndex(h => h.textContent.trim().toLowerCase() === 'version');
                if (index < 0) continue;
                for (const row of table.querySelectorAll('tbody tr')) {
                    const links = [...row.querySelectorAll('a[href]')];
                    if (!links.some(a => {
                        const href = a.getAttribute('href');
                        return /[?&]action=performDownload(?:&|$)/.test(href) &&
                            href.match(/[?&]key=([0-9]+)(?=[&'"\)]|$)/)?.[1] === key;
                    })) continue;
                    const revision = row.cells[index]?.textContent.trim();
                    if (!revision || !/^[a-z]{1,4}[0-9]{2,8}$/i.test(revision)) return null;
                    matches.push(revision.toUpperCase());
                }
            }
            return matches.length === 1 ? matches[0] : null;
        }
        """, certificate.Query.Substring(5));
}
