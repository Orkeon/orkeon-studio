using Orkeon.Application.Crew.Compaction;
using Orkeon.Application.Interfaces.Ports;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// Typed view over <c>Orkeon:Compaction</c> and <c>Orkeon:Compaction:Prune</c>: what a run does when an
/// agent's context nears its model's window. Compaction, on by default, replaces the older turns with
/// a summary once the context fills <see cref="TriggerShare"/> of the window; pruning, off by default,
/// first asks the decision model which older tool results to cut. Every key is optional: absent, the
/// engine's default stands, and the defaults here are the engine's own constants.
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

    private readonly AppSettingsDocument _document;

    internal CompactionSection(AppSettingsDocument document) => _document = document;

    /// <summary>True when the section carries at least one key, the pruning's included.</summary>
    public bool Exists => _document.SectionExists(SectionPath);

    /// <summary>Whether a context is compacted (<c>Orkeon:Compaction:Enabled</c>); null lets the default stand.</summary>
    public bool? Enabled
    {
        get => _document.GetBoolean($"{SectionPath}:Enabled");
        set => _document.SetBoolean($"{SectionPath}:Enabled", value);
    }

    /// <summary>Whether a run of the file compacts: the key, else the engine's default.</summary>
    public bool IsEnabled => Enabled ?? DefaultEnabled;

    /// <summary>The share of the window, above 0 and at most 1, that starts a compaction.</summary>
    public double? TriggerShare
    {
        get => _document.GetDouble($"{SectionPath}:TriggerShare");
        set => _document.SetDouble($"{SectionPath}:TriggerShare", value);
    }

    /// <summary>The latest turns a compaction leaves as they are, 0 or more.</summary>
    public int? KeepRecentTurns
    {
        get => _document.GetInt32($"{SectionPath}:KeepRecentTurns");
        set => _document.SetInt32($"{SectionPath}:KeepRecentTurns", value);
    }

    /// <summary>The model profile that writes the summary; null, the agent's own model does.</summary>
    public string? Profile
    {
        get => _document.GetString($"{SectionPath}:Profile");
        set => _document.SetString($"{SectionPath}:Profile", string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    /// <summary>Whether tool results are pruned (<c>Orkeon:Compaction:Prune:Enabled</c>); null lets the default stand.</summary>
    public bool? PruneEnabled
    {
        get => _document.GetBoolean($"{PrunePath}:Enabled");
        set => _document.SetBoolean($"{PrunePath}:Enabled", value);
    }

    /// <summary>Whether a run of the file prunes: the key, else the engine's default.</summary>
    public bool IsPruneEnabled => PruneEnabled ?? DefaultPruneEnabled;

    /// <summary>The share of the window, above 0 and at most 1, that starts a pruning.</summary>
    public double? PruneFillShare
    {
        get => _document.GetDouble($"{PrunePath}:FillShare");
        set => _document.SetDouble($"{PrunePath}:FillShare", value);
    }

    /// <summary>The latest messages a pruning never judges, 0 or more.</summary>
    public int? PruneKeepRecentMessages
    {
        get => _document.GetInt32($"{PrunePath}:KeepRecentMessages");
        set => _document.SetInt32($"{PrunePath}:KeepRecentMessages", value);
    }

    /// <summary>The characters kept at the head of a tool result that is cut, 1 or more.</summary>
    public int? PruneHeadChars
    {
        get => _document.GetInt32($"{PrunePath}:HeadChars");
        set => _document.SetInt32($"{PrunePath}:HeadChars", value);
    }

    /// <summary>
    /// What the host's start refuses in the two sections, as the engine binds and validates them
    /// (<c>ContextCompactionOptions.Problems</c>, <c>ToolResultPruningOptions.Problems</c>): a value the
    /// binder cannot convert, a share outside ]0, 1], a negative count, and a <c>Profile</c> that is
    /// <c>auto</c> or names no model profile of the settings. Each with the path of its key; empty when
    /// the sections are accepted.
    /// </summary>
    public IReadOnlyList<(string Path, string Problem)> Problems()
    {
        var configuration = DecisionSection.ToConfiguration(_document.Root);
        var problems = new List<(string, string)>();
        var compaction = new ContextCompactionOptions();
        var prune = new ToolResultPruningOptions();
        if (SettingsBinding.TryBind(configuration, SectionPath, compaction, problems))
        {
            problems.AddRange(ContextCompactionOptions.Problems(compaction).Select(problem => (SettingsBinding.PathOf(problem, SectionPath), problem)));
            if (compaction.Profile?.Trim() is { Length: > 0 } profile)
            {
                var path = $"{SectionPath}:Profile";
                if (LlmProfiles.IsAuto(profile))
                {
                    problems.Add((path, $"{path} is '{profile}', which System One picks per task: name a model profile, or remove the key so each agent's own model writes its summary."));
                }
                else if (!LlmProfiles.IsDefault(profile)
                    && !_document.Llm.Profiles.Ids.Contains(profile, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add((path, $"{path} is '{profile}', which is no model profile of these settings: name '{LlmProfiles.Default}' or a profile of {LlmProfilesSection.SectionPath}."));
                }
            }
        }

        if (SettingsBinding.TryBind(configuration, PrunePath, prune, problems))
            problems.AddRange(ToolResultPruningOptions.Problems(prune).Select(problem => (SettingsBinding.PathOf(problem, PrunePath), problem)));

        return problems;
    }

    /// <summary>Removes the whole section, the pruning's keys included.</summary>
    public void Remove() => _document.Remove(SectionPath);
}
