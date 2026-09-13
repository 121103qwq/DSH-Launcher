using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class ModPackMarketServiceTests
{
    [Fact]
    public async Task ReadsSchema2LocalizedFieldsAndKeepsMetadata()
    {
        var payload = CatalogJson(2, new
        {
            id = "owner.repo",
            name = "pack-id",
            displayName = new Dictionary<string, string>
            {
                ["en-US"] = "English",
                ["zh-CN"] = "中文名称"
            },
            description = new Dictionary<string, string>
            {
                ["ja"] = "日本語说明",
                ["en-US"] = "English description"
            },
            author = "author",
            version = "1.2.3",
            type = "profile",
            profileName = "pack-id",
            category = "theme",
            downloadUrl = "https://example.test/pack.dspack",
            sha256 = new string('A', 64),
            size = 123L,
            manifestVersion = 4,
            bundleCount = 2,
            depCount = 1,
            profileCount = 1,
            updatedAt = "2026-09-12T09:19:28Z"
        });
        using var client = new HttpClient(new StaticHandler(_ => JsonResponse(payload)));
        using var service = new ModPackMarketService(client);

        var entries = await service.ReadCatalogAsync();

        var entry = Assert.Single(entries);
        Assert.Equal("中文名称", entry.Name);
        Assert.Equal("English description", entry.Description);
        Assert.Equal("profile", entry.PackageType);
        Assert.Equal(123L, entry.Size);
        Assert.True(entry.IsInstallable);
        Assert.Equal(new string('a', 64), entry.Sha256);
    }

    [Fact]
    public async Task PrefersZhCnThenEnUsThenFirstLocale()
    {
        var payload = CatalogJson(2,
            new
            {
                name = "zh",
                displayName = new Dictionary<string, string>
                {
                    ["en-US"] = "English",
                    ["zh-CN"] = "中文"
                },
                description = new Dictionary<string, string>
                {
                    ["en-US"] = "English",
                    ["zh-CN"] = "中文"
                },
                downloadUrl = "https://example.test/zh.tgz",
                sha256 = new string('b', 64),
                size = 1
            },
            new
            {
                name = "en",
                displayName = new Dictionary<string, string> { ["en-US"] = "English" },
                description = new Dictionary<string, string> { ["en-US"] = "English" },
                downloadUrl = "https://example.test/en.tgz",
                sha256 = new string('c', 64),
                size = 1
            },
            new
            {
                name = "first",
                displayName = new Dictionary<string, string> { ["fr"] = "Premier", ["de"] = "Erste" },
                description = new Dictionary<string, string> { ["fr"] = "Description" },
                downloadUrl = "https://example.test/first.tgz",
                sha256 = new string('d', 64),
                size = 1
            });
        using var client = new HttpClient(new StaticHandler(_ => JsonResponse(payload)));
        using var service = new ModPackMarketService(client);

        var entries = await service.ReadCatalogAsync();

        Assert.Equal(new[] { "中文", "English", "Premier" }, entries.Select(entry => entry.Name));
        Assert.Equal("中文", entries[0].Description);
        Assert.Equal("English", entries[1].Name);
        Assert.Equal("Premier", entries[2].Name);
    }

    [Fact]
    public async Task ReadsLegacySchema1ModpacksArray()
    {
        var bytes = Encoding.UTF8.GetBytes("legacy");
        var payload = CatalogJson(1, new
        {
            name = "legacy-pack",
            displayName = "Legacy pack",
            description = "legacy description",
            author = "legacy author",
            version = "1.0.0",
            downloadUrl = "https://example.test/legacy.tgz",
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            size = bytes.Length
        });
        using var client = new HttpClient(new StaticHandler(_ => JsonResponse(payload)));
        using var service = new ModPackMarketService(client);

        var entry = Assert.Single(await service.ReadCatalogAsync());

        Assert.Equal("Legacy pack", entry.Name);
        Assert.Equal("legacy description", entry.Description);
        Assert.True(entry.IsInstallable);
    }

    [Fact]
    public async Task RetainsEntriesButBlocksMissingHashDshHomeAndPlaceholder()
    {
        var payload = CatalogJson(2,
            Entry("missing-hash", "https://example.test/missing.tgz", null, 10),
            Entry("missing-size", "https://example.test/missing-size.tgz", new string('1', 64), 0),
            Entry("dsh-home", "https://example.test/home.dspack", new string('e', 64), 10, "dshhome"),
            Entry("placeholder", "https://github.com/your-org/repo/releases/download/v1/pack.dspack",
                new string('f', 64), 10));
        using var client = new HttpClient(new StaticHandler(_ => JsonResponse(payload)));
        using var service = new ModPackMarketService(client);

        var entries = await service.ReadCatalogAsync();

        Assert.Equal(4, entries.Count);
        Assert.All(entries, entry => Assert.False(entry.IsInstallable));
        Assert.Contains("SHA", entries[0].UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("大小", entries[1].UnavailableReason);
        Assert.Contains("整机", entries[2].UnavailableReason);
        Assert.Contains("占位", entries[3].UnavailableReason);
    }

    [Fact]
    public async Task DownloadsAndVerifiesArchiveThenCleanupRemovesOnlyOwnedPath()
    {
        var bytes = Encoding.UTF8.GetBytes("valid package bytes");
        var entry = InstallableEntry("pack.dspack", bytes);
        var progress = new ProgressSink();
        using var client = new HttpClient(new StaticHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        }));
        using var service = new ModPackMarketService(client);

        var path = await service.DownloadPackageAsync(entry, progress);

        Assert.True(File.Exists(path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal((long)bytes.Length, progress.Last.BytesDownloaded);
        Assert.Equal(100d, progress.Last.Percent!.Value);

        ModPackMarketService.CleanupDownload(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RejectsSizeMismatchShaMismatchAndHttpFailure()
    {
        var bytes = Encoding.UTF8.GetBytes("package");
        using var sizeClient = new HttpClient(new StaticHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        }));
        using var sizeService = new ModPackMarketService(sizeClient);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            sizeService.DownloadPackageAsync(InstallableEntry("size.tgz", bytes, expectedSize: bytes.Length + 1)));

        using var hashClient = new HttpClient(new StaticHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        }));
        using var hashService = new ModPackMarketService(hashClient);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            hashService.DownloadPackageAsync(InstallableEntry("hash.tgz", bytes, expectedHash: new string('0', 64))));

        using var httpClient = new HttpClient(new StaticHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var httpService = new ModPackMarketService(httpClient);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            httpService.DownloadPackageAsync(InstallableEntry("http.tgz", bytes)));
    }

    [Fact]
    public async Task RejectsResponseThatDeclaresMoreThanDownloadLimit()
    {
        var content = new ByteArrayContent(Array.Empty<byte>());
        content.Headers.ContentLength = 256L * 1024 * 1024 + 1;
        using var client = new HttpClient(new StaticHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        }));
        using var service = new ModPackMarketService(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadPackageAsync(InstallableEntry("oversize.dspack", Array.Empty<byte>(),
                expectedSize: 1)));
    }

    [Fact]
    public async Task CancellationCleansPartFileAndDoesNotLeaveReturnedArchive()
    {
        var bytes = Encoding.UTF8.GetBytes("cancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new HttpClient(new StaticHandler((_, token) =>
            Task.FromCanceled<HttpResponseMessage>(token)));
        using var service = new ModPackMarketService(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DownloadPackageAsync(
                InstallableEntry("cancel.tgz", bytes),
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public void CleanupDownloadIgnoresPathOutsidePrivateRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"dsh-market-outside-{Guid.NewGuid():N}.dspack");
        File.WriteAllText(outside, "keep");
        try
        {
            ModPackMarketService.CleanupDownload(outside);
            Assert.True(File.Exists(outside));
        }
        finally
        {
            if (File.Exists(outside))
            {
                File.Delete(outside);
            }
        }
    }

    private static ModPackMarketEntry InstallableEntry(
        string fileName,
        byte[] bytes,
        string? expectedHash = null,
        int? expectedSize = null)
    {
        return new ModPackMarketEntry
        {
            Name = "pack",
            Description = "description",
            Version = "1.0.0",
            Author = "author",
            DownloadUrl = $"https://example.test/{fileName}",
            Sha256 = expectedHash ?? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Size = expectedSize ?? bytes.Length,
            IsInstallable = true
        };
    }

    private static object Entry(
        string name,
        string downloadUrl,
        string? sha256,
        long size,
        string type = "profile")
    {
        return new
        {
            name,
            displayName = name,
            description = name,
            author = "author",
            version = "1.0.0",
            type,
            downloadUrl,
            sha256,
            size
        };
    }

    private static string CatalogJson(int schemaVersion, params object[] entries) =>
        JsonSerializer.Serialize(new { schemaVersion, modpacks = entries });

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class ProgressSink : IProgress<NodeDownloadProgress>
    {
        public NodeDownloadProgress Last { get; private set; } = new(0, null, null);
        public void Report(NodeDownloadProgress value) => Last = value;
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StaticHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this((request, _) => Task.FromResult(handler(request)))
        {
        }

        public StaticHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }
}
