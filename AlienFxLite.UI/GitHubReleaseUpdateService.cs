using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlienFxLite.UI;

internal sealed class GitHubReleaseUpdateService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private const string LatestReleaseUrl = "https://api.github.com/repos/sabino/alienfx-lite/releases/latest";

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, LatestReleaseUrl);
        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new UpdateCheckResult(
                AppVersionInfo.CurrentVersion,
                null,
                false,
                "No published GitHub release is available yet.",
                null);
        }

        response.EnsureSuccessStatusCode();

        await using Stream jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        GitHubReleaseResponse? release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(jsonStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
        {
            throw new InvalidOperationException("GitHub returned an invalid release response.");
        }

        Version currentVersion = ParseVersion(AppVersionInfo.CurrentVersion);
        Version latestVersion = ParseVersion(release.TagName);
        string? installerUrl = release.Assets?
            .FirstOrDefault(asset => asset.Name.StartsWith("AlienFxLite-Setup-win-x64-", StringComparison.OrdinalIgnoreCase) &&
                                     asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            ?.BrowserDownloadUrl;

        GitHubReleaseInfo releaseInfo = new(
            release.Name ?? release.TagName,
            release.TagName,
            latestVersion.ToString(3),
            release.HtmlUrl,
            installerUrl,
            release.Body ?? string.Empty,
            release.PublishedAt);

        if (latestVersion <= currentVersion)
        {
            return new UpdateCheckResult(
                AppVersionInfo.CurrentVersion,
                releaseInfo,
                false,
                $"AlienFx Lite {AppVersionInfo.CurrentVersion} is up to date.",
                null);
        }

        return new UpdateCheckResult(
            AppVersionInfo.CurrentVersion,
            releaseInfo,
            true,
            $"Version {releaseInfo.Version} is available on GitHub Releases.",
            installerUrl is null
                ? "The latest release exists, but no installer asset was found."
                : null);
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AlienFxLite", AppVersionInfo.CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static Version ParseVersion(string rawVersion)
    {
        string normalized = rawVersion.Trim().TrimStart('v', 'V');
        int separator = normalized.IndexOf('-');
        if (separator >= 0)
        {
            normalized = normalized[..separator];
        }

        if (!Version.TryParse(normalized, out Version? version))
        {
            throw new InvalidOperationException($"Unable to parse version '{rawVersion}'.");
        }

        if (version.Build < 0)
        {
            version = new Version(version.Major, version.Minor, 0);
        }

        return version;
    }

    internal sealed record UpdateCheckResult(
        string CurrentVersion,
        GitHubReleaseInfo? Release,
        bool UpdateAvailable,
        string StatusMessage,
        string? WarningMessage);

    internal sealed record GitHubReleaseInfo(
        string Title,
        string TagName,
        string Version,
        string ReleasePageUrl,
        string? InstallerUrl,
        string Notes,
        DateTimeOffset PublishedAt);

    private sealed record GitHubReleaseResponse(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("published_at")] DateTimeOffset PublishedAt,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAssetResponse>? Assets);

    private sealed record GitHubAssetResponse(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
}
