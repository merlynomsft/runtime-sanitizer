// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ArtifactCacheCli;

internal sealed record CacheEntry(string Name, string RelativePathTemplate);

internal sealed record ResolvedCacheEntry(string Name, string RelativePath, string AbsolutePath)
{
    public bool Exists => Directory.Exists(AbsolutePath);
}

internal sealed record Profile(string Name, IReadOnlyList<CacheEntry> Entries);

internal static class Program
{
    private static readonly JsonSerializerOptions s_jsonSerializerOptions = new()
    {
        WriteIndented = true
    };

    private static readonly Dictionary<string, Profile> s_profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["libs-prereqs"] = new(
            "libs-prereqs",
            [
                new CacheEntry("microsoft.netcore.app.ref", "artifacts/bin/microsoft.netcore.app.ref"),
                new CacheEntry("runtime-pack", "artifacts/bin/runtime/net11.0-{targetOs}-{targetArch}-{librariesConfigurationLower}"),
                new CacheEntry("native-pack", "artifacts/bin/native/net11.0-{targetOs}-{targetArch}-{librariesConfigurationLower}")
            ]),
        ["host-debug-prereqs"] = new(
            "host-debug-prereqs",
            [
                new CacheEntry("corehost", "artifacts/bin/{targetOs}-{targetArch}.{runtimeConfiguration}/corehost"),
                new CacheEntry("coreclr", "artifacts/bin/coreclr/{targetOs}.{targetArch}.{runtimeConfiguration}")
            ]),
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return 1;
        }

        try
        {
            string command = args[0];
            Dictionary<string, string> options = ParseOptions(args[1..]);

            return command switch
            {
                "determine" => RunDetermine(options),
                "pack" => RunPack(options),
                "restore" => await RunRestoreAsync(options).ConfigureAwait(false),
                _ => Fail($"Unknown command '{command}'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int RunDetermine(Dictionary<string, string> options)
    {
        string repoRoot = Require(options, "repo-root");
        string profileName = Require(options, "profile");
        var context = CreateContext(options);

        ResolvedCacheEntry[] entries = ResolveProfile(repoRoot, profileName, context);

        var payload = entries.Select(e => new
        {
            name = e.Name,
            relativePath = e.RelativePath,
            absolutePath = e.AbsolutePath,
            exists = e.Exists
        });

        Console.WriteLine(JsonSerializer.Serialize(payload, s_jsonSerializerOptions));
        return 0;
    }

    private static int RunPack(Dictionary<string, string> options)
    {
        string repoRoot = Require(options, "repo-root");
        string profileName = Require(options, "profile");
        string outputDir = Require(options, "output-dir");
        string artifactPrefix = Require(options, "artifact-prefix");

        var context = CreateContext(options);
        ResolvedCacheEntry[] entries = ResolveProfile(repoRoot, profileName, context);

        Directory.CreateDirectory(outputDir);

        foreach (ResolvedCacheEntry entry in entries)
        {
            if (!entry.Exists)
            {
                Console.WriteLine($"skip: '{entry.RelativePath}' does not exist.");
                continue;
            }

            string archivePath = Path.Combine(outputDir, $"{artifactPrefix}-{entry.Name}.zip");
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            AddDirectoryToArchive(archive, repoRoot, entry.AbsolutePath);

            Console.WriteLine($"packed: {archivePath}");
        }

        return 0;
    }

    private static async Task<int> RunRestoreAsync(Dictionary<string, string> options)
    {
        string repoRoot = Require(options, "repo-root");
        string profileName = Require(options, "profile");
        string owner = Require(options, "owner");
        string repo = Require(options, "repo");
        string artifactPrefix = Require(options, "artifact-prefix");

        string? token = ReadOptional(options, "token")
            ?? Environment.GetEnvironmentVariable("RUNTIME_SANITIZER_GITHUB_TOKEN")
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? Environment.GetEnvironmentVariable("GH_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            return Fail("A GitHub token is required. Set --token, RUNTIME_SANITIZER_GITHUB_TOKEN, GITHUB_TOKEN, or GH_TOKEN.");
        }

        var context = CreateContext(options);
        ResolvedCacheEntry[] entries = ResolveProfile(repoRoot, profileName, context);

        using HttpClient client = CreateGitHubClient(token);

        foreach (ResolvedCacheEntry entry in entries)
        {
            string artifactName = $"{artifactPrefix}-{entry.Name}";
            ArtifactInfo? artifact = await FindLatestArtifactAsync(client, owner, repo, artifactName).ConfigureAwait(false);
            if (artifact is null)
            {
                Console.WriteLine($"miss: artifact '{artifactName}' not found.");
                continue;
            }

            string temporaryArchive = Path.GetTempFileName();
            try
            {
                await DownloadArtifactArchiveAsync(client, owner, repo, artifact.Id, artifactName, temporaryArchive).ConfigureAwait(false);
                using ZipArchive archive = ZipFile.OpenRead(temporaryArchive);
                ExtractArchiveSafely(archive, repoRoot);
            }
            finally
            {
                if (File.Exists(temporaryArchive))
                {
                    File.Delete(temporaryArchive);
                }
            }

            Console.WriteLine($"restored: {artifactName} (run {artifact.WorkflowRun?.Id?.ToString() ?? "unknown"})");
        }

        return 0;
    }

    private static HttpClient CreateGitHubClient(string token)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("runtime-sanitizer-artifact-cache", "1.0"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static async Task<ArtifactInfo?> FindLatestArtifactAsync(HttpClient client, string owner, string repo, string artifactName)
    {
        string url = $"https://api.github.com/repos/{owner}/{repo}/actions/artifacts?name={Uri.EscapeDataString(artifactName)}&per_page=100";
        using var response = await client.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub API request failed ({(int)response.StatusCode}) for artifact '{artifactName}'.");
        }

        using Stream json = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        ArtifactListResponse? parsed;
        try
        {
            parsed = await JsonSerializer.DeserializeAsync<ArtifactListResponse>(json).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse artifact list response for '{artifactName}'.", ex);
        }

        return parsed?.Artifacts?
            .Where(static a => !a.Expired)
            .OrderByDescending(static a => a.CreatedAt)
            .FirstOrDefault();
    }

    private static async Task DownloadArtifactArchiveAsync(HttpClient client, string owner, string repo, long artifactId, string artifactName, string destinationPath)
    {
        string url = $"https://api.github.com/repos/{owner}/{repo}/actions/artifacts/{artifactId}/zip";
        using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub artifact download failed ({(int)response.StatusCode}) for artifact '{artifactName}' (id '{artifactId}').");
        }

        using Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using FileStream destination = File.Create(destinationPath);
        await source.CopyToAsync(destination).ConfigureAwait(false);
    }

    private static void AddDirectoryToArchive(ZipArchive archive, string repoRoot, string sourceDir)
    {
        IEnumerable<string> files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories);
        foreach (string file in files)
        {
            string relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, relativePath, CompressionLevel.Optimal);
        }
    }

    private static void ExtractArchiveSafely(ZipArchive archive, string destinationRoot)
    {
        string root = Path.GetFullPath(destinationRoot);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (Path.IsPathRooted(entry.FullName) ||
                (entry.FullName.Length >= 2 && char.IsAsciiLetter(entry.FullName[0]) && entry.FullName[1] == ':'))
            {
                throw new InvalidOperationException($"Archive entry '{entry.FullName}' uses an absolute path.");
            }

            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            string destinationPath = Path.GetFullPath(Path.Combine(root, entry.FullName));
            string relativePath = Path.GetRelativePath(root, destinationPath);
            if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            {
                throw new InvalidOperationException($"Archive entry '{entry.FullName}' resolves outside repository root.");
            }

            string? directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static ResolvedCacheEntry[] ResolveProfile(string repoRoot, string profileName, CacheContext context)
    {
        if (!s_profiles.TryGetValue(profileName, out Profile? profile))
        {
            throw new InvalidOperationException($"Unknown profile '{profileName}'.");
        }

        string normalizedRepoRoot = Path.GetFullPath(repoRoot);

        return profile.Entries
            .Select(entry =>
            {
                string relativePath = ExpandTemplate(entry.RelativePathTemplate, context)
                    .Replace('/', Path.DirectorySeparatorChar);
                string absolutePath = Path.GetFullPath(Path.Combine(normalizedRepoRoot, relativePath));
                return new ResolvedCacheEntry(entry.Name, relativePath, absolutePath);
            })
            .ToArray();
    }

    private static string ExpandTemplate(string template, CacheContext context)
    {
        return template
            .Replace("{targetOs}", context.TargetOs, StringComparison.Ordinal)
            .Replace("{targetArch}", context.TargetArch, StringComparison.Ordinal)
            .Replace("{librariesConfiguration}", context.LibrariesConfiguration, StringComparison.Ordinal)
            .Replace("{librariesConfigurationLower}", context.LibrariesConfiguration.ToLowerInvariant(), StringComparison.Ordinal)
            .Replace("{runtimeConfiguration}", context.RuntimeConfiguration, StringComparison.Ordinal);
    }

    private static CacheContext CreateContext(Dictionary<string, string> options)
    {
        return new CacheContext(
            ReadOptional(options, "target-os") ?? "linux",
            ReadOptional(options, "target-arch") ?? "x64",
            ReadOptional(options, "libraries-configuration") ?? "Debug",
            ReadOptional(options, "runtime-configuration") ?? "Debug");
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected argument '{arg}'.");
            }

            string key = arg[2..];
            if (string.IsNullOrEmpty(key))
            {
                throw new InvalidOperationException("Option name cannot be empty.");
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                dict[key] = "true";
                continue;
            }

            dict[key] = args[++i];
        }

        return dict;
    }

    private static string Require(Dictionary<string, string> options, string key)
    {
        if (!options.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required option --{key}.");
        }

        return value;
    }

    private static string? ReadOptional(Dictionary<string, string> options, string key)
    {
        return options.TryGetValue(key, out string? value) ? value : null;
    }

    private static bool IsHelp(string value)
    {
        return value is "-h" or "--help" or "help";
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            ArtifactCacheCli

            Commands:
              determine --repo-root <path> --profile <name> [--target-os linux] [--target-arch x64] [--libraries-configuration Debug] [--runtime-configuration Debug]
              pack      --repo-root <path> --profile <name> --output-dir <path> --artifact-prefix <prefix> [context args]
              restore   --repo-root <path> --profile <name> --owner <owner> --repo <repo> --artifact-prefix <prefix> [--token <token>] [context args]

            Profiles:
              libs-prereqs
              host-debug-prereqs
            """);
    }

    private sealed record CacheContext(
        string TargetOs,
        string TargetArch,
        string LibrariesConfiguration,
        string RuntimeConfiguration);

    private sealed record ArtifactListResponse(List<ArtifactInfo>? Artifacts);

    private sealed record ArtifactInfo(
        long Id,
        bool Expired,
        DateTimeOffset CreatedAt,
        ArtifactRunInfo? WorkflowRun);

    private sealed record ArtifactRunInfo(long? Id);
}
