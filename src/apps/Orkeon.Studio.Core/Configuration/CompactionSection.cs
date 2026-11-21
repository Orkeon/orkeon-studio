using Orkeon.Application.Crew.Compaction;
using Orkeon.Application.Interfaces.Ports;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// Typed view over <c>Orkeon:Compaction</c> and <c>Orkeon:Compaction:Prune</c>: what a run does when an
/// agent's context nears its model's window. Compaction, on by default, replaces the older turns with
/// a summary once the context fills <see cref="TriggerShare"/> of the window; pruning, off by default,
/// first asks the decision model which older tool results to cut. Every key is optional: absent, the
/// engine's default stands, and the defaults here are the engine's own constants. Every key is read and
/// written where the file spells it, in whatever letter case.
/// </summary>
public sealed class CompactionSection
{
    /// <summary>Configuration path of the section.</summary>
    public const string SectionPath = ContextCompactionOptions.SectionName;

    /// <summary>Configuration path of the pruning of tool results.</summary>
    public const string PrunePath = ToolResultPruningOptions.SectionName;

    /// <summary>Default of <c>TriggerShare</c>: the share of the window that starts a compaction.</summary>
    public const double DefaultTriggerShare = ContextCompactionOptions.DefaultTriggerShare;

    /// <summary>Default of <c>KeepRecentTurns</c>: the latest turns a compaction leaves as they are.</summary>
    public const int DefaultKeepRecentTurns = ContextCompactionOptions.DefaultKeepRecentTurns;

    /// <summary>Default of <c>Prune:FillShare</c>: the share of the window that starts a pruning.</summary>
    public const double DefaultPruneFillShare = ToolResultPruningOptions.DefaultFillShare;

    /// <summary>Default of <c>Prune:KeepRecentMessages</c>: the latest messages a pruning never judges.</summary>
    public const int DefaultPruneKeepRecentMessages = ToolResultPruningOptions.DefaultKeepRecentMessages;

    /// <summary>Default of <c>Prune:HeadChars</c>: the characters kept of a tool result that is cut.</summary>
    public const int DefaultPruneHeadChars = ToolResultPruningOptions.DefaultHeadChars;

    /// <summary>Whether the engine compacts when <c>Enabled</c> is absent: it does.</summary>
    public static bool DefaultEnabled { get; } = new ContextCompactionOptions().Enabled;

    /// <summary>Whether the engine prunes when <c>Prune:Enabled</c> is absent: it does not.</summary>
    public static bool DefaultPruneEnabled { get; } = new ToolResultPruningOptions().Enabled;

    // The key of the pruning's section under the compaction's.
    private static readonly string PruneKey = PrunePath[(SectionPath.Length + 1)..];

    private readonly AppSettingsDocument _document;

    internal CompactionSection(AppSettingsDocument document) => _document = document;

    /// <summary>True when the section carries at least one key, the pruning's included.</summary>
    public bool Exists => _document.SectionExists(_document.PathAsWritten(SectionPath));

    /// <summary>Whether a context is compacted (<c>Orkeon:Compaction:Enabled</c>); null lets the default stand.</summary>
    public bool? Enabled
    {
        get => _document.GetBoolean(Key(SectionPath, "Enabled"));
        set => _document.SetBoolean(Key(SectionPath, "Enabled"), value);
    }

    /// <summary>
    /// Whether a run of the file compacts, as the binder reads the key: the engine's default when it
    /// is absent or does not convert (which the start refuses), off when it is written without a value
    /// (a JSON null, an empty object), else what it says.
    /// </summary>
    public bool IsEnabled => SettingsBinding.Bind<ContextCompactionOptions>(DecisionSection.ToConfiguration(_document.Root), SectionPath, []).Enabled;

    /// <summary>The share of the window, above 0 and at most 1, that starts a compaction.</summary>
    public double? TriggerShare
    {
        get => _document.GetDouble(Key(SectionPath, "TriggerShare"));
        set => _document.SetDouble(Key(SectionPath, "TriggerShare"), value);
    }

    /// <summary>The latest turns a compaction leaves as they are, 0 or more.</summary>
    public int? KeepRecentTurns
    {
        get => _document.GetInt32(Key(SectionPath, "KeepRecentTurns"));
        set => _document.SetInt32(Key(SectionPath, "KeepRecentTurns"), value);
    }

    /// <summary>The model profile that writes the summary; null, the agent's own model does.</summary>
    public string? Profile
    {
        get => _document.GetString(Key(SectionPath, "Profile"));
        set => _document.SetString(Key(SectionPath, "Profile"), string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    /// <summary>Whether tool results are pruned (<c>Orkeon:Compaction:Prune:Enabled</c>); null lets the default stand.</summary>
    public bool? PruneEnabled
    {
        get => _document.GetBoolean(Key(PrunePath, "Enabled"));
        set => _document.SetBoolean(Key(PrunePath, "Enabled"), value);
    }

    /// <summary>Whether a run of the file prunes, read as <see cref="IsEnabled"/> is.</summary>
    public bool IsPruneEnabled => SettingsBinding.Bind<ToolResultPruningOptions>(DecisionSection.ToConfiguration(_document.Root), PrunePath, []).Enabled;

    /// <summary>The share of the window, above 0 and at most 1, that starts a pruning.</summary>
    public double? PruneFillShare
    {
        get => _document.GetDouble(Key(PrunePath, "FillShare"));
        set => _document.SetDouble(Key(PrunePath, "FillShare"), value);
    }

    /// <summary>The latest messages a pruning never judges, 0 or more.</summary>
    public int? PruneKeepRecentMessages
    {
        get => _document.GetInt32(Key(PrunePath, "KeepRecentMessages"));
        set => _document.SetInt32(Key(PrunePath, "KeepRecentMessages"), value);
    }

    /// <summary>The characters kept at the head of a tool result that is cut, 1 or more.</summary>
    public int? PruneHeadChars
    {
        get => _document.GetInt32(Key(PrunePath, "HeadChars"));
        set => _document.SetInt32(Key(PrunePath, "HeadChars"), value);
    }

    /// <summary>
    /// What the host's start refuses in the two sections, as the engine binds and validates them
    /// (<c>ContextCompactionOptions.Problems</c>, <c>ToolResultPruningOptions.Problems</c>): a file the
    /// configuration does not load, when the key it names is the sections'; a value the binder cannot
    /// convert; a share outside ]0, 1], a negative count, a <c>Profile</c> that is <c>auto</c>, each
    /// judged on the values that did convert; and a key that is no setting of its section, at any depth,
    /// or a share that is no finite number. Each with the path of its key, in the engine's sentence and
    /// the host's order; empty when the sections are accepted. A <c>Profile</c> the settings do not
    /// define is not here: see <see cref="UnknownProfile"/>.
    /// </summary>
    public IReadOnlyList<(string Path, string Problem)> Problems()
    {
        var configuration = DecisionSection.ToConfiguration(_document.Root);
        var problems = new List<(string, string)>();
        if (_document.LoadRefusal() is { } load && SettingsBinding.IsUnder(load.Path, SectionPath))
            problems.Add(load);
        var compaction = SettingsBinding.Bind<ContextCompactionOptions>(configuration, SectionPath, problems);
        problems.AddRange(ContextCompactionOptions.Problems(compaction).Select(problem => (SettingsBinding.PathOf(problem, SectionPath), problem)));
        if (LlmProfiles.IsAuto(compaction.Profile?.Trim()))
        {
            // The engine's own sentence (CompactionProfileProblem, internal to its composition root).
            var path = $"{SectionPath}:Profile";
            problems.Add((path, $"{path} is '{LlmProfiles.Auto}', which picks a profile per task and cannot write a summary: name a profile of Llm:Profiles, or leave the key out for the agent's own."));
        }

        var prune = SettingsBinding.Bind<ToolResultPruningOptions>(configuration, PrunePath, problems);
        problems.AddRange(ToolResultPruningOptions.Problems(prune).Select(problem => (SettingsBinding.PathOf(problem, PrunePath), problem)));

        // One walk of the section, the pruning's keys met where the keys' order puts them, as the host walks it.
        problems.AddRange(SettingsBinding.UnknownKeys(
            configuration, SectionPath, typeof(ContextCompactionOptions), (PruneKey, typeof(ToolResultPruningOptions))));
        return problems;
    }

    /// <summary>
    /// The engine's refusal of a <c>Profile</c> that neither the model profiles (<c>Llm:Profiles</c>) nor
    /// the decision profiles (<c>Orkeon:Decision:Profiles</c>) of this file define; null when it is
    /// defined, is the default, or is left out. A warning of the settings check and of the WPF card: a
    /// run launched from the WPF app is given Studio's model profiles through its environment, which
    /// this file does not show. The terminal launcher lays no profile, and makes it an error unless its
    /// own environment lays the profile.
    /// </summary>
    public (string Path, string Problem)? UnknownProfile()
    {
        var configuration = DecisionSection.ToConfiguration(_document.Root);
        var profile = configuration[$"{SectionPath}:Profile"]?.Trim();
        if (LlmProfiles.IsDefault(profile) || LlmProfiles.IsAuto(profile))
            return null;

        var offered = DecisionSection.ProfilesOf(configuration);
        var path = $"{SectionPath}:Profile";
        return offered.Contains(profile!, StringComparer.OrdinalIgnoreCase)
            ? null
            : (path, LlmProfiles.UnknownMessage(profile!, path, offered));
    }

    // Each key where the file spells it, in whatever letter case, as the configuration reads it.
    private string Key(string section, string key) => _document.PathAsWritten($"{section}:{key}");
}
