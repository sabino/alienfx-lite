using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace AlienFxLite.UI;

internal sealed class InstallerUpdateService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<InstallUpdateResult> DownloadAndLaunchAsync(
        GitHubReleaseUpdateService.GitHubReleaseInfo release,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(release.InstallerUrl))
        {
            throw new InvalidOperationException("No installer is available for the selected release.");
        }

        Uri installerUri = new(release.InstallerUrl, UriKind.Absolute);
        string fileName = Path.GetFileName(installerUri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"AlienFxLite-Setup-win-x64-v{release.Version}.exe";
        }

        string updateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlienFxLite",
            "updates");
        Directory.CreateDirectory(updateDir);

        string targetPath = Path.Combine(updateDir, fileName);
        string tempPath = targetPath + ".download";

        using HttpRequestMessage request = new(HttpMethod.Get, installerUri);
        using HttpResponseMessage response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using (FileStream targetStream = new(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous))
        await using (Stream sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            await sourceStream.CopyToAsync(targetStream, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, targetPath, true);

        Process.Start(new ProcessStartInfo
        {
            FileName = targetPath,
            UseShellExecute = true,
        });

        return new InstallUpdateResult(release.Version, targetPath);
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AlienFxLite", AppVersionInfo.CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return client;
    }

    internal sealed record InstallUpdateResult(string Version, string InstallerPath);
}
