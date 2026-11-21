using System.Globalization;
using Orkeon.Infrastructure.Decision.SystemOne;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Config.Presentation;

/// <summary>
/// The decision models (System One): the circuit's switch (<c>Orkeon:Decision:Enabled</c>, tri-state:
/// removing the key and writing <c>false</c> differ), the provider's name (<c>Orkeon:Decision:Provider</c>,
/// automatic when absent), and for each provider whether its section is written, its model and its
/// address, then the ladder of profiles and the thinking levels System One picks among
/// (<c>Orkeon:Decision:Route:Ladder</c>, <c>Orkeon:Decision:Effort:Levels</c>), each a comma-separated
/// list. Only what changed since the file was loaded is written: a field left as loaded keeps its key as
/// the file writes it (a list written as an object, a switch written as text), and a changed field left
/// blank removes its key, the engine then giving the provider's default. While a
/// provider's section stays ticked, every key the form does not show (its timeout, its retries, its key
/// variable, a key written by hand) is kept as it is, and so are the decision profiles; unticking the
/// section removes it whole, with every key in it. A provider name the engine does not know is shown as
/// written and kept, and refused at apply until another choice is picked, as the engine refuses it at start.
/// Every key is read and written where the file spells it, in whatever letter case
/// (<see cref="AppSettingsDocument.PathAsWritten"/>), as the configuration reads it.
/// </summary>
internal sealed class DecisionForm : ISettingsForm
{
    private readonly Func<string, string?> _environment;

    /// <summary>Builds the form; <paramref name="environment"/> reads the providers' keys, the process's when null.</summary>
    public DecisionForm(Func<string, string?>? environment = null)
    {
        _environment = environment ?? Environment.GetEnvironmentVariable;
        Providers = DecisionSection.Providers.Select(d => new ProviderFields(d)).ToList();
    }

    /// <inheritdoc />
    public string Title => "Decision models";

    /// <summary>Label of the automatic choice, offered first in the list.</summary>
    public const string AutomaticLabel = "(automatic: deduced from a section or key)";

    /// <summary>The choices every file gets: the automatic choice, then each provider's key.</summary>
    public static IReadOnlyList<string> StandardChoices { get; } =
        new List<string> { AutomaticLabel }.Concat(DecisionSection.Providers.Select(d => d.Key)).ToList().AsReadOnly();

    /// <summary>
    /// The list the provider chooser shows: <see cref="StandardChoices"/>, then the name the file holds
    /// when the engine does not know it (<see cref="ProviderAsWritten"/>), labelled with the name itself.
    /// </summary>
    public IReadOnlyList<string> ProviderChoices =>
        ProviderAsWritten is { } written ? [.. StandardChoices, written] : StandardChoices;

    /// <summary>
    /// Where the keys come from, one line each: the environment, never this file, under the variable
    /// each provider reads, and the one provider no key or section ever deduces.
    /// </summary>
    public static IReadOnlyList<string> KeyLines { get; } =
    [
        "Keys are read from the environment:",
        .. DecisionSection.Providers.Select(d =>
            "  " + d.DisplayName + ": " + (d.KeyRequired ? d.DefaultApiKeyVariable : d.DefaultApiKeyVariable + " (optional)")),
        "Kev is never deduced: name it.",
    ];

    /// <summary>What unticking a section takes with it.</summary>
    public const string SectionsNotice = "Unticked section: removed, every key in it.";

    /// <summary>What this screen does not edit.</summary>
    public const string JsonNotice = "Timeouts, retries, key variable: JSON file.";

    /// <summary>
    /// The switch: null leaves no key (on by default). A key the file writes without a value (a JSON
    /// null, an empty object) is shown as the run reads it, off, and stays as written until the switch
    /// is touched.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>The text the switch holds when it reads as no boolean, else null: kept and refused until it changes.</summary>
    public string? EnabledAsWritten { get; set; }

    /// <summary>The provider's name, or null for the automatic choice.</summary>
    public string? Provider { get; set; }

    /// <summary>The provider's name as the file loaded holds it, when the engine does not know it; else null.</summary>
    public string? ProviderAsWritten { get; private set; }

    /// <summary>The fields of each provider, in the engine's order.</summary>
    public IReadOnlyList<ProviderFields> Providers { get; }

    /// <summary>
    /// The profiles System One picks among for an agent on <c>llm.profile: auto</c>, comma-separated,
    /// cheapest first; blank removes the key, and a crew that asks for the pick is then refused at load.
    /// </summary>
    public string RouteLadder { get; set; } = "";

    /// <summary>
    /// The thinking levels System One picks among for <c>thinking: { effort: auto }</c>, comma-separated,
    /// cheapest first; blank removes the key, the engine's own three then standing.
    /// </summary>
    public string EffortLevels { get; set; } = "";

    /// <summary>What the two lists are for.</summary>
    public const string PicksNotice = "For llm.profile: auto and effort: auto.";

    /// <summary>How the two lists are written.</summary>
    public const string ListsNotice = "Commas between names, cheapest first.";

    /// <summary>The levels a list may name.</summary>
    public static string LevelsNotice { get; } = "Levels: " + string.Join(", ", DecisionSection.KnownEffortLevels) + ".";

    /// <summary>The levels that stand when the list is blank.</summary>
    public static string DefaultLevelsNotice { get; } = "No level: " + string.Join(", ", DecisionSection.DefaultEffortLevels) + ".";

    /// <summary>What a run of the file as loaded gets, resolved as the engine resolves it.</summary>
    public string Status { get; private set; } = "";

    /// <summary>
    /// Index of <see cref="Provider"/> in <see cref="ProviderChoices"/>: 0 for the automatic choice, the
    /// last row for <see cref="ProviderAsWritten"/>, and 0 for any other name the engine does not know.
    /// </summary>
    public int ProviderChoiceIndex
    {
        get
        {
            if (Provider is not { Length: > 0 } name)
                return 0;
            for (var i = 1; i < StandardChoices.Count; i++)
            {
                if (string.Equals(StandardChoices[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return string.Equals(name, ProviderAsWritten, StringComparison.Ordinal) ? StandardChoices.Count : 0;
        }
    }

    /// <summary>Selects a provider by its index in <see cref="ProviderChoices"/>.</summary>
    public void SelectProvider(int choiceIndex)
    {
        var choices = ProviderChoices;
        var picked = choiceIndex >= 1 && choiceIndex < choices.Count ? choices[choiceIndex] : null;
        // The row of the name the file already holds, in whatever letter case, changes nothing.
        if (!string.Equals(picked, Provider, StringComparison.OrdinalIgnoreCase))
            Provider = picked;
    }

    /// <inheritdoc />
    public void LoadFrom(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var section = document.Decision;
        // A key written without a value reads as no boolean and binds to false: shown off, as the run gets it.
        Enabled = section.Enabled ?? (section.IsEnabled ? null : false);
        // An empty object is such a key too: a value the run reads, off, not a text it refuses.
        EnabledAsWritten = Enabled is null ? FieldText.UnreadableSwitch(document, section.EnabledPathAsWritten) : null;
        Provider = section.Provider;
        ProviderAsWritten = Provider is { Length: > 0 } name && DecisionProviderDescriptor.Find(name) is null ? name : null;
        foreach (var fields in Providers)
            fields.LoadFrom(section.For(fields.Descriptor));
        RouteLadder = string.Join(", ", section.RouteLadder);
        EffortLevels = string.Join(", ", section.EffortLevels);
        _loaded = Current;
        Status = Describe(section.Resolve(_environment));
    }

    // The fields as the file gave them: a field left as loaded is not written again, so what a field
    // cannot show whole (a list the file writes as an object, a blank entry in it) stays as written.
    private Fields _loaded = new(null, null, null, "", "");

    private Fields Current => new(Enabled, EnabledAsWritten, Provider, RouteLadder, EffortLevels);

    private sealed record Fields(bool? Enabled, string? EnabledAsWritten, string? Provider, string RouteLadder, string EffortLevels);

    /// <inheritdoc />
    public IReadOnlyList<string> ApplyTo(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();
        FieldText.RefuseUnreadableSwitch(EnabledAsWritten, DecisionSection.EnabledPath, errors);
        if (Provider is { Length: > 0 } name && DecisionProviderDescriptor.Find(name) is null)
        {
            errors.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{DecisionSection.ProviderPath}: '{name}' is not one of {string.Join(", ", StandardChoices.Skip(1))}: pick one, or the automatic choice."));
        }

        // The two lists, by the engine's own rules: what its start refuses is never written. A list
        // left as loaded is judged as the file holds it, a blank entry included, not as the field shows it.
        var section = document.Decision;
        var typed = Current;
        var ladderChanged = typed.RouteLadder != _loaded.RouteLadder;
        var levelsChanged = typed.EffortLevels != _loaded.EffortLevels;
        var ladder = ladderChanged ? DecisionSection.SplitList(RouteLadder) : section.RouteLadder;
        var levels = levelsChanged ? DecisionSection.SplitList(EffortLevels) : section.EffortLevels;
        errors.AddRange(DecisionSection.PickProblems(ladder, levels));
        if (errors.Count > 0)
            return errors;

        if (typed.Enabled != _loaded.Enabled || typed.EnabledAsWritten != _loaded.EnabledAsWritten)
            section.Enabled = Enabled;
        if (typed.Provider != _loaded.Provider)
            section.Provider = FieldText.ToStringOrNull(Provider);
        foreach (var fields in Providers)
            fields.ApplyTo(section.For(fields.Descriptor));
        if (ladderChanged)
            section.RouteLadder = ladder;
        if (levelsChanged)
            section.EffortLevels = levels;
        _loaded = typed;
        Status = Describe(section.Resolve(_environment));
        return [];
    }

    private static string Describe(DecisionStatus status) => status switch
    {
        { Refusal: { } refusal } => "Refused at start: " + refusal,
        { Enabled: false } => "Off: every run goes without the decision models.",
        { Provider: { } provider, How: DecisionProviderSelection.Named } => provider.DisplayName + " answers the decision tasks.",
        { Provider: { } provider } => provider.DisplayName + " answers the decision tasks (deduced).",
        _ => "No provider: the runs go without the decision models.",
    };

    /// <summary>The fields of one provider's section.</summary>
    internal sealed class ProviderFields
    {
        internal ProviderFields(DecisionProviderDescriptor descriptor) => Descriptor = descriptor;

        /// <summary>The provider, as the engine describes it.</summary>
        public DecisionProviderDescriptor Descriptor { get; }

        /// <summary>
        /// Whether the section is written, as the engine sees it (a key without a value counts); unset, the
        /// section is removed whole, with the keys this screen does not show.
        /// </summary>
        public bool SetUp { get; set; }

        /// <summary>The model; blank gives the provider's.</summary>
        public string Model { get; set; } = "";

        /// <summary>The API root; blank gives the provider's.</summary>
        public string BaseAddress { get; set; } = "";

        // The two fields as the section gave them: one left as loaded is not written again.
        private (string Model, string BaseAddress) _loaded = ("", "");

        internal void LoadFrom(DecisionProviderSettings settings)
        {
            SetUp = settings.Exists;
            Model = FieldText.FromString(settings.Model);
            BaseAddress = FieldText.FromString(settings.BaseUrl);
            _loaded = (Model, BaseAddress);
        }

        internal void ApplyTo(DecisionProviderSettings settings)
        {
            if (!SetUp)
            {
                settings.Remove();
                // Nothing of it is left: ticked again, both fields are written anew.
                _loaded = ("", "");
                return;
            }

            // A section the engine already sees (even null-valued) is kept as written: only the fields change it.
            if (!settings.Exists)
                settings.Create();
            if (Model != _loaded.Model)
                settings.Model = FieldText.ToStringOrNull(Model);
            if (BaseAddress != _loaded.BaseAddress)
                settings.BaseUrl = FieldText.ToStringOrNull(BaseAddress);
            _loaded = (Model, BaseAddress);
        }
    }
}
