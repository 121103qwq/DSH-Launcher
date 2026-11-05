using DshLauncher.Models;
using DshLauncher.Services;
using Xunit;

namespace DshLauncher.UnitTests;

public sealed class DshNodeEngineInferenceTests
{
    private static string CreatePackage(string root, string? engines, string binJs)
    {
        var package = Path.Combine(root, "node_modules", "@deepseek-ai", "dsh");
        Directory.CreateDirectory(Path.Combine(package, "lib"));
        File.WriteAllText(Path.Combine(package, "package.json"),
            "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.2.1-rc.5\"" + (engines is null ? "" : $",\"engines\":{{\"node\":\"{engines}\"}}") + "}");
        File.WriteAllText(Path.Combine(package, "lib", "bin.js"), binJs);
        return package;
    }

    [Fact]
    public void UndeclaredEngineIsInferredFromImportMetaMainEntry()
    {
        using var temporary = new TestDirectory();
        var package = CreatePackage(temporary.Path, null, "if (import.meta.main) await runCli();");
        Assert.Equal(DshRuntimeDetector.ImportMetaMainNodeEngine, DshRuntimeDetector.TryReadNodeEngine(package));
    }

    [Fact]
    public void DeclaredEngineWins()
    {
        using var temporary = new TestDirectory();
        var package = CreatePackage(temporary.Path, ">=24", "if (import.meta.main) await runCli();");
        Assert.Equal(">=24", DshRuntimeDetector.TryReadNodeEngine(package));
    }

    [Fact]
    public void EntryWithoutImportMetaMainHasNoInferredEngine()
    {
        using var temporary = new TestDirectory();
        var package = CreatePackage(temporary.Path, null, "await runCli();");
        Assert.Null(DshRuntimeDetector.TryReadNodeEngine(package));
    }

    [Theory]
    [InlineData("22.11.0", NodeRuntimeCompatibility.Incompatible)]
    [InlineData("22.18.0", NodeRuntimeCompatibility.Compatible)]
    [InlineData("22.23.2", NodeRuntimeCompatibility.Compatible)]
    [InlineData("23.11.0", NodeRuntimeCompatibility.Incompatible)]
    [InlineData("24.1.0", NodeRuntimeCompatibility.Incompatible)]
    [InlineData("24.2.0", NodeRuntimeCompatibility.Compatible)]
    [InlineData("24.21.0", NodeRuntimeCompatibility.Compatible)]
    public void ImportMetaMainFloorMatchesNodeReleases(string version, NodeRuntimeCompatibility expected) =>
        Assert.Equal(expected, NodeRuntimeInfo.EvaluateCompatibility(version, DshRuntimeDetector.ImportMetaMainNodeEngine));

    [Fact]
    public void PostInstallMessageExplainsOldNode()
    {
        using var temporary = new TestDirectory();
        CreatePackage(temporary.Path, null, "if (import.meta.main) await runCli();");
        var message = MainWindow.DescribeMissingDshAfterInstall(
            new NodeRuntimeInfo(true, "node.exe", "22.11.0", null), temporary.Path);
        Assert.Contains("过旧", message);
        Assert.Contains("v22.11.0", message);
    }
}
