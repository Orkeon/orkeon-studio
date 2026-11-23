using System.Text.Json.Serialization;

namespace Orkeon.Studio.Core.Profiles;

/// <summary>
/// Every model connection on this machine and the settings they give — one per (connection, model)
/// pair, <see cref="Profiles"/> — plus the two elections: the default (applied to new
/// teams and mirrored into the settings file) and Studio's own assistant profile (null until
/// the user picks one — the creation wizard is gated on it). Immutable; every mutation
/// returns the set to persist.
/// </summary>
public sealed record ModelProfileSet
{
    /// <summary>The empty first-run state: no profile, nothing elected.</summary>
    public static ModelProfileSet Empty { get; } = new();

    /// <summary>
    /// The connections, in display order — what the file stores, under the name <c>Profiles</c> it bore
    /// when each entry was one setting: such an entry reads as a connection of one model.
    /// </summary>
    [JsonPropertyName("Profiles")]
    public IReadOnlyList<ModelConnection> Connections
    {
        get => _connections;
        init
        {
            _connections = value ?? [];
            _profiles =
            [
                .. _connections.OfType<ModelConnection>()
                    .Where(connection => !string.IsNullOrWhiteSpace(connection.Name))
                    .SelectMany(connection => connection.Flatten()),
            ];
        }
    }

    /// <summary>
    /// The settings, in display order: each (connection, model) pair (<see cref="ModelConnection.Flatten"/>),
    /// computed once per <see cref="Connections"/> — a connection without a name, a hand edit, gives
    /// none. Set, each profile becomes a connection of one model.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ModelProfile> Profiles
    {
        get => _profiles;
        init => Connections = [.. value.Select(ModelConnection.Of)];
    }

    private readonly IReadOnlyList<ModelConnection> _connections = [];
    private readonly IReadOnlyList<ModelProfile> _profiles = [];

    /// <summary>Name of the default profile, or null when none is elected.</summary>
    public string? DefaultProfile { get; init; }

    /// <summary>Name of the profile Studio's assistant uses, or null while unconfigured.</summary>
    public string? StudioProfile { get; init; }

    /// <summary>The elected default, resolved; null when absent or dangling.</summary>
    [JsonIgnore]
    public ModelProfile? Default => Find(DefaultProfile);

    /// <summary>The assistant's profile, resolved; null while unconfigured or dangling.</summary>
    [JsonIgnore]
    public ModelProfile? Studio => Find(StudioProfile);

    /// <summary>Finds a profile by name (ordinal), or null.</summary>
    public ModelProfile? Find(string? name) =>
        name is { Length: > 0 }
            ? Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal))
            : null;

    /// <summary>Finds a connection by name (ordinal), or null.</summary>
    public ModelConnection? FindConnection(string? name) =>
        name is { Length: > 0 }
            ? Connections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
            : null;

    /// <summary>
    /// Adds or replaces a setting, as a connection of one model named after it
    /// (<see cref="ModelConnection.Of"/>): <see cref="UpsertConnection"/>, its renames left to the
    /// elections. A rename onto a name another setting already bears absorbs that setting — the name is
    /// the identity teams reference, so the set never holds two entries with the same one. While no
    /// default is elected, the upserted profile becomes it — a machine with exactly one setting has
    /// exactly one answer to "which one".
    /// </summary>
    public ModelProfileSet Upsert(ModelProfile profile, string? previousName = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return UpsertConnection(ModelConnection.Of(profile), previousName).Set;
    }

    /// <summary>
    /// Adds or replaces a connection — <paramref name="previousName"/> when it renames one — and says
    /// which pairs it renamed and which it removed, so that what names them (teams, the host profiles,
    /// the decision ladder) can follow. The elections follow the renames; one whose pair is gone falls
    /// back as <see cref="Remove"/> does. Each model's previous pair is
    /// <paramref name="previousPairNames"/> at its index (null for a model the edit added); without the
    /// list, models are matched by alias, and the one model of a connection that keeps one is matched
    /// whatever its alias. A pair of another connection bearing the name of one of the new pairs is
    /// absorbed, as a connection bearing the new name is.
    /// </summary>
    /// <param name="connection">The connection as edited.</param>
    /// <param name="previousName">The name the connection bore, when the edit renames it.</param>
    /// <param name="previousPairNames">For each model of <paramref name="connection"/>, in order, the pair it was.</param>
    public ConnectionUpsert UpsertConnection(ModelConnection connection, string? previousName = null, IReadOnlyList<string?>? previousPairNames = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(connection.Name);

        var edited = connection.Normalized();
        var target = previousName ?? edited.Name;
        var before = FindConnection(target);
        var oldPairs = before?.PairNames ?? [];
        var newPairs = edited.PairNames;
        var origins = previousPairNames ?? OriginsByAlias(before, edited);
        if (origins.Count != newPairs.Count)
            throw new ArgumentException("One previous pair name is expected per model of the connection.", nameof(previousPairNames));

        var renames = new List<(string From, string To)>();
        for (var i = 0; i < newPairs.Count; i++)
        {
            if (origins[i] is { } from && oldPairs.Contains(from, StringComparer.Ordinal) && !string.Equals(from, newPairs[i], StringComparison.Ordinal))
                renames.Add((from, newPairs[i]));
        }

        var carried = origins.OfType<string>().ToHashSet(StringComparer.Ordinal);
        var removed = oldPairs.Where(pair => !carried.Contains(pair) && !newPairs.Contains(pair, StringComparer.Ordinal)).ToList();

        // The other connections, without a homonym of the new one nor the pairs whose names it takes.
        var taken = newPairs.ToHashSet(StringComparer.Ordinal);
        var connections = new List<ModelConnection>();
        var placed = false;
        foreach (var existing in Connections)
        {
            if (before is not null && ReferenceEquals(existing, before))
            {
                connections.Add(edited);
                placed = true;
                continue;
            }

            if (string.Equals(existing.Name, edited.Name, StringComparison.Ordinal))
            {
                removed.AddRange(existing.PairNames.Where(pair => !taken.Contains(pair)));
                continue;
            }

            if (Without(existing, taken) is { } kept)
                connections.Add(kept);
        }

        if (!placed)
            connections.Add(edited);

        var set = this with { Connections = connections };
        set = set with
        {
            DefaultProfile = Follow(DefaultProfile, renames) ?? newPairs[0],
            StudioProfile = Follow(StudioProfile, renames),
        };
        return new ConnectionUpsert(set.Fallen(), renames, removed);
    }

    /// <summary>
    /// Removes a setting: its model from its connection, and the connection with its last model. A
    /// dangling election is cleared rather than left pointing at nothing; the default falls back to the
    /// first remaining profile.
    /// </summary>
    public ModelProfileSet Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var connections = new List<ModelConnection>();
        foreach (var connection in Connections)
        {
            if (Without(connection, new HashSet<string>(StringComparer.Ordinal) { name }) is { } kept)
                connections.Add(kept);
        }

        return (this with { Connections = connections }).Fallen();
    }

    /// <summary>Removes a connection and every pair of it, the elections falling back as <see cref="Remove"/> says.</summary>
    public ModelProfileSet RemoveConnection(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return (this with { Connections = [.. Connections.Where(c => !string.Equals(c.Name, name, StringComparison.Ordinal))] }).Fallen();
    }

    /// <summary>Elects a default; a name that matches no profile leaves the set unchanged.</summary>
    public ModelProfileSet WithDefault(string name) =>
        Find(name) is null ? this : this with { DefaultProfile = name };

    /// <summary>Elects the assistant's profile; unknown names leave the set unchanged.</summary>
    public ModelProfileSet WithStudio(string name) =>
        Find(name) is null ? this : this with { StudioProfile = name };

    /// <summary>
    /// The set with its elections on pairs that exist: a dangling default falls back to the first
    /// pair, a dangling assistant setting is cleared.
    /// </summary>
    private ModelProfileSet Fallen()
    {
        var profiles = Profiles;
        var names = profiles.Select(profile => profile.Name).ToHashSet(StringComparer.Ordinal);
        return this with
        {
            DefaultProfile = DefaultProfile is { } elected && names.Contains(elected) ? elected : profiles.Count > 0 ? profiles[0].Name : null,
            StudioProfile = StudioProfile is { } studio && names.Contains(studio) ? studio : null,
        };
    }

    private static string? Follow(string? name, List<(string From, string To)> renames) =>
        name is null ? null : renames.FirstOrDefault(rename => string.Equals(rename.From, name, StringComparison.Ordinal)).To ?? name;

    /// <summary><paramref name="connection"/> without its pairs named in <paramref name="names"/>, or null when none is left.</summary>
    private static ModelConnection? Without(ModelConnection connection, HashSet<string> names)
    {
        var models = connection.DeclaredModels;
        var kept = models.Where(model => !names.Contains(ModelConnection.PairName(connection.Name, model.Alias))).ToList();
        if (kept.Count == models.Count)
            return connection;

        return kept.Count == 0 ? null : (connection with { Models = kept }).Normalized();
    }

    /// <summary>The previous pair of each model of <paramref name="edited"/>, matched by alias; the one model of each side to each other.</summary>
    private static List<string?> OriginsByAlias(ModelConnection? before, ModelConnection edited)
    {
        var models = edited.DeclaredModels;
        if (before is null)
            return [.. models.Select(_ => (string?)null)];

        var previous = before.DeclaredModels;
        if (previous.Count == 1 && models.Count == 1)
            return [ModelConnection.PairName(before.Name, previous[0].Alias)];

        return
        [
            .. models.Select(model => previous.FirstOrDefault(old => string.Equals(old.Alias?.Trim() ?? "", model.Alias?.Trim() ?? "", StringComparison.Ordinal)) is { } match
                ? ModelConnection.PairName(before.Name, match.Alias)
                : null),
        ];
    }

    /// <summary>
    /// A unique copy name for duplication: "Name (copy)", "Name (copy 2)", … — unique as a name,
    /// and as whatever else <paramref name="isTaken"/> says a name must not collide on: the host
    /// profile id it gives (STUDIO-48), which a suffix written in another script, « (副本) », does
    /// not change.
    /// </summary>
    /// <param name="name">The name of the setting being copied.</param>
    /// <param name="copySuffix">The localized word for « copy ».</param>
    /// <param name="isTaken">Refuses a candidate the set's names alone would accept; null accepts any.</param>
    public string CopyNameFor(string name, string copySuffix, Func<string, bool>? isTaken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(copySuffix);

        var candidate = $"{name} ({copySuffix})";
        for (var i = 2; Find(candidate) is not null || isTaken?.Invoke(candidate) == true; i++)
            candidate = $"{name} ({copySuffix} {i})";
        return candidate;
    }
}

/// <summary>
/// What <see cref="ModelProfileSet.UpsertConnection"/> did: the set to persist, the pairs it renamed
/// (old name, new name) and the pairs it removed — a model the edit dropped, or a pair another
/// connection took the name of.
/// </summary>
/// <param name="Set">The set to persist.</param>
/// <param name="Renames">Each renamed pair, in the order of the connection's models.</param>
/// <param name="Removed">Each pair the edit removed.</param>
public sealed record ConnectionUpsert(ModelProfileSet Set, IReadOnlyList<(string From, string To)> Renames, IReadOnlyList<string> Removed);
