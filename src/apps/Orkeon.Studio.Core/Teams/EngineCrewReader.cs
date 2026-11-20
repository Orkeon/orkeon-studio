using Microsoft.Extensions.Logging.Abstractions;
using Orkeon.Compliance.Vfs;
using Orkeon.Domain.Configuration;
using Orkeon.Domain.Exceptions;
using Orkeon.Domain.FileSystem;
using Orkeon.Infrastructure.Configuration;
using Orkeon.Infrastructure.FileSystem;
using Orkeon.Infrastructure.Security;
using Orkeon.Infrastructure.Serialization;
using Orkeon.Studio.Core.Targets;
using YamlDotNet.Core;

namespace Orkeon.Studio.Core.Teams;

/// <summary>
/// Reads a YAML crew with the engine's own loader, in process (<c>YamlCrewDefinitionLoader</c> over a
/// read-only mount of the crew's folder, as <c>orkeon run</c> reads it), and hands the crew to a judge
/// when both the loader and the validator the crew factory runs next (<c>CrewDefinitionValidator</c>)
/// accept it. A crew either refuses gets the judge's empty answer, so that the load error the launch
/// reports is the one thing the user reads. A <c>.ork.ts</c> crew is never read: the CLI judges it.
/// The crew read last is kept with the text of every YAML file the loader reads: the judges of one
/// refresh (the deliverable writers, the decision usage) share one load, and a file changed since,
/// added or removed reads the crew again.
/// </summary>
internal static class EngineCrewReader
{
    /// <summary>Where the crew's folder is mounted for the engine's loader, as <c>orkeon run</c> mounts it.</summary>
    private const string CrewMount = "/crew";

    private static readonly Lock CacheLock = new();

    /// <summary>The crew read last: its key and its files' text, and the crew (null when the engine refused it).</summary>
    private static (string Key, string Files, CrewConfiguration? Crew)? s_lastRead;

    /// <summary>The judge's answer for the crew <paramref name="target"/> runs, or <paramref name="none"/>.</summary>
    [SuppressVfsCompliance(
        "EXCEPTION-BOOTSTRAP: Studio is a host application; the crew's folder is user-owned storage on the " +
        "physical disk, mounted for the engine's loader as orkeon run mounts it.")]
    public static T Read<T>(RunTarget? target, Func<CrewConfiguration, T> judge, T none)
    {
        if (target is null || target.Dialect != RunTargetDialect.Yaml || string.IsNullOrWhiteSpace(target.RunPath))
            return none;

        var directory = target.Kind == RunTargetKind.MultiFileCrewDirectory;
        var folder = directory ? target.RunPath : Path.GetDirectoryName(Path.GetFullPath(target.RunPath));
        if (string.IsNullOrEmpty(folder))
            return none;

        var key = $"{(directory ? 'd' : 'f')}|{Path.GetFullPath(target.RunPath)}";
        var files = Fingerprint(directory, target.RunPath, folder);
        CrewConfiguration? crew;
        lock (CacheLock)
        {
            if (files is not null && s_lastRead is { } last && last.Key == key && string.Equals(last.Files, files, StringComparison.Ordinal))
                return last.Crew is { } kept ? judge(kept) : none;
        }

        // The engine's file system is async; nothing here awaits the caller's context.
        crew = Task.Run(async () =>
        {
            try
            {
                using var registry = new FileSystemRegistry(
                    [FileSystemMount.Parse($"{FileSystemMount.Quote(folder)}:{CrewMount}:ro")]);
                var loader = Loader(registry, folder);
                var config = directory
                    ? await loader.LoadFromDirectoryAsync(CrewMount).ConfigureAwait(false)
                    : await loader.LoadFromFileAsync($"{CrewMount}/{Path.GetFileName(target.RunPath)}").ConfigureAwait(false);
                return loader.Validate(config).IsValid ? config : null;
            }
            catch (Exception ex) when (IsRefusal(ex))
            {
                return null;
            }
        }).GetAwaiter().GetResult();

        // Kept only when the files read the same after the load: one changed meanwhile reads it again next time.
        if (files is not null && string.Equals(files, Fingerprint(directory, target.RunPath, folder), StringComparison.Ordinal))
        {
            lock (CacheLock)
                s_lastRead = (key, files, crew);
        }

        return crew is not null ? judge(crew) : none;
    }

    /// <summary>
    /// The text of every YAML file the engine's loader may read for the crew, each after its path: the
    /// file itself for a single-file crew; for a crew folder, its own YAML files and those of its
    /// <c>agents/</c> and <c>tasks/</c> folders. Null when a file cannot be read: the crew is then read
    /// again, never served from what was kept.
    /// </summary>
    [SuppressVfsCompliance(
        "EXCEPTION-BOOTSTRAP: Studio is a host application; the crew's folder is user-owned storage on the " +
        "physical disk, read to tell whether the crew read last is still the one on disk.")]
    private static string? Fingerprint(bool directory, string runPath, string folder)
    {
        try
        {
            IEnumerable<string> paths = directory
                ? new[] { folder, Path.Combine(folder, "agents"), Path.Combine(folder, "tasks") }
                    .Where(Directory.Exists)
                    .SelectMany(Directory.EnumerateFiles)
                    .Where(IsYaml)
                    .Order(StringComparer.Ordinal)
                : [Path.GetFullPath(runPath)];
            var text = new System.Text.StringBuilder();
            foreach (var path in paths)
                text.Append(path).Append('\0').Append(File.ReadAllText(path)).Append('\0');
            return text.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsYaml(string path) =>
        Path.GetExtension(path) is { } extension
        && (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase));

    /// <summary>The judge's answer for a single-file crew's text, or <paramref name="none"/>.</summary>
    public static T ReadText<T>(string yaml, Func<CrewConfiguration, T> judge, T none)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        try
        {
            using var registry = new FileSystemRegistry([]);
            var loader = Loader(registry, workspace: null);
            // A string is loaded at once: the task is complete when it returns.
            var config = loader.LoadFromStringAsync(yaml).GetAwaiter().GetResult();
            return loader.Validate(config).IsValid ? judge(config) : none;
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            return none;
        }
    }

    /// <summary>
    /// The engine's crew loader over <paramref name="registry"/>, its paths checked by the engine's own
    /// validator against the crew's folder (<paramref name="workspace"/>), the one root it reads.
    /// </summary>
    private static YamlCrewDefinitionLoader Loader(FileSystemRegistry registry, string? workspace) =>
        new(
            new YamlDotNetSerializer(),
            new FileSystemService(
                registry,
                new PathValidator(new PathSecurityOptions { DefaultWorkspaceRoot = workspace }, NullLogger<PathValidator>.Instance),
                NullLogger<FileSystemService>.Instance),
            NullLogger<YamlCrewDefinitionLoader>.Instance);

    /// <summary>
    /// Whether <paramref name="ex"/> is how the engine's loader or its file system refuses a crew: what the
    /// launch would report as the load error.
    /// </summary>
    private static bool IsRefusal(Exception ex) =>
        ex is InvalidOperationException or ArgumentException or FormatException or InvalidCastException
            or NotSupportedException or IOException or UnauthorizedAccessException or YamlException
            or FileAccessDeniedException or DomainException;
}
