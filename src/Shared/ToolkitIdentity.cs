using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentDebugToolkit.Identity;

internal sealed record ToolkitBuildIdentity(string? SourceCommit, bool? SourceDirty, string Configuration,
    string? RuntimeIdentifier, string DeploymentKind, bool? SelfContained);

internal sealed record ExecutableSnapshot(int ContractVersion, string Tool, string Version, string ModuleVersionId,
    string ExecutablePath, string ExecutableSha256, string AssemblyPath, string AssemblySha256,
    string Architecture, ToolkitBuildIdentity Build, string[] Capabilities);

internal sealed record PackageIssue(string Path, string Reason);
internal sealed record PackageVerification(string Status, string? ManifestPath, int CheckedFiles, IReadOnlyList<PackageIssue> Issues);

internal static class ToolkitIdentity
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !args[0].Equals("info", StringComparison.Ordinal)) return false;
        var verify = false;
        for (var i = 1; i < args.Length; i++)
        {
            var argument = args[i];
            string value;
            if (argument.Equals("--verifyPackage", StringComparison.OrdinalIgnoreCase))
                value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            else if (argument.StartsWith("--verifyPackage=", StringComparison.OrdinalIgnoreCase))
                value = argument[(argument.IndexOf('=') + 1)..];
            else
            {
                Console.WriteLine(JsonSerializer.Serialize(new { success = false, error = "invalid-argument", message = $"Unknown info argument '{argument}'. Expected info [--verifyPackage [true|false]]." }));
                exitCode = 1;
                return true;
            }
            if (!bool.TryParse(value, out verify))
            {
                Console.WriteLine(JsonSerializer.Serialize(new { success = false, error = "invalid-argument", message = "--verifyPackage must be true or false." }));
                exitCode = 1;
                return true;
            }
        }
        var result = CreateResult(verify);
        Console.WriteLine(result.Json.GetRawText());
        exitCode = result.ExitCode;
        return true;
    }

    internal static (int ExitCode, JsonElement Json) CreateResult(bool verifyPackage)
    {
        try
        {
            var snapshot = Read();
            var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(snapshot, JsonOptions))!;
            var verification = verifyPackage ? PackageVerifier.Verify(snapshot) : null;
            var success = verification?.Status != "mismatched";
            payload["success"] = JsonSerializer.SerializeToElement(success);
            if (verification is not null) payload["packageVerification"] = JsonSerializer.SerializeToElement(verification, JsonOptions);
            if (!success)
            {
                payload["error"] = JsonSerializer.SerializeToElement("package-verification-failed");
                payload["message"] = JsonSerializer.SerializeToElement("Package contents or provenance do not match the manifest; inspect packageVerification.issues.");
            }
            return (success ? 0 : 1, JsonSerializer.SerializeToElement(payload));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or CryptographicException)
        {
            return (1, JsonSerializer.SerializeToElement(new { success = false, error = "identity-read-failed", message = ex.Message }));
        }
    }

    internal static ExecutableSnapshot Read()
    {
        var assembly = typeof(ToolkitIdentity).Assembly;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(item => item.Key, item => item.Value);
        string? Value(string key) => metadata.GetValueOrDefault(key) is { Length: > 0 } value ? value : null;
        bool? Flag(string key) => bool.TryParse(Value(key), out var value) ? value : null;
        var assemblyPath = Path.GetFullPath(assembly.Location);
        var executablePath = Environment.ProcessPath ?? throw new IOException("The actual process executable path is unavailable.");
        var tool = assembly.GetName().Name switch
        {
            "agentdebug-ui" => "ui", "agentdebug-vs" => "vs", "agentdebug-console" => "console", "agentdebug-mcp" => "server",
            _ => throw new InvalidOperationException("Identity is only supported by toolkit executables.")
        };
        var capabilities = new List<string> { "toolkit.info.v1", "toolkit.package-verification.sha256.v1" };
        capabilities.AddRange(tool switch
        {
            "ui" => new[] { "ui.get-state.v1", "ui.set-toggle-state.v1", "ui.activate.restore.v1", "ui.lookup-diagnostics.v1", "ui.click.coordinates.v1", "ui.send-keys.window.v1",
                "ui.inspect.bounded.v1", "ui.read-visible-text.bounded.v1", "ui.read-rows.uia-paged.v1", "ui.read-tree-items.uia-paged.v1",
                "ui.select-item.verified.v1", "ui.select-row.verified.v1" },
            "vs" => new[] { "vs.get-callstack.v1", "vs.get-exception-info.v1", "vs.evaluate.side-effect-opt-in.v1",
                "vs.help.json.v1", "vs.wait-for-break.long.v1", "vs.instance-targeting.v1", "vs.break-context.v1" },
            "console" => new[] { "console.sessions.v1" },
            _ => new[] { "mcp.toolkit-info.v1", "mcp.tool-groups.v1" }
        });
        return new(1, tool, assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version!.ToString(),
            assembly.ManifestModule.ModuleVersionId.ToString(), executablePath, Hash(executablePath), assemblyPath, Hash(assemblyPath),
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            new(Value("ToolkitSourceCommit"), Flag("ToolkitSourceDirty"), Value("ToolkitConfiguration") ?? "unknown",
                Value("ToolkitRuntimeIdentifier"), Value("ToolkitDeploymentKind") ?? "unknown", Flag("ToolkitSelfContained")), capabilities.ToArray());
    }

    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

internal static class PackageVerifier
{
    private const string ManifestName = "package-manifest.json";
    private const int MaxFiles = 10000;

    internal static PackageVerification Verify(ExecutableSnapshot current)
    {
        var directory = Path.GetDirectoryName(current.AssemblyPath)!;
        var local = Path.Combine(directory, ManifestName);
        var parent = Path.GetDirectoryName(directory);
        var candidate = parent is null ? local : Path.Combine(parent, ManifestName);
        var manifestPath = ManifestExists(local) ? local : ManifestExists(candidate) ? candidate : null;
        if (manifestPath is null) return new("not-present", null, 0, Array.Empty<PackageIssue>());
        var issues = new List<PackageIssue>();
        var checkedFiles = 0;
        try
        {
            var root = Path.GetDirectoryName(manifestPath)!;
            CheckPath(root, ManifestName);
            if (new FileInfo(manifestPath).Length > 4 * 1024 * 1024) throw new InvalidDataException("Manifest exceeds 4 MiB.");
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath), new JsonDocumentOptions { MaxDepth = 32 });
            var manifest = document.RootElement;
            if (manifest.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported manifest schemaVersion.");
            var expectedBuild = current.Build;
            if (manifest.GetProperty("sourceCommit").GetString() != expectedBuild.SourceCommit
                || ReadNullableBool(manifest.GetProperty("sourceDirty")) != expectedBuild.SourceDirty
                || manifest.GetProperty("configuration").GetString() != expectedBuild.Configuration
                || manifest.GetProperty("runtimeIdentifier").GetString() != expectedBuild.RuntimeIdentifier)
                issues.Add(new(ManifestName, "source-or-build-mismatch"));

            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.GetProperty("files").EnumerateArray())
            {
                if (files.Count >= MaxFiles) throw new InvalidDataException("Manifest file limit exceeded.");
                var path = file.GetProperty("path").GetString() ?? throw new InvalidDataException("Missing file path.");
                var hash = file.GetProperty("sha256").GetString() ?? "";
                CheckPath(root, path);
                if (path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || !files.TryAdd(path, hash)
                    || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid or duplicate manifest file entry.");
                if (!File.Exists(Path.Combine(root, path))) issues.Add(new(path, "missing-file"));
                else
                {
                    checkedFiles++;
                    if (!ToolkitIdentity.Hash(Path.Combine(root, path)).Equals(hash, StringComparison.OrdinalIgnoreCase)) issues.Add(new(path, "hash-mismatch"));
                }
            }
            var tools = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in manifest.GetProperty("tools").EnumerateArray())
            {
                var tool = entry.GetProperty("tool").GetString() ?? "";
                var (folder, name) = tool switch
                {
                    "ui" => ("ui", "agentdebug-ui"), "vs" => ("vs", "agentdebug-vs"),
                    "console" => ("console", "agentdebug-console"), "server" => ("mcp", "agentdebug-mcp"),
                    _ => throw new InvalidDataException("Unknown tool identity.")
                };
                if (!tools.Add(tool)) throw new InvalidDataException("Duplicate tool identity.");
                var assemblyPath = Path.Combine(folder, name + ".dll");
                var executablePath = Path.Combine(folder, name + ".exe");
                if (entry.GetProperty("assemblyPath").GetString() != assemblyPath || entry.GetProperty("executablePath").GetString() != executablePath
                    || !files.ContainsKey(assemblyPath) || !files.ContainsKey(executablePath)) throw new InvalidDataException("Tool payload paths are missing or inconsistent.");
                var build = entry.GetProperty("build").Deserialize<ToolkitBuildIdentity>(ToolkitIdentity.JsonOptions)
                    ?? throw new InvalidDataException("Missing tool build identity.");
                if (build.SourceCommit != expectedBuild.SourceCommit || build.SourceDirty != expectedBuild.SourceDirty
                    || build.Configuration != expectedBuild.Configuration || build.RuntimeIdentifier != expectedBuild.RuntimeIdentifier
                    || build.DeploymentKind != "portable-package" || build.SelfContained != true)
                    issues.Add(new(assemblyPath, "tool-build-mismatch"));
                if (tool == current.Tool && (!Path.GetFullPath(Path.Combine(root, assemblyPath)).Equals(current.AssemblyPath, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFullPath(Path.Combine(root, executablePath)).Equals(current.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                    || build != current.Build || entry.GetProperty("version").GetString() != current.Version
                    || entry.GetProperty("moduleVersionId").GetString() != current.ModuleVersionId
                    || entry.GetProperty("contractVersion").GetInt32() != current.ContractVersion
                    || !entry.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()).SequenceEqual(current.Capabilities)))
                    issues.Add(new(assemblyPath, "running-identity-mismatch"));
            }
            if (tools.Count != 4 || !tools.Contains(current.Tool)) throw new InvalidDataException("Manifest must identify all four tools including the running component.");
            foreach (var path in EnumeratePayload(root))
                if (!files.ContainsKey(path) && !path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) issues.Add(new(path, "unexpected-file"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException
            or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or CryptographicException)
        {
            issues.Add(new(ManifestName, "verification-error: " + ex.Message));
        }
        return new(issues.Count == 0 ? "matched" : "mismatched", manifestPath, checkedFiles, issues);
    }

    private static bool? ReadNullableBool(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetBoolean();

    private static bool ManifestExists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void CheckPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')
            || relative.Split('\\', '/').Any(part => part is "" or "." or "..")) throw new InvalidDataException("Unsafe package path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package path escapes root.");
        for (var part = path; part is not null; part = Path.GetDirectoryName(part))
        {
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Package reparse points are not supported.");
            if (part.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private static IEnumerable<string> EnumeratePayload(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var count = 0;
        while (pending.TryPop(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > MaxFiles) throw new InvalidDataException("Package entry limit exceeded.");
                var relative = Path.GetRelativePath(root, path);
                CheckPath(root, relative);
                if (Directory.Exists(path)) pending.Push(path);
                else yield return relative;
            }
    }
}