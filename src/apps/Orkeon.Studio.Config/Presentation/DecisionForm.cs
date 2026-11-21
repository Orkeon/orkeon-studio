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
/// list. A field left blank removes its key: the engine gives the provider's default. While a
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
    public const string AutomaticLabel = "(automatic — deduced from a TypeSafe or Liquid section or key)";

    /// <summary>The choices every file gets: the automatic choice, then each provider's key.</summary>
    public static IReadOnlyList<string> StandardChoices { get; } =
        new List<string> { AutomaticLabel }.Concat(DecisionSection.Providers.Select(d => d.Key)).ToList().AsReadOnly();

    /// <summary>
    /// The list the provider chooser shows: <see cref="StandardChoices"/>, then the name the file holds
    /// when the engine does not know it (<see cref="ProviderAsWritten"/>), labelled with the name itself.
    /// </summary>
    public IReadOnlyList<string> ProviderChoices =>
        ProviderAsWritten is { } written ? [.. StandardChoices, written] : StandardChoices;

    /// <summary>Where the keys come from: the environment, never this file.</summary>
    public static string KeysNotice { get; } =
        "Keys are read from the environment: " + string.Join(", ", DecisionSection.Providers.Select(d =>
            d.KeyRequired ? d.DefaultApiKeyVariable : d.DefaultApiKeyVariable + " (optional)"))
        + ". Kev, a local server, is never deduced: name it.";

    /// <summary>What this screen does not edit, and what unticking a section takes with it.</summary>
    public const string SectionsNotice =
        "Unticking a section removes it whole, with every key in it. Timeouts, retries and the key variable: "
        + "edit them in the JSON file itself, or in the WPF app.";

    /// <summary>The switch as written; null removes the key (on by default).</summary>
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

    /// <summary>What the two lists are for, and how they are written.</summary>
    public const string PicksNotice = "For llm.profile: auto and thinking effort auto. Commas, cheapest first.";

    /// <summary>The levels a list may name, and those that stand when it is blank.</summary>
    public static string LevelsNotice { get; } =
        "Levels: " + string.Join(", ", DecisionSection.KnownEffortLevels)
        + " (blank: " + string.Join(", ", DecisionSection.DefaultEffortLevels) + ").";

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
        Provider = choiceIndex >= 1 && choiceIndex < choices.Count ? choices[choiceIndex] : null;
    }

    /// <inheritdoc />
    public void LoadFrom(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var section = document.Decision;
        Enabled = section.Enabled;
        EnabledAsWritten = FieldText.UnreadableSwitch(document, section.EnabledPathAsWritten);
        Provider = section.Provider;
        ProviderAsWritten = Provider is { Length: > 0 } name && DecisionProviderDescriptor.Find(name) is null ? name : null;
        foreach (var fields in Providers)
            fields.LoadFrom(section.For(fields.Descriptor));
        RouteLadder = string.Join(", ", section.RouteLadder);
        EffortLevels = string.Join(", ", section.EffortLevels);
        Status = Describe(section.Resolve(_environment));
    }

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

        // The two lists, by the engine's own rules: what its start refuses is never written.
        errors.AddRange(DecisionSection.PickProblems(DecisionSection.SplitList(RouteLadder), DecisionSection.SplitList(EffortLevels)));
        if (errors.Count > 0)
            return errors;

        var section = document.Decision;
        section.Enabled = Enabled;
        section.Provider = FieldText.ToStringOrNull(Provider);
        foreach (var fields in Providers)
            fields.ApplyTo(section.For(fields.Descriptor));
        section.RouteLadder = DecisionSection.SplitList(RouteLadder);
        section.EffortLevels = DecisionSection.SplitList(EffortLevels);
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

        internal void LoadFrom(DecisionProviderSettings settings)
        {
            SetUp = settings.Exists;
            Model = FieldText.FromString(settings.Model);
            BaseAddress = FieldText.FromString(settings.BaseUrl);
        }

        internal void ApplyTo(DecisionProviderSettings settings)
        {
            if (!SetUp)
            {
                settings.Remove();
                return;
            }

            // A section the engine already sees (even null-valued) is kept as written: only the fields change it.
            if (!settings.Exists)
                settings.Create();
            settings.Model = FieldText.ToStringOrNull(Model);
            settings.BaseUrl = FieldText.ToStringOrNull(BaseAddress);
        }
    }
}
