using Orkeon.Constants.Llm;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Core.Profiles;

/// <summary>What becomes of a model setting's name as a host LLM profile (STUDIO-48).</summary>
public enum HostProfileStatus
{
    /// <summary>Offered to crews: the setting is the host profile <see cref="HostProfileCheck.Id"/>.</summary>
    Offered,

    /// <summary>The setting names no provider (the « no model » card): no host profile can be an echo.</summary>
    NoProvider,

    /// <summary>The name keeps no ASCII letter or digit: there is no id for a crew to write.</summary>
    NoId,

    /// <summary>The id is <c>default</c>, the reserved name of the <c>Llm</c> section itself.</summary>
    DefaultName,

    /// <summary>The id is <c>auto</c>, the reserved name of the automatic pick (<c>llm: { profile: auto }</c>).</summary>
    AutoName,

    /// <summary>Another setting answers to the same id (<see cref="HostProfileCheck.TakenBy"/>).</summary>
    TakenBySetting,

    /// <summary>An entry written by hand in the settings file holds the id; Studio never takes one over.</summary>
    TakenByFile,
}

/// <summary>The standing of one name as a host profile.</summary>
/// <param name="Status">Whether the name is offered, and why not.</param>
/// <param name="Id">The id the name gives (<see cref="ModelProfile.HostProfileIdOf"/>); null when it gives none.</param>
/// <param name="TakenBy">The setting that already answers to <paramref name="Id"/>, for <see cref="HostProfileStatus.TakenBySetting"/>.</param>
public sealed record HostProfileCheck(HostProfileStatus Status, string? Id, string? TakenBy = null)
{
    /// <summary>True when the setting is offered to crews under <see cref="Id"/>.</summary>
    public bool IsOffered => Status == HostProfileStatus.Offered;

    /// <summary>
    /// Whether the editor refuses the name: the reserved name, or an id another owner holds. A
    /// setting with no provider or no id is legitimate — it serves a team or the assistant — and
    /// is simply offered to no crew.
    /// </summary>
    public bool BlocksSave => Status is HostProfileStatus.DefaultName or HostProfileStatus.AutoName or HostProfileStatus.TakenBySetting or HostProfileStatus.TakenByFile;
}

/// <summary>
/// Studio's model settings as the host's LLM profiles (STUDIO-48). Each setting that names a
/// provider is the entry <c>Llm:Profiles:&lt;id&gt;</c> of the settings file — its id the name's
/// slug —, written without the key: the entry names the variable holding it (<c>ApiKeyEnvVar</c>,
/// STUDIO-49), which a run outside Studio reads — a Docker Model Runner setting carries its card's
/// placeholder <c>not-needed</c> instead (STUDIO-54) —, and a launch passes the key itself as
/// <c>ORKEON_Llm__Profiles__&lt;id&gt;__ApiKey</c>; so a crew that writes <c>profile: claude</c> runs
/// on the « Claude » setting from Studio, and from a terminal <c>orkeon run</c> or a scheduled
/// team reading the same file. The elected setting is the <c>Llm</c> section itself, written
/// whole (<see cref="ElectDefault"/>). An entry no setting owns was written by hand: Studio shows
/// it, never rewrites it, never removes it, and refuses a name that would take it over.
/// <para>
/// Ownership is the rule of the fiche: an id is Studio's when a setting of
/// <c>studio-model-profiles.json</c> offers it. The first setting to answer to an id owns it — the
/// editor refuses a second one, so only a hand-edited store can hold two.
/// </para>
/// </summary>
public static class HostLlmProfiles
{
    /// <summary>Each setting's standing, by name, in set order.</summary>
    public static IReadOnlyDictionary<string, HostProfileCheck> Classify(ModelProfileSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        var standing = new Dictionary<string, HostProfileCheck>(StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in set.Profiles)
        {
            // Two settings under one name: the first one is the one every lookup finds.
            if (!standing.ContainsKey(profile.Name))
                standing[profile.Name] = Check(profile, owners);
        }

        return standing;
    }

    /// <summary>
    /// One setting's standing, given the ids the settings before it own; a setting that may
    /// offer its id takes it, so the next one answering to it is told whose it is.
    /// </summary>
    private static HostProfileCheck Check(ModelProfile profile, Dictionary<string, string> owners)
    {
        var id = profile.HostProfileId;
        if (Reserved(id) is { } reserved)
            return reserved;

        if (id is not null && owners.TryGetValue(id, out var owner))
            return new HostProfileCheck(HostProfileStatus.TakenBySetting, id, owner);

        if (id is not null)
            owners[id] = profile.Name;

        if (!profile.DescribesProvider)
            return new HostProfileCheck(HostProfileStatus.NoProvider, id);

        return id is null
            ? new HostProfileCheck(HostProfileStatus.NoId, null)
            : new HostProfileCheck(HostProfileStatus.Offered, id);
    }

    /// <summary>The host profile id each offered setting answers to, by setting name.</summary>
    public static IReadOnlyDictionary<string, string> Offered(ModelProfileSet set) =>
        Classify(set)
            .Where(entry => entry.Value.IsOffered)
            .ToDictionary(entry => entry.Key, entry => entry.Value.Id!, StringComparer.Ordinal);

    /// <summary>
    /// The setting names whose host profile differs between <paramref name="before"/> and
    /// <paramref name="after"/> (STUDIO-50): offered on one side only, or under another id — a
    /// setting created, removed, renamed, switched to « no model ». A team whose companion file
    /// names one of them now runs elsewhere outside Studio, and its launchers say so again.
    /// </summary>
    /// <param name="before">The settings before the change.</param>
    /// <param name="after">The settings after it.</param>
    public static IReadOnlySet<string> MovedNames(ModelProfileSet before, ModelProfileSet after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var was = Offered(before);
        var now = Offered(after);
        var moved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in was.Keys.Concat(now.Keys))
        {
            if (!string.Equals(was.GetValueOrDefault(name), now.GetValueOrDefault(name), StringComparison.OrdinalIgnoreCase))
                moved.Add(name);
        }

        return moved;
    }

    /// <summary>
    /// The entries of <paramref name="document"/>'s <c>Llm:Profiles</c> no setting of
    /// <paramref name="set"/> owns — written by hand —, in document order.
    /// </summary>
    public static IReadOnlyList<LlmProfileEntry> HandWritten(AppSettingsDocument document, ModelProfileSet set)
    {
        ArgumentNullException.ThrowIfNull(document);

        var owned = new HashSet<string>(Offered(set).Values, StringComparer.OrdinalIgnoreCase);
        var profiles = document.Llm.Profiles;
        return
        [
            .. profiles.Ids
                .Where(id => !owned.Contains(id))
                .Select(id => profiles.Get(id))
                .OfType<LlmProfileEntry>(),
        ];
    }

    /// <summary>
    /// The standing a name typed in the profile editor would have.
    /// </summary>
    /// <param name="name">The name as typed.</param>
    /// <param name="previousName">The name the edited setting had; null for a new one.</param>
    /// <param name="describesProvider">Whether the draft names an endpoint or a model.</param>
    /// <param name="set">The settings as they are.</param>
    /// <param name="document">The settings file being edited, for the entries written by hand.</param>
    public static HostProfileCheck Check(
        string name,
        string? previousName,
        bool describesProvider,
        ModelProfileSet set,
        AppSettingsDocument document) =>
        Check(name, previousName is null ? [] : [previousName], [], describesProvider, set, document);

    /// <summary>
    /// The standing one model of a connection being edited would have, its pair named
    /// <paramref name="name"/>: the settings the edit replaces — the connection's pairs as they were —
    /// own no id, and a model above it in the editor that answers to the same id takes it first, as a
    /// setting earlier in the set does (<see cref="Classify"/>).
    /// </summary>
    /// <param name="name">The pair's name as typed (<see cref="ModelConnection.PairName"/>).</param>
    /// <param name="replaced">The names of the settings the edit replaces.</param>
    /// <param name="above">The pair names of the models above it in the editor.</param>
    /// <param name="describesProvider">Whether the draft names an endpoint or a model.</param>
    /// <param name="set">The settings as they are.</param>
    /// <param name="document">The settings file being edited, for the entries written by hand.</param>
    public static HostProfileCheck Check(
        string name,
        IReadOnlyCollection<string> replaced,
        IReadOnlyList<string> above,
        bool describesProvider,
        ModelProfileSet set,
        AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(replaced);
        ArgumentNullException.ThrowIfNull(above);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(document);

        var id = ModelProfile.HostProfileIdOf(name?.Trim());
        if (Reserved(id) is { } reserved)
            return reserved;

        if (id is not null
            && above.FirstOrDefault(other => string.Equals(ModelProfile.HostProfileIdOf(other.Trim()), id, StringComparison.OrdinalIgnoreCase)) is { } sibling)
        {
            return new HostProfileCheck(HostProfileStatus.TakenBySetting, id, sibling.Trim());
        }

        if (id is not null
            && set.Profiles.FirstOrDefault(p =>
                    !replaced.Contains(p.Name, StringComparer.Ordinal)
                    && string.Equals(p.HostProfileId, id, StringComparison.OrdinalIgnoreCase)) is { } other)
        {
            return new HostProfileCheck(HostProfileStatus.TakenBySetting, id, other.Name);
        }

        if (!describesProvider)
            return new HostProfileCheck(HostProfileStatus.NoProvider, id);
        if (id is null)
            return new HostProfileCheck(HostProfileStatus.NoId, null);

        return HandWritten(document, set).Any(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase))
            ? new HostProfileCheck(HostProfileStatus.TakenByFile, id)
            : new HostProfileCheck(HostProfileStatus.Offered, id);
    }

    /// <summary>
    /// The standing of an id the engine reserves: <c>default</c>, the <c>Llm</c> section itself, and
    /// <c>auto</c>, the automatic pick — a profile under either refuses the run's start. Null otherwise.
    /// </summary>
    private static HostProfileCheck? Reserved(string? id)
    {
        if (id is null)
            return null;
        if (LlmProfilesSection.IsDefault(id))
            return new HostProfileCheck(HostProfileStatus.DefaultName, id);
        return Orkeon.Application.Interfaces.Ports.LlmProfiles.IsAuto(id) ? new HostProfileCheck(HostProfileStatus.AutoName, id) : null;
    }

    /// <summary>
    /// Brings <paramref name="document"/>'s <c>Llm:Profiles</c> in line with a change of the
    /// settings from <paramref name="before"/> to <paramref name="after"/>: a renamed setting's
    /// entry moves, every key it carries with it; the entry of a setting no longer offered —
    /// removed, switched to « no model » — goes; and every offered setting the change touched, or
    /// whose entry the file lacks, carries without the reference to its key's variable (STUDIO-49)
    /// or with a key its card disagrees with (<see cref="LlmProfilesSection.KeyAgrees(LlmProfileEntry)"/>,
    /// STUDIO-54), is written, without its key. What names a profile follows a rename:
    /// <c>Orkeon:Rag:LlmProfile</c>, <c>Orkeon:Compaction:Profile</c> and the rungs of
    /// <c>Orkeon:Decision:Route:Ladder</c>. When its profile goes, the first falls back to the default
    /// and the second to the agent's own model (the host refuses to start on a profile it does not
    /// define); a rung stays, a ladder being refused under two rungs. Entries written by hand are
    /// never touched.
    /// </summary>
    /// <param name="document">The settings file being edited.</param>
    /// <param name="before">The settings before the change.</param>
    /// <param name="after">The settings after it.</param>
    /// <param name="renamedFrom">The name a renamed setting had; null when nothing was renamed.</param>
    /// <param name="renamedTo">The name it has now.</param>
    /// <returns>True when the document changed.</returns>
    public static bool Mirror(
        AppSettingsDocument document,
        ModelProfileSet before,
        ModelProfileSet after,
        string? renamedFrom = null,
        string? renamedTo = null) =>
        Mirror(document, before, after, renamedFrom is not null && renamedTo is not null ? [(renamedFrom, renamedTo)] : []);

    /// <summary>
    /// <see cref="Mirror(AppSettingsDocument, ModelProfileSet, ModelProfileSet, string?, string?)"/> for
    /// a change that renamed several settings at once — a renamed connection renames each of its pairs
    /// (<see cref="ConnectionUpsert.Renames"/>): each entry moves, with what names it.
    /// </summary>
    /// <param name="document">The settings file being edited.</param>
    /// <param name="before">The settings before the change.</param>
    /// <param name="after">The settings after it.</param>
    /// <param name="renames">Each renamed setting, its name before and after.</param>
    /// <returns>True when the document changed.</returns>
    public static bool Mirror(
        AppSettingsDocument document,
        ModelProfileSet before,
        ModelProfileSet after,
        IReadOnlyList<(string From, string To)> renames)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(renames);

        var was = Offered(before);
        var now = Offered(after);
        var section = document.Llm.Profiles;
        var changed = false;

        // Moved in two passes through ids no profile bears, so that two settings swapping their names
        // never write one entry over the other.
        var moves = renames
            .Select(rename => (Old: was.GetValueOrDefault(rename.From), New: now.GetValueOrDefault(rename.To)))
            .Where(move => move.Old is not null && move.New is not null && !string.Equals(move.Old, move.New, StringComparison.OrdinalIgnoreCase))
            .Select(move => (Old: move.Old!, New: move.New!))
            .ToList();
        var staged = new List<(string Staging, string New)>();
        foreach (var (oldId, newId) in moves)
        {
            var staging = moves.Count == 1 ? newId : $"{oldId}-moving-{staged.Count}";
            changed |= section.Rename(oldId, staging);
            changed |= MoveProfileReferences(document, oldId, staging);
            staged.Add((staging, newId));
        }

        foreach (var (staging, newId) in staged.Where(move => !string.Equals(move.Staging, move.New, StringComparison.Ordinal)))
        {
            changed |= section.Rename(staging, newId);
            changed |= MoveProfileReferences(document, staging, newId);
        }

        var offeredNow = new HashSet<string>(now.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var id in was.Values.Where(id => !offeredNow.Contains(id)))
        {
            changed |= section.Remove(id);
            changed |= MoveProfileReferences(document, id, null);
        }

        foreach (var (name, id) in now)
        {
            var profile = after.Find(name)!;
            var entry = profile.ToHostEntry(id);
            // Untouched — and healed: an entry written before it named its key's variable
            // (STUDIO-49), or whose key its card disagrees with — a Docker Model Runner entry
            // without its placeholder, another one with it (STUDIO-54) —, is written again at the
            // next gesture, never at startup.
            var untouched = before.Find(name) == profile
                && was.TryGetValue(name, out var previousId)
                && string.Equals(previousId, id, StringComparison.OrdinalIgnoreCase)
                && section.Get(id) is { } written
                && string.Equals(written.ApiKeyEnvVar, entry.ApiKeyEnvVar, StringComparison.Ordinal)
                && section.KeyAgrees(entry);
            if (!untouched)
                changed |= section.Set(entry);
        }

        return changed;
    }

    /// <summary>
    /// Writes the elected <paramref name="profile"/> into the <c>Llm</c> section, whole (STUDIO-49):
    /// every field it pins, a field it leaves unset removing its key, and the reference to its key's
    /// variable — so a run outside Studio follows the election with its timeout, its thinking
    /// switch and its key, as a host profile already did. The keys Studio does not model stay
    /// (<see cref="LlmSection.Set"/>), a clear-text <c>ApiKey</c> among them: it is not Studio's. Docker
    /// Model Runner's placeholder alone follows the card (STUDIO-54): an elected Docker Model Runner
    /// setting writes it where no key is set, any other takes it out — the file
    /// <c>orkeon init --preset docker-model-runner</c> wrote, then DeepSeek elected, included.
    /// </summary>
    /// <param name="document">The settings file being edited.</param>
    /// <param name="profile">The setting elected default.</param>
    /// <returns>True when the document changed.</returns>
    public static bool ElectDefault(AppSettingsDocument document, ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(profile);
        return document.Llm.Set(profile.ToHostEntry(LlmProfileNames.Default));
    }

    /// <summary>
    /// Heals the <c>Llm</c> section the election wrote before it named the key's variable
    /// (STUDIO-49), or whose key the elected setting's card disagrees with (STUDIO-54,
    /// <see cref="LlmSection.KeyAgrees"/>): when it lacks the reference the elected setting carries —
    /// or carries another —, or a Docker Model Runner setting's placeholder, or holds that placeholder
    /// under another card, it receives the elected setting whole, at the next gesture on the
    /// settings, never at startup. Otherwise nothing is written: only the election and an edit of the
    /// elected setting write the section, so a field edited by hand there stays.
    /// </summary>
    /// <param name="document">The settings file being edited.</param>
    /// <param name="set">The settings, the elected one among them.</param>
    /// <returns>True when the document changed.</returns>
    public static bool HealDefault(AppSettingsDocument document, ModelProfileSet set)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(set);

        if (set.Default is not { } profile)
            return false;

        var expected = profile.ToHostEntry(LlmProfileNames.Default);
        var healthy = string.Equals(document.Llm.ApiKeyEnvVar, expected.ApiKeyEnvVar, StringComparison.Ordinal)
            && document.Llm.KeyAgrees(expected);
        return !healthy && ElectDefault(document, profile);
    }

    /// <summary>
    /// The environment a launch lays over its child so that every setting offered to crews is the
    /// host profile it is in the file — endpoint, model, what it pins and its key, as
    /// <c>ORKEON_Llm__Profiles__&lt;id&gt;__*</c>, the key resolved through <paramref name="keys"/>, a
    /// Docker Model Runner setting's placeholder otherwise (STUDIO-54). A launch from Studio thus
    /// never depends on the settings file having been saved, nor on which settings file it reads; a
    /// setting whose key is not remembered rides without one.
    /// </summary>
    /// <param name="set">The settings.</param>
    /// <param name="keys">Reads a key variable by name — the key store.</param>
    public static IReadOnlyDictionary<string, string> LaunchEnvironment(ModelProfileSet set, Func<string, string?> keys)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(keys);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, id) in Offered(set))
        {
            foreach (var (key, value) in set.Find(name)!.HostEnvironment(id, keys))
                environment[key] = value;
        }

        return environment;
    }

    /// <summary>
    /// Points what names the profile <paramref name="from"/> to <paramref name="to"/>:
    /// <c>Orkeon:Rag:LlmProfile</c> (null: the default), <c>Orkeon:Compaction:Profile</c> (null: the
    /// agent's own model), and, for a rename only, the rungs of <c>Orkeon:Decision:Route:Ladder</c>.
    /// </summary>
    private static bool MoveProfileReferences(AppSettingsDocument document, string from, string? to)
    {
        bool Names(string? written) => string.Equals(written?.Trim(), from, StringComparison.OrdinalIgnoreCase);

        var changed = false;
        if (Names(document.Rag.LlmProfile))
        {
            document.Rag.LlmProfile = to;
            changed = true;
        }

        if (Names(document.Compaction.Profile))
        {
            document.Compaction.Profile = to;
            changed = true;
        }

        var ladder = document.Decision.RouteLadder;
        if (to is not null && ladder.Any(Names))
        {
            document.Decision.RouteLadder = [.. ladder.Select(rung => Names(rung) ? to : rung)];
            changed = true;
        }

        return changed;
    }
}
