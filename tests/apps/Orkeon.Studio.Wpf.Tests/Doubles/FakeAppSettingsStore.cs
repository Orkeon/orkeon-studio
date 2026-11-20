using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Wpf.ViewModels.Services;

namespace Orkeon.Studio.Wpf.Tests.Doubles;

/// <summary>An in-memory <see cref="IAppSettingsStore"/>: a path-to-JSON map, no disk involved.</summary>
public sealed class FakeAppSettingsStore : IAppSettingsStore
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public List<string> SavedPaths { get; } = [];

    public string? LastSavedJson { get; private set; }

    public string? LoadError { get; set; }

    /// <summary>When set, a save throws it instead of writing — a locked or read-only file.</summary>
    public Exception? SaveFault { get; set; }

    /// <summary>When set, a load waits for it before it reads the map: a file still being read.</summary>
    public TaskCompletionSource? LoadGate { get; set; }

    public bool Exists(string path) => Files.ContainsKey(path);

    public async Task<(AppSettingsDocument? Document, string? ErrorMessage)> TryLoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (LoadGate is { } gate)
            await gate.Task.ConfigureAwait(false);

        return Load(path);
    }

    private (AppSettingsDocument? Document, string? ErrorMessage) Load(string path)
    {
        if (LoadError is { Length: > 0 })
            return (null, LoadError);

        if (!Files.TryGetValue(path, out var json))
            return (null, $"File not found: {path}");

        return AppSettingsDocument.TryParse(json, out var document, out var error)
            ? (document, null)
            : (null, error);
    }

    public Task SaveAsync(AppSettingsDocument document, string path, CancellationToken cancellationToken = default)
    {
        if (SaveFault is { } fault)
            throw fault;

        LastSavedJson = document.ToJson();
        Files[path] = LastSavedJson;
        SavedPaths.Add(path);
        return Task.CompletedTask;
    }
}
