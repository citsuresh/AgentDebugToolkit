using System.IO;
using System.Text.Json;
using AgentDebugToolkit.Identity;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class PackageVerificationTests
{
    [Fact]
    public void CompletePackageAndRelocatedCopy_MatchAllPayloadHashesAndEmbeddedIdentity()
    {
        using var package = new PackageFixture();
        var result = PackageVerifier.Verify(package.Current);
        Assert.Equal("matched", result.Status);
        Assert.Equal(9, result.CheckedFiles);
        Assert.Empty(result.Issues);
        using var copy = new IdentityTestFolder();
        foreach (var file in Directory.EnumerateFiles(package.Root, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(copy.Path, Path.GetRelativePath(package.Root, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        var relocated = package.Current with { AssemblyPath = Path.Combine(copy.Path, "ui", "agentdebug-ui.dll"), ExecutablePath = Path.Combine(copy.Path, "ui", "agentdebug-ui.exe") };
        Assert.Equal("matched", PackageVerifier.Verify(relocated).Status);
    }

    [Theory]
    [InlineData("ui\\agentdebug-ui.dll")]
    [InlineData("vs\\agentdebug-vs.exe")]
    [InlineData("README.txt")]
    public void TamperedPayload_IsExplicitMismatch(string relative)
    {
        using var package = new PackageFixture();
        File.AppendAllText(Path.Combine(package.Root, relative), "changed");
        var result = PackageVerifier.Verify(package.Current);
        Assert.Equal("mismatched", result.Status);
        Assert.Contains(result.Issues, issue => issue.Path == relative && issue.Reason == "hash-mismatch");
    }

    [Fact]
    public void MissingAndUnexpectedFiles_AreBothReported()
    {
        using var package = new PackageFixture();
        File.Delete(Path.Combine(package.Root, "vs", "agentdebug-vs.dll"));
        File.WriteAllText(Path.Combine(package.Root, "extra.dll"), "extra");
        var result = PackageVerifier.Verify(package.Current);
        Assert.Equal("mismatched", result.Status);
        Assert.Contains(result.Issues, issue => issue.Reason == "missing-file");
        Assert.Contains(result.Issues, issue => issue.Reason == "unexpected-file");
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("dirty")]
    [InlineData("configuration")]
    [InlineData("deployment")]
    [InlineData("capabilities")]
    [InlineData("module")]
    [InlineData("path")]
    public void MixedOrStaleRunningIdentity_IsNotAcceptedEvenWhenFilesMatch(string field)
    {
        using var package = new PackageFixture();
        var original = package.Current;
        var different = field switch
        {
            "commit" => original with { Build = original.Build with { SourceCommit = new string('b', 40) } },
            "dirty" => original with { Build = original.Build with { SourceDirty = true } },
            "configuration" => original with { Build = original.Build with { Configuration = "Debug" } },
            "deployment" => original with { Build = original.Build with { DeploymentKind = "managed-source" } },
            "capabilities" => original with { Capabilities = new[] { "not-advertised" } },
            "module" => original with { ModuleVersionId = Guid.NewGuid().ToString() },
            _ => original with { ExecutablePath = Path.Combine(package.Root, "other.exe") }
        };
        Assert.Equal("mismatched", PackageVerifier.Verify(different).Status);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"schemaVersion\":999}")]
    [InlineData("{\"schemaVersion\":1}")]
    public void InvalidManifest_IsNotAbsentOrSuccess(string text)
    {
        using var package = new PackageFixture();
        File.WriteAllText(package.ManifestPath, text);
        var result = PackageVerifier.Verify(package.Current);
        Assert.Equal("mismatched", result.Status);
        Assert.NotEmpty(result.Issues);
    }

    [Theory]
    [InlineData("..\\outside.dll")]
    [InlineData("C:\\outside.dll")]
    [InlineData("README.txt:stream")]
    [InlineData("ui\\..\\README.txt")]
    public void UnsafeManifestPaths_AreRejectedBeforeHashing(string path)
    {
        using var package = new PackageFixture();
        package.WriteManifest(new[] { new { path, sha256 = new string('0', 64) } });
        var result = PackageVerifier.Verify(package.Current);
        Assert.Equal("mismatched", result.Status);
        Assert.Equal(0, result.CheckedFiles);
        Assert.Contains(result.Issues, issue => issue.Reason.Contains("Unsafe package path"));
    }

    [Fact]
    public void NoManifest_DoesNotMeanMatched()
    {
        using var package = new PackageFixture();
        File.Delete(package.ManifestPath);
        var result = PackageVerifier.Verify(package.Current);
        Assert.Equal("not-present", result.Status);
        Assert.Null(result.ManifestPath);
        Assert.Equal(0, result.CheckedFiles);
    }

    private sealed class PackageFixture : IDisposable
    {
        private readonly IdentityTestFolder folder = new();
        internal string Root => folder.Path;
        internal string ManifestPath => Path.Combine(Root, "package-manifest.json");
        internal ExecutableSnapshot Current { get; }
        private readonly List<object> tools = new();

        internal PackageFixture()
        {
            var build = new ToolkitBuildIdentity(new string('a', 40), false, "Release", "win-x64", "portable-package", true);
            ExecutableSnapshot? current = null;
            foreach (var (tool, subdirectory, executable) in new[] { ("ui", "ui", "ui"), ("vs", "vs", "vs"), ("console", "console", "console"), ("server", "mcp", "mcp") })
            {
                Directory.CreateDirectory(Path.Combine(Root, subdirectory));
                var assembly = Path.Combine(subdirectory, $"agentdebug-{executable}.dll");
                var application = Path.Combine(subdirectory, $"agentdebug-{executable}.exe");
                File.WriteAllText(Path.Combine(Root, assembly), tool + " assembly fixture");
                File.WriteAllText(Path.Combine(Root, application), tool + " executable fixture");
                var snapshot = new ExecutableSnapshot(1, tool, "1.0", Guid.NewGuid().ToString(), Path.Combine(Root, application), ToolkitIdentity.Hash(Path.Combine(Root, application)),
                    Path.Combine(Root, assembly), ToolkitIdentity.Hash(Path.Combine(Root, assembly)), "x64", build, new[] { "toolkit.info.v1" });
                if (tool == "ui") current = snapshot;
                tools.Add(new { tool, executablePath = application, assemblyPath = assembly, snapshot.Version, snapshot.ModuleVersionId, snapshot.ContractVersion, snapshot.Capabilities, build });
            }
            Current = current ?? throw new InvalidOperationException("UI identity fixture was not created.");
            File.WriteAllText(Path.Combine(Root, "README.txt"), "fixture documentation");
            WriteManifest();
        }

        internal void WriteManifest(object? files = null)
        {
            files ??= Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Where(file => file != ManifestPath)
                .Select(file => new { path = Path.GetRelativePath(Root, file), sha256 = ToolkitIdentity.Hash(file) }).ToArray();
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(new { schemaVersion = 1, sourceCommit = Current.Build.SourceCommit, sourceDirty = Current.Build.SourceDirty,
                configuration = Current.Build.Configuration, runtimeIdentifier = Current.Build.RuntimeIdentifier, tools, files }, ToolkitIdentity.JsonOptions));
        }

        public void Dispose() => folder.Dispose();
    }
}