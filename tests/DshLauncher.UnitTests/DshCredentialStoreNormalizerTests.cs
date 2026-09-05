using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class DshCredentialStoreNormalizerTests
{
    [Fact]
    public void LegacyVersionAndRefsEnvelopeIsFlattened()
    {
        const string legacy = "version: 1\r\nrefs:\r\n  DEEPSEEK_API_KEY: test-value\r\n  OPENAI_API_KEY: \"quoted:value\"\r\n";

        var normalized = DshCredentialStoreNormalizer.NormalizeForCurrentDsh(legacy);

        Assert.Equal(
            "DEEPSEEK_API_KEY: test-value\r\nOPENAI_API_KEY: \"quoted:value\"\r\n",
            normalized);
    }

    [Fact]
    public void CurrentAndAmbiguousDocumentsAreLeftUnchanged()
    {
        const string current = "DEEPSEEK_API_KEY: test-value\n";
        const string ambiguous = "version: 1\nrefs:\n  KEY: value\nother: keep\n";

        Assert.Same(current, DshCredentialStoreNormalizer.NormalizeForCurrentDsh(current));
        Assert.Same(ambiguous, DshCredentialStoreNormalizer.NormalizeForCurrentDsh(ambiguous));
    }

    [Fact]
    public async Task FreshImportNormalizesLegacyCredentialStore()
    {
        var root = CreateTemporaryDirectory();
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(
            Path.Combine(source, ".credentials.yaml"),
            "version: 1\nrefs:\n  TEST_API_KEY: test-value\n");

        try
        {
            var result = await new DshHomeImportService().ImportAsync(source, destination);

            Assert.True(result.Imported);
            Assert.Equal(
                "TEST_API_KEY: test-value\n",
                await File.ReadAllTextAsync(Path.Combine(destination, ".credentials.yaml")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshImportNormalizesLegacyCredentialStore()
    {
        var root = CreateTemporaryDirectory();
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(
            Path.Combine(source, ".credentials.yaml"),
            "version: 1\nrefs:\n  TEST_API_KEY: refreshed-value\n");

        try
        {
            var result = await new DshHomeImportService().RefreshImportAsync(source, destination);

            Assert.True(result.Imported);
            Assert.Equal(
                "TEST_API_KEY: refreshed-value\n",
                await File.ReadAllTextAsync(Path.Combine(destination, ".credentials.yaml")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dsh-credential-normalizer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
