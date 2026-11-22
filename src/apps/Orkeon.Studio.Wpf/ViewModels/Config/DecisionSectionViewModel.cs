using System.Collections.Generic;
using System.Globalization;
using Orkeon.Infrastructure.Decision.SystemOne;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Llm;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;

namespace Orkeon.Studio.Wpf.ViewModels.Config;

/// <summary>One entry of the provider list: a provider's key, or null for the automatic choice.</summary>
/// <param name="Key">The value written to <c>Orkeon:Decision:Provider</c>; null removes it.</param>
/// <param name="Label">What the list shows.</param>
public sealed record DecisionProviderChoiceItem(string? Key, string Label);

/// <summary>
/// The decision models (System One) form: the circuit's switch (<c>Orkeon:Decision:Enabled</c>), the
/// provider's name (<c>Orkeon:Decision:Provider</c>, automatic when absent), one card per provider the
/// engine knows, the ladder of profiles and the thinking levels System One picks among
/// (<c>Orkeon:Decision:Route:Ladder</c>, <c>Orkeon:Decision:Effort:Levels</c>), and the status line a run
/// of the file gets, resolved as the engine resolves it
/// (<see cref="DecisionSection.Resolve"/>) over the keys in place. The keys go to the user environment
/// through <see cref="IApiKeyStore"/>, never to the file. Every field is read and written where the file
/// spells its key, in whatever letter case, as the configuration reads it.
/// </summary>
public sealed class DecisionSectionViewModel : DocumentSectionViewModel
{
    private readonly IStudioStrings _strings;
    private readonly IApiKeyStore _keyStore;

    /// <summary>Binds the form to the decision sections of the document.</summary>
    public DecisionSectionViewModel(
        Func<AppSettingsDocument> document, Action onChanged, IStudioStrings? strings = null, IApiKeyStore? keyStore = null)
        : base(document, onChanged)
    {
        _strings = strings ?? EnglishStudioStrings.Instance;
        _keyStore = keyStore ?? new EnvironmentApiKeyStore();
        Providers = DecisionSection.Providers
            .Select(descriptor => new DecisionProviderCardViewModel(this, descriptor, _keyStore, _strings))
            .ToList();
        AddRungCommand = new RelayCommand(AddRung, () => _rungToAdd is not null);
        _strings.CultureChanged += (_, _) => RefreshTexts();
    }

    private ProfileChoiceItem? _rungToAdd;

    /// <summary>
    /// The model settings the lists name, read at each use; set by the settings screen, which holds
    /// them. Left null, the lists offer the names the file defines alone.
    /// </summary>
    internal Func<ModelProfileSet>? ProfileSet { get; set; }

    /// <summary>The ladder, a row per rung, cheapest first: what <see cref="RouteLadderText"/> spells.</summary>
    public IReadOnlyList<LadderRungViewModel> Rungs
    {
        get
        {
            var ladder = Section.RouteLadder;
            var choices = AllProfileChoices();
            var unknown = Status.UnknownRungs;
            return
            [
                .. ladder.Select((rung, index) => new LadderRungViewModel(
                    this, index, rung, ProfileChoices.LabelOf(choices, rung),
                    unknown.Contains(rung, StringComparer.OrdinalIgnoreCase), index > 0, index < ladder.Count - 1)),
            ];
        }
    }

    /// <summary>Whether the ladder has no rung.</summary>
    public bool HasNoRung => Section.RouteLadder.Count == 0;

    /// <summary>The profiles a rung may name that the ladder does not name yet.</summary>
    public IReadOnlyList<ProfileChoiceItem> RungChoices
    {
        get
        {
            var ladder = Section.RouteLadder;
            return [.. AllProfileChoices().Where(choice => !ladder.Contains(choice.Id, StringComparer.OrdinalIgnoreCase))];
        }
    }

    /// <summary>The profile <see cref="AddRungCommand"/> appends.</summary>
    public ProfileChoiceItem? RungToAdd
    {
        get => _rungToAdd;
        set
        {
            if (SetProperty(ref _rungToAdd, value))
                AddRungCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Appends <see cref="RungToAdd"/> to the ladder, as its most expensive rung.</summary>
    public RelayCommand AddRungCommand { get; }

    private IReadOnlyList<ProfileChoiceItem> AllProfileChoices() =>
        ProfileChoices.Of(Document, ProfileSet?.Invoke(), withDefault: true);

    private void AddRung()
    {
        if (_rungToAdd is not { } choice)
            return;

        WriteLadder([.. Section.RouteLadder, choice.Id]);
        RungToAdd = null;
    }

    internal void MoveRung(int index, int offset)
    {
        var ladder = Section.RouteLadder.ToList();
        var target = index + offset;
        if (index < 0 || index >= ladder.Count || target < 0 || target >= ladder.Count)
            return;

        (ladder[index], ladder[target]) = (ladder[target], ladder[index]);
        WriteLadder(ladder);
    }

    internal void RemoveRung(int index)
    {
        var ladder = Section.RouteLadder.ToList();
        if (index < 0 || index >= ladder.Count)
            return;

        ladder.RemoveAt(index);
        WriteLadder(ladder);
    }

    private void WriteLadder(IReadOnlyList<string> ladder) => RouteLadderText = string.Join(", ", ladder);

    /// <summary>The model settings changed: the lists that name them say them again.</summary>
    internal void RefreshProfileChoices() => OnPropertiesChanged(nameof(Rungs), nameof(RungChoices));

    private DecisionSection Section => Document.Decision;

    /// <summary>The document the cards edit: the form's own, replaced with it.</summary>
    internal AppSettingsDocument DocumentForCards => Document;

    /// <inheritdoc />
    public override bool Exists => Section.Exists || Providers.Any(card => card.IsConfigured);

    /// <summary>Whether decision tasks ask the decision model; a switch turned back on removes its key.</summary>
    public bool Enabled
    {
        get => Section.IsEnabled;
        set => SetValue(Enabled, value, v => Section.Enabled = v ? null : false);
    }

    /// <summary>
    /// The automatic choice, then each provider the engine knows, then the name the file writes when the
    /// engine does not know it: shown as itself, so the list never hides what the run will refuse and the
    /// selection keeps it until another entry is picked.
    /// </summary>
    public IReadOnlyList<DecisionProviderChoiceItem> ProviderChoices
    {
        get
        {
            List<DecisionProviderChoiceItem> choices =
            [
                new(null, _strings[StudioStringKeys.SettingsDecisionProviderAuto]),
                .. DecisionSection.Providers.Select(d => new DecisionProviderChoiceItem(d.Key, d.DisplayName)),
            ];
            if (Section.Provider is { } written
                && !choices.Any(c => string.Equals(c.Key, written, StringComparison.OrdinalIgnoreCase)))
            {
                choices.Add(new DecisionProviderChoiceItem(written, written));
            }

            return choices;
        }
    }

    /// <summary>The entry of <see cref="ProviderChoices"/> that names the provider written in the file.</summary>
    public DecisionProviderChoiceItem SelectedProvider
    {
        get
        {
            var choices = ProviderChoices;
            var written = Section.Provider;
            if (written is null)
                return choices[0];
            return choices.First(c => string.Equals(c.Key, written, StringComparison.OrdinalIgnoreCase));
        }
        set
        {
            // A null comes from the list rebuilt under its selection, never from a pick: it changes nothing.
            if (value is null)
                return;
            var key = value.Key;
            if (string.Equals(key, Section.Provider, StringComparison.OrdinalIgnoreCase))
                return;
            Section.Provider = key;
            // A named provider is configured: its card shows its fields at once.
            if (key is not null && DecisionProviderDescriptor.Find(key) is { } descriptor)
                Section.For(descriptor).Create();
            // The name the engine does not know leaves the list once another entry replaces it.
            OnPropertiesChanged(nameof(ProviderChoices), nameof(SelectedProvider));
            NotifyDocumentChanged();
            foreach (var card in Providers)
                card.Refresh();
        }
    }

    /// <summary>
    /// The ladder System One picks a profile on, comma-separated, cheapest first; emptied, the key is
    /// removed. What the engine refuses in it is said by the status line.
    /// </summary>
    public string RouteLadderText
    {
        get => string.Join(", ", Section.RouteLadder);
        set => SetValue(RouteLadderText, Joined(value), v => Section.RouteLadder = DecisionSection.SplitList(v));
    }

    /// <summary>The rungs that name no profile of these settings, as a warning; empty when each names one.</summary>
    public string RouteLadderNote =>
        Status is { Refusal: null, UnknownRungs: { Count: > 0 } unknown }
            ? _strings.Format(StudioStringKeys.SettingsDecisionLadderUnknown, string.Join(", ", unknown))
            : string.Empty;

    /// <summary>
    /// The thinking levels System One picks among, comma-separated, cheapest first; emptied, the key is
    /// removed and the engine's own three stand.
    /// </summary>
    public string EffortLevelsText
    {
        get => string.Join(", ", Section.EffortLevels);
        set => SetValue(EffortLevelsText, Joined(value), v => Section.EffortLevels = DecisionSection.SplitList(v));
    }

    /// <summary>The levels the engine picks among when the list is empty: the field's watermark.</summary>
    public static string EffortLevelsDefault => string.Join(", ", DecisionSection.DefaultEffortLevels);

    /// <summary>How the levels are written, naming every level the engine knows.</summary>
    public string EffortLevelsHint => _strings.Format(
        StudioStringKeys.SettingsDecisionEffortLevelsHint,
        string.Join(", ", DecisionSection.KnownEffortLevels),
        EffortLevelsDefault);

    private static string Joined(string? text) => string.Join(", ", DecisionSection.SplitList(text));

    /// <summary>One card per provider, in the engine's order.</summary>
    public IReadOnlyList<DecisionProviderCardViewModel> Providers { get; }

    // The status as last resolved: a resolution reads the whole document and probes the environment
    // for each provider, so it is done once per change and never per bound property.
    private DecisionStatus? _status;

    /// <summary>
    /// The circuit a run of this file gets, over the keys in place: resolved when the section, the
    /// document or a key changes (<see cref="RefreshVerdict"/>), and kept until then.
    /// </summary>
    public DecisionStatus Status => _status ??= Section.Resolve(_keyStore.Peek);

    /// <summary>Whether a run of this file asks the decision model.</summary>
    public bool IsOn => Status.CircuitOn;

    /// <summary>Whether the engine refuses these settings at start.</summary>
    public bool IsRefused => Status.Refusal is not null;

    /// <summary>The status line: who answers the decision tasks, or why nobody does.</summary>
    public string StatusText => Describe(Status);

    /// <summary>The provider sections the run leaves aside, said under the status line; empty when none.</summary>
    public string IgnoredText =>
        Status.IgnoredSections is { Count: > 0 } ignored && Status.Refusal is null
            ? _strings.Format(StudioStringKeys.SettingsDecisionIgnored, string.Join(", ", ignored))
            : string.Empty;

    /// <summary>Whether <see cref="IgnoredText"/> has something to say.</summary>
    public bool HasIgnored => IgnoredText.Length > 0;

    /// <summary>The tab's subtitle: the provider in use, off, none, or to fix.</summary>
    public string TabSubtitle => Status switch
    {
        { Refusal: not null } => _strings[StudioStringKeys.SettingsTabDecisionRefused],
        { Enabled: false } => _strings[StudioStringKeys.SettingsTabDecisionOff],
        { Provider: { } provider } => provider.DisplayName,
        _ => _strings[StudioStringKeys.SettingsTabDecisionNone],
    };

    /// <summary>Warning on a refused choice, done while a provider answers, empty otherwise.</summary>
    public string TabState => Status switch
    {
        { Refusal: not null } => "Warning",
        { CircuitOn: true } => "Done",
        _ => "Empty",
    };

    /// <summary>A key was remembered, or a card rewrote its section: the status is read again.</summary>
    internal void OnCardChanged(bool documentChanged)
    {
        if (documentChanged)
            NotifyDocumentChanged();
        else
            OnSectionChanged();
    }

    /// <summary>
    /// Resolves the status again after a change made outside this form: an edit elsewhere in the
    /// document (a profile added, renamed or removed changes what a ladder rung names), or a key stored
    /// on another screen. What follows from the status is announced only when it changed.
    /// </summary>
    public void RefreshVerdict()
    {
        var resolved = Section.Resolve(_keyStore.Peek);
        if (_status is { } known && Same(known, resolved))
            return;

        _status = resolved;
        OnStatusChanged();
        RefreshProfileChoices();
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        // The document was replaced or rewritten: nothing kept of the one before, and every binding reads again.
        _status = null;
        base.Refresh();
        foreach (var card in Providers)
            card.Refresh();
    }

    /// <inheritdoc />
    protected override void OnSectionChanged()
    {
        OnPropertiesChanged(nameof(Exists), nameof(RouteLadderText), nameof(EffortLevelsText), nameof(HasNoRung));
        RefreshVerdict();
        RefreshProfileChoices();
    }

    // Everything the status decides, the status itself first: the shell judges the launchers again on it.
    private void OnStatusChanged() => OnPropertiesChanged(
        nameof(Status), nameof(IsOn), nameof(IsRefused), nameof(StatusText),
        nameof(IgnoredText), nameof(HasIgnored), nameof(TabSubtitle), nameof(TabState), nameof(RouteLadderNote));

    /// <summary>
    /// Whether two resolutions say the same thing. A record compares its lists by reference, and each
    /// resolution builds its own: they are compared item by item, then everything else as the record does.
    /// </summary>
    private static bool Same(DecisionStatus known, DecisionStatus resolved) =>
        known.IgnoredSections.SequenceEqual(resolved.IgnoredSections)
        && known.RouteLadder.SequenceEqual(resolved.RouteLadder)
        && known.UnknownRungs.SequenceEqual(resolved.UnknownRungs)
        && known.DecisionProfiles.SequenceEqual(resolved.DecisionProfiles)
        && known.BlockingProfiles.SequenceEqual(resolved.BlockingProfiles)
        && known == resolved with
        {
            IgnoredSections = known.IgnoredSections,
            RouteLadder = known.RouteLadder,
            UnknownRungs = known.UnknownRungs,
            DecisionProfiles = known.DecisionProfiles,
            BlockingProfiles = known.BlockingProfiles,
        };

    private void RefreshTexts()
    {
        OnPropertiesChanged(nameof(ProviderChoices), nameof(SelectedProvider), nameof(EffortLevelsHint));
        // The status is the same, its words are not.
        OnStatusChanged();
        foreach (var card in Providers)
            card.RefreshTexts();
    }

    private string Describe(DecisionStatus status) => status switch
    {
        { Refusal: { } refusal } => _strings.Format(StudioStringKeys.SettingsDecisionStatusRefused, refusal),
        { Enabled: false } => _strings[StudioStringKeys.SettingsDecisionStatusOff],
        { Provider: { } provider, How: DecisionProviderSelection.Named } =>
            _strings.Format(StudioStringKeys.SettingsDecisionStatusNamed, provider.DisplayName),
        { Provider: { } provider } => _strings.Format(StudioStringKeys.SettingsDecisionStatusDeduced, provider.DisplayName),
        _ => _strings[StudioStringKeys.SettingsDecisionStatusNone],
    };
}

/// <summary>
/// The card of one decision provider (<c>Orkeon:TypeSafe</c>, <c>Orkeon:Liquid</c>, <c>Orkeon:Kev</c>): set
/// up or removed whole, its fields blank for the engine's defaults, and the row of its key.
/// </summary>
public sealed class DecisionProviderCardViewModel : ObservableObject
{
    private readonly DecisionSectionViewModel _owner;
    private readonly IApiKeyStore _keyStore;
    private readonly IStudioStrings _strings;
    private SecretRowViewModel _key;

    internal DecisionProviderCardViewModel(
        DecisionSectionViewModel owner, DecisionProviderDescriptor descriptor, IApiKeyStore keyStore, IStudioStrings strings)
    {
        _owner = owner;
        Descriptor = descriptor;
        _keyStore = keyStore;
        _strings = strings;
        _key = NewKeyRow();
        SetUpCommand = new RelayCommand(SetUp, () => !IsConfigured);
        RemoveCommand = new RelayCommand(Remove, () => IsConfigured);
    }

    private DecisionProviderSettings Settings => _owner.DocumentForCards.Decision.For(Descriptor);

    /// <summary>The provider, as the engine describes it.</summary>
    public DecisionProviderDescriptor Descriptor { get; }

    /// <summary>The provider's name.</summary>
    public string Name => Descriptor.DisplayName;

    /// <summary>The section's path, shown in the card's title tooltip.</summary>
    public string SectionPath => Descriptor.Section;

    /// <summary>What the provider is: a hosted service that needs a key, or a local server.</summary>
    public string Kind => _strings[Descriptor.Hosted ? StudioStringKeys.SettingsDecisionHosted : StudioStringKeys.SettingsDecisionLocal];

    /// <summary>Whether the section is written: what the engine deduces a hosted provider from.</summary>
    public bool IsConfigured => Settings.Exists;

    /// <summary>
    /// How the engine comes to this provider, while it does not yet: a hosted one is deduced from its
    /// section or its key, a local server (Kev) never is and must be named. Empty once nothing is to say.
    /// </summary>
    public string Hint =>
        !Descriptor.Hosted && !IsNamed ? _strings[StudioStringKeys.SettingsDecisionNamedOnly]
        : Descriptor.Hosted && !IsConfigured ? _strings[StudioStringKeys.SettingsDecisionNotSetUp]
        : string.Empty;

    /// <summary>Whether <see cref="Hint"/> has something to say.</summary>
    public bool HasHint => Hint.Length > 0;

    private bool IsNamed => string.Equals(
        _owner.DocumentForCards.Decision.Provider, Descriptor.Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes the provider's section, empty: the engine gives its defaults.</summary>
    public RelayCommand SetUpCommand { get; }

    /// <summary>Removes the provider's section.</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>The API root; blank gives the provider's.</summary>
    public string? BaseAddress
    {
        get => Settings.BaseUrl;
        set => Write(Settings.BaseUrl, value, v => Settings.BaseUrl = v);
    }

    /// <summary>The watermark of <see cref="BaseAddress"/>.</summary>
    public string BaseAddressDefault => Descriptor.DefaultBaseUrl.ToString();

    /// <summary>The model; blank gives the provider's.</summary>
    public string? Model
    {
        get => Settings.Model;
        set => Write(Settings.Model, value, v => Settings.Model = v);
    }

    /// <summary>The watermark of <see cref="Model"/>.</summary>
    public string ModelDefault => Descriptor.DefaultModel;

    /// <summary>The time a request may take, in seconds; blank gives the engine's.</summary>
    public int? TimeoutSeconds
    {
        get => Settings.TimeoutSeconds;
        set => Write(Settings.TimeoutSeconds, value is > 0 ? value : null, v => Settings.TimeoutSeconds = v);
    }

    /// <summary>The watermark of <see cref="TimeoutSeconds"/>.</summary>
    public static string TimeoutSecondsDefault => DecisionSection.DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture);

    /// <summary>The retries of a failed request; blank gives the engine's.</summary>
    public int? MaxRetries
    {
        get => Settings.MaxRetries;
        set => Write(Settings.MaxRetries, value is >= 0 ? value : null, v => Settings.MaxRetries = v);
    }

    /// <summary>The watermark of <see cref="MaxRetries"/>.</summary>
    public static string MaxRetriesDefault => DecisionSection.DefaultMaxRetries.ToString(CultureInfo.InvariantCulture);

    /// <summary>The row of the provider's key, on the variable the run reads it from.</summary>
    public SecretRowViewModel Key => _key;

    /// <summary>Whether the provider takes a key at all: Kev, a local server, may go without one.</summary>
    public bool KeyRequired => Descriptor.KeyRequired;

    /// <summary>Whether a key is written in the file itself: said, since Studio keeps keys out of files.</summary>
    public bool HasApiKeyInFile => Settings.HasApiKeyInFile;

    /// <summary>Re-reads every field, after the document was replaced or rewritten.</summary>
    public void Refresh()
    {
        if (!string.Equals(_key.EnvName, Settings.EffectiveApiKeyEnvVar, StringComparison.Ordinal))
        {
            _key.Stored -= OnKeyStored;
            _key = NewKeyRow();
        }

        OnPropertyChanged(string.Empty);
        SetUpCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The language switched: the texts are read again.</summary>
    public void RefreshTexts()
    {
        _key.RefreshTexts();
        OnPropertiesChanged(nameof(Kind), nameof(Hint), nameof(HasHint));
    }

    private SecretRowViewModel NewKeyRow()
    {
        var row = new SecretRowViewModel(Settings.EffectiveApiKeyEnvVar, () => Descriptor.DisplayName, _keyStore, _strings);
        row.Stored += OnKeyStored;
        return row;
    }

    private void OnKeyStored(object? sender, EventArgs e) => _owner.OnCardChanged(documentChanged: false);

    private void SetUp()
    {
        Settings.Create();
        Refresh();
        _owner.OnCardChanged(documentChanged: true);
    }

    private void Remove()
    {
        Settings.Remove();
        Refresh();
        _owner.OnCardChanged(documentChanged: true);
    }

    private void Write<T>(T current, T value, Action<T> write, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return;
        write(value);
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(BaseAddress) or nameof(Model) or nameof(TimeoutSeconds) or nameof(MaxRetries))
        {
            OnPropertiesChanged(nameof(IsConfigured), nameof(Hint), nameof(HasHint));
            SetUpCommand.RaiseCanExecuteChanged();
            RemoveCommand.RaiseCanExecuteChanged();
        }

        _owner.OnCardChanged(documentChanged: true);
    }
}

/// <summary>One rung of the decision ladder: the profile it names, and the gestures that move or remove it.</summary>
public sealed class LadderRungViewModel
{
    internal LadderRungViewModel(DecisionSectionViewModel owner, int index, string id, string label, bool isUnknown, bool canMoveUp, bool canMoveDown)
    {
        Id = id;
        Label = label;
        IsUnknown = isUnknown;
        Position = index + 1;
        MoveUpCommand = new RelayCommand(() => owner.MoveRung(index, -1), () => canMoveUp);
        MoveDownCommand = new RelayCommand(() => owner.MoveRung(index, +1), () => canMoveDown);
        RemoveCommand = new RelayCommand(() => owner.RemoveRung(index));
    }

    /// <summary>The name the file writes.</summary>
    public string Id { get; }

    /// <summary>The setting's name when one offers the id (« mydeepseek.flash »), else the id.</summary>
    public string Label { get; }

    /// <summary>Whether <see cref="Label"/> differs from <see cref="Id"/>: the id is then shown beside it.</summary>
    public bool ShowsId => !string.Equals(Id, Label, StringComparison.Ordinal);

    /// <summary>Whether the rung names no profile of these settings: the engine refuses it (<see cref="DecisionSectionViewModel.RouteLadderNote"/>).</summary>
    public bool IsUnknown { get; }

    /// <summary>The rung's place, 1 for the cheapest.</summary>
    public int Position { get; }

    /// <summary>Swaps the rung with the cheaper one above it.</summary>
    public RelayCommand MoveUpCommand { get; }

    /// <summary>Swaps the rung with the dearer one under it.</summary>
    public RelayCommand MoveDownCommand { get; }

    /// <summary>Takes the rung out of the ladder.</summary>
    public RelayCommand RemoveCommand { get; }
}
