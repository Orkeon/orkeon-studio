using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Orkeon.Domain.SharedKernel.ValueObjects;
using Orkeon.Constants.Llm;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Llm;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Core.Presets;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Core.Teams;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;
using Orkeon.Studio.Wpf.ViewModels.Shell;
using System.Globalization;

namespace Orkeon.Studio.Wpf.ViewModels.Config;

/// <summary>
/// Payload of <see cref="ModelProfilesViewModel.HostProfilesChanged"/> (STUDIO-50): the setting
/// names whose host profile the change moved (<see cref="HostLlmProfiles.MovedNames"/>).
/// </summary>
public sealed class HostProfilesChangedEventArgs(IReadOnlySet<string> names) : EventArgs
{
    /// <summary>The names, as a team's companion file holds them.</summary>
    [SuppressMessage("Minor Code Smell", "S3604:Member initializer values should not be redundant",
        Justification = "False positive on a primary constructor: the initializer IS the only "
                      + "assignment of the member, and removing it would leave it unset.")]
    public IReadOnlySet<string> Names { get; } = names;
}

/// <summary>
/// Where a setting stands among the others, as its card says it: the default, the assistant's,
/// the teams naming it, and its host-profile verdict (null when it is not offered to crews).
/// <paramref name="FollowingTeams"/> counts, among the teams, those that follow the default (STUDIO-73).
/// </summary>
internal sealed record ModelProfileStanding(
    bool IsDefault,
    bool IsStudio,
    IReadOnlyList<string>? UsedByTeams = null,
    HostProfileCheck? Host = null,
    int FollowingTeams = 0);

/// <summary>
/// The seams the setting editor works over: the probe of the connection test, the key store, the
/// strings, the balances read this session and the opener of a vendor's console.
/// </summary>
internal sealed record ModelProfileEditorSeams(
    ILlmEndpointProbe Probe,
    IApiKeyStore KeyStore,
    IStudioStrings Strings,
    BalanceReadings? Balances = null,
    IShellOpener? Opener = null);

/// <summary>
/// What the model-settings tab is linked to beyond its store and its seams (STUDIO-35, STUDIO-52):
/// the teams naming a setting, the balances read this session, the opener of a vendor's console
/// and the hook a rename runs before it reaches the cards.
/// </summary>
public sealed record ModelProfilesLinks
{
    /// <summary>The teams of the catalogue, read for « Used by »; none when null.</summary>
    public Func<IReadOnlyList<TeamSummary>>? LoadTeams { get; init; }

    /// <summary>The provider balances read this session; left out, no row shows one.</summary>
    public BalanceReadings? Balances { get; init; }

    /// <summary>Opens a vendor's console from the editor.</summary>
    public IShellOpener? ShellOpener { get; init; }

    /// <summary>
    /// Called once with every setting the editor renames in one save, each former name with its new
    /// one: applied at once, each resolved from the names before the save, so two models swapping
    /// their names swap, and a chain moves each team once.
    /// </summary>
    public Action<IReadOnlyList<(string From, string To)>>? FollowRename { get; init; }
}

/// <summary>
/// One profile card on the model-settings tab. A thin projection over
/// <see cref="ModelProfile"/>; the commands are handed in by the list so every mutation goes
/// through one place.
/// </summary>
public sealed class ModelProfileItemViewModel : ObservableObject
{
    private readonly BalanceReadings? _balances;
    private readonly IStudioStrings _strings;
    private readonly HostProfileCheck? _host;

    internal ModelProfileItemViewModel(
        ModelProfile profile,
        ModelProfileStanding standing,
        ModelProfilesViewModel owner,
        BalanceReadings? balances = null,
        IStudioStrings? strings = null)
    {
        ArgumentNullException.ThrowIfNull(standing);
        Profile = profile;
        IsDefault = standing.IsDefault;
        IsStudio = standing.IsStudio;
        UsedByTeams = standing.UsedByTeams ?? [];
        FollowingTeams = standing.FollowingTeams;
        _balances = balances;
        _strings = strings ?? EnglishStudioStrings.Instance;
        _host = standing.Host;
        SetDefaultCommand = new RelayCommand(() => owner.SetDefault(profile.Name));
        EditCommand = new RelayCommand(() => owner.BeginEdit(profile));
        DuplicateCommand = new RelayCommand(() => owner.Duplicate(profile));
        DeleteCommand = new RelayCommand(
            () =>
            {
                // A setting teams name is removed only once the card said which ones lose it.
                if (UsedByTeams.Count > 0 && !IsConfirmingDelete)
                    IsConfirmingDelete = true;
                else
                    owner.Delete(profile.Name);
            },
            () => owner.CanDelete);
        ConfirmDeleteCommand = new RelayCommand(() => owner.Delete(profile.Name), () => owner.CanDelete);
        KeepCommand = new RelayCommand(() => IsConfirmingDelete = false);
    }

    private bool _isConfirmingDelete;

    /// <summary>Whether the card asks to confirm the removal of a setting teams name.</summary>
    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        private set
        {
            if (SetProperty(ref _isConfirmingDelete, value))
                OnPropertyChanged(nameof(DeleteWarning));
        }
    }

    /// <summary>The confirmation's line: the teams that would lose their setting.</summary>
    public string? DeleteWarning => _isConfirmingDelete
        ? _strings.Format(StudioStringKeys.ProfileDeleteUsedWarning, string.Join(", ", UsedByTeams))
        : null;

    /// <summary>Removes the setting the confirmation is about.</summary>
    public RelayCommand ConfirmDeleteCommand { get; }

    /// <summary>Keeps the setting: the confirmation closes.</summary>
    public RelayCommand KeepCommand { get; }

    /// <summary>The profile being shown.</summary>
    public ModelProfile Profile { get; }

    /// <summary>Display name.</summary>
    public string Name => Profile.Name;

    /// <summary>"Provider · model" one-liner — the card's title in the interface's language (STUDIO-54).</summary>
    public string Summary => Profile.Summary(_strings);

    /// <summary>Endpoint, shown in expert mode only.</summary>
    [SuppressMessage("Design", "CA1056",
        Justification = "Presentation of the profile's endpoint text, shown verbatim in a mono label.")]
    public string? Endpoint => Profile.BaseUrl;

    /// <summary>
    /// The title of the setting's card for the non-default badge, in the interface's language: the
    /// setting holds the card's name, never a title (STUDIO-54).
    /// </summary>
    public string Provider => LlmPresets.TitleFor(LlmPresets.CardOf(Profile), _strings);

    /// <summary>True for the elected default.</summary>
    public bool IsDefault { get; }

    /// <summary>True for the profile Studio's assistant uses.</summary>
    public bool IsStudio { get; }

    /// <summary>The adopted teams whose sidecar names this profile (audit 07/16 chips).</summary>
    public IReadOnlyList<string> UsedByTeams { get; }

    /// <summary>Whether the "used by" row shows.</summary>
    public bool IsUsedByTeams => UsedByTeams.Count > 0;

    /// <summary>How many of <see cref="UsedByTeams"/> follow the default rather than name this setting (STUDIO-73).</summary>
    public int FollowingTeams { get; }

    /// <summary>« 3 team(s), 2 by default » — on the elected default, when teams follow it; null otherwise.</summary>
    public string? UsedByCountLine => FollowingTeams > 0
        ? _strings.Format(StudioStringKeys.SettingsUsedByDefault, UsedByTeams.Count, FollowingTeams)
        : null;

    /// <summary>
    /// The host profile this setting is in the settings file — what a crew writes,
    /// <c>profile: &lt;id&gt;</c> (STUDIO-48); null when no crew can name it.
    /// </summary>
    public string? HostProfileId => _host is { IsOffered: true } host ? host.Id : null;

    /// <summary>The line under the name: the name a crew writes, or why no crew can write one; null for a setting without a model.</summary>
    public string? HostIdText => HostProfileText.Describe(_host, _strings);

    /// <summary>Whether the line shows.</summary>
    public bool HasHostIdText => HostIdText is not null;

    /// <summary>Whether the line is a problem — no crew can name this setting — shown in the warning tone.</summary>
    public bool IsHostIdIssue => HostProfileText.IsIssue(_host);

    /// <summary>
    /// The expert line of a setting that keeps its key in <c>ORKEON_Llm__ApiKey</c> without being
    /// the default (STUDIO-49, decision 7): that variable is the runtime's own key of the default,
    /// so every run of the user reads this key as its default's, whatever the endpoint — and how to
    /// take it back. The « Compatible OpenAI » card used that variable before; a setting created
    /// since keeps its key in <see cref="LlmPresets.CustomApiKeyEnv"/>. Null otherwise.
    /// </summary>
    public string? DefaultKeyVariableWarning =>
        !IsDefault && string.Equals(Profile.KeyEnvName?.Trim(), LlmPresets.DefaultApiKeyEnv, StringComparison.OrdinalIgnoreCase)
            ? _strings.Format(
                StudioStringKeys.ProfileDefaultKeyVariableWarning,
                LlmPresets.DefaultApiKeyEnv,
                LlmPresets.CustomApiKeyEnv)
            : null;

    /// <summary>Whether that line shows (in expert mode).</summary>
    public bool HasDefaultKeyVariableWarning => DefaultKeyVariableWarning is not null;

    /// <summary>
    /// What the profile's account has left, when a read of this session told it (STUDIO-35
    /// D-04): the amounts as the provider returned them; null when nothing was read, or the
    /// provider does not tell.
    /// </summary>
    public string? Balance => Reading is { Status: ProviderBalanceStatus.Available } reading
        ? BalanceText.Amounts(reading)
        : null;

    /// <summary>Whether the balance chip shows.</summary>
    public bool HasBalance => Balance is not null;

    /// <summary>Whether the balance is under its provider's alert threshold — the chip's warning tone (D-03).</summary>
    public bool IsBalanceLow => Reading is { } reading && _balances is not null && _balances.IsUnderThreshold(reading);

    /// <summary>The chip on hover: what the amount is, and when it was read.</summary>
    public string? BalanceTip => Reading is { } reading && _balances is not null
        ? BalanceText.Summary(reading, _balances, _strings)
        : null;

    /// <summary>Elects this profile as the machine default.</summary>
    public RelayCommand SetDefaultCommand { get; }

    /// <summary>Opens the editor over this profile.</summary>
    public RelayCommand EditCommand { get; }

    /// <summary>Adds a copy of this profile.</summary>
    public RelayCommand DuplicateCommand { get; }

    /// <summary>Removes this profile (disabled on the last one).</summary>
    public RelayCommand DeleteCommand { get; }

    private ProviderBalanceResult? Reading =>
        _balances is not null && ProviderBalanceAccount.HasAccount(Profile.BaseUrl)
            ? _balances.Of(ProviderBalanceAccount.For(Profile))
            : null;

    /// <summary>A read landed, or a threshold moved: the chip says it again.</summary>
    internal void RefreshBalance() =>
        OnPropertiesChanged(nameof(Balance), nameof(HasBalance), nameof(IsBalanceLow), nameof(BalanceTip));

    /// <summary>The language switched: the card's title, the host profile line and the key warning say it again.</summary>
    internal void RefreshTexts() =>
        OnPropertiesChanged(
            nameof(Summary), nameof(Provider), nameof(HostIdText), nameof(HasHostIdText), nameof(DefaultKeyVariableWarning));
}

/// <summary>
/// An entry of <c>Llm:Profiles</c> no setting owns — written by hand in the settings file
/// (STUDIO-48). Read-only by design: a crew can name it, Studio shows it and never changes it,
/// and the row offers no gesture.
/// </summary>
public sealed class HandWrittenProfileViewModel
{
    internal HandWrittenProfileViewModel(LlmProfileEntry entry, IStudioStrings strings)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(strings);

        Id = entry.Id;
        Summary = entry.Summary;
        HostIdText = HostProfileText.Describe(new HostProfileCheck(HostProfileStatus.Offered, entry.Id), strings)!;
    }

    /// <summary>The entry's key — the name a crew writes.</summary>
    public string Id { get; }

    /// <summary>The model and the endpoint the entry names.</summary>
    public string Summary { get; }

    /// <summary>Whether the summary line shows.</summary>
    public bool HasSummary => Summary.Length > 0;

    /// <summary>"In a crew file: profile: &lt;id&gt;".</summary>
    public string HostIdText { get; }
}

/// <summary>
/// One choice of the RAG's model picker (<c>Orkeon:Rag:LlmProfile</c>, STUDIO-48): a host profile,
/// or the default — <paramref name="Id"/> null.
/// </summary>
/// <param name="Id">The profile the RAG calls; null for the default profile.</param>
/// <param name="Label">What the picker shows.</param>
public sealed record RagProfileChoice(string? Id, string Label);

/// <summary>
/// The editor overlay for one profile (the new-profile / edit-profile form). Picking a
/// provider seeds the endpoint and model from the preset catalogue; expert mode exposes both
/// fields for hand-editing. The API key never appears here — it stays in the environment.
/// </summary>
/// <summary>One position of the profile editor's thinking switch: null is the provider's default.</summary>
/// <param name="Value">What the profile pins — null, true or false.</param>
/// <param name="Label">The localized label shown in the combo box.</param>
public sealed record ThinkingChoice(bool? Value, string Label);

/// <summary>One position of the profile editor's call mode.</summary>
/// <param name="Value">Auto, Buffer or Stream.</param>
/// <param name="Label">The localized label.</param>
public sealed record CallModeChoice(string Value, string Label);

public sealed class ModelProfileEditorViewModel : ObservableObject
{
    private readonly ModelProfilesViewModel _owner;
    private readonly ILlmEndpointProbe _probe;
    private readonly IApiKeyStore _keyStore;
    private readonly IStudioStrings _strings;
    private readonly BalanceReadings? _balances;
    private readonly IShellOpener? _opener;
    private LlmPresetInfo? _selectedProvider;
    private ProviderBalanceResult? _balanceReading;
    private bool _balanceKeyMissing;
    private string _name;
    private string? _baseUrl;
    private string? _workspaceId;
    private ConnectionModelRowViewModel _selectedRow = null!;
    private readonly IReadOnlyList<string> _previousPairs;
    private string? _apiKeyEnv;
    private string _apiKeyInput = "";
    private ProbeVerdict? _verdict;
    private string _modelFilter = "";
    private string? _keyStoreFailure;
    private bool _isTestingConnection;
    private bool _otherProvidersExpanded;
    private int _settingsGeneration;
    private string _temperatureText = "";
    private string _timeoutText = "";
    private string _streamIdleText = "";
    private string _maxTokensText = "";
    private bool? _thinkingEnabled;
    private string _callMode;
    private string _thinkingEffortText = "";
    private readonly IReadOnlyList<string> _originalHostIds;

    /// <summary>The model names the last successful connection test found (UC-01).</summary>
    public ObservableCollection<string> AvailableModels { get; } = [];

    /// <summary>Text narrowing <see cref="FilteredModels"/>: substring, case ignored.</summary>
    public string ModelFilter
    {
        get => _modelFilter;
        set
        {
            if (SetProperty(ref _modelFilter, value ?? ""))
                RefreshFilteredModels();
        }
    }

    /// <summary>The found names that contain <see cref="ModelFilter"/>, in the order the probe gave them.</summary>
    public IEnumerable<string> FilteredModels => _filteredModels;

    private IReadOnlyList<string> _filteredModels = [];

    /// <summary>Narrows <see cref="AvailableModels"/> to <see cref="ModelFilter"/> again, and says so.</summary>
    private void RefreshFilteredModels()
    {
        _filteredModels = AvailableModels.Where(name => name.Contains(_modelFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        OnPropertyChanged(nameof(FilteredModels));
    }

    /// <summary>True when the last test found names to list.</summary>
    public bool HasAvailableModels => AvailableModels.Count > 0;

    /// <summary>The "no list published" note: set when the last test succeeded without any name.</summary>
    public string? ModelListNote => _verdict is { Result: { Succeeded: true, ModelNames.IsDefaultOrEmpty: true } }
        ? _strings[StudioStringKeys.SettingsNoModelList]
        : null;

    /// <summary>Fills the selected model's identifier with the name carried as the parameter; saves nothing.</summary>
    public RelayCommand PickModelCommand { get; }

    internal ModelProfileEditorViewModel(
        ModelProfilesViewModel owner,
        IReadOnlyList<LlmPresetInfo> providers,
        ModelProfile profile,
        string? previousName,
        ModelProfileEditorSeams seams)
        : this(owner, providers, ModelConnection.Of(profile), previousName, seams)
    {
    }

    /// <summary>
    /// The editor over <paramref name="connection"/>: its shared fields, and a row per model, the one of
    /// alias <paramref name="selectedAlias"/> selected — else the first.
    /// </summary>
    internal ModelProfileEditorViewModel(
        ModelProfilesViewModel owner,
        IReadOnlyList<LlmPresetInfo> providers,
        ModelConnection connection,
        string? previousName,
        ModelProfileEditorSeams seams,
        string? selectedAlias = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        // The connection's own settings, read as the setting of its first model without what that model pins.
        var profile = (connection with { Models = null, Model = connection.DeclaredModels[0].Model }).Flatten()[0];
        ArgumentNullException.ThrowIfNull(seams);
        var strings = seams.Strings;
        _owner = owner;
        _probe = seams.Probe;
        _keyStore = seams.KeyStore;
        _strings = strings;
        _balances = seams.Balances;
        _opener = seams.Opener;
        Providers = providers;
        PreviousName = previousName;
        _name = profile.Name;
        _baseUrl = profile.BaseUrl;
        _workspaceId = profile.WorkspaceId;
        _apiKeyEnv = profile.KeyEnvName;
        _temperatureText = profile.Temperature is { } temperature
            ? temperature.ToString(CultureInfo.InvariantCulture)
            : "";
        _timeoutText = profile.TimeoutSeconds is { } timeout
            ? timeout.ToString(CultureInfo.InvariantCulture)
            : "";
        _streamIdleText = profile.StreamIdleSeconds is { } streamIdle
            ? streamIdle.ToString(CultureInfo.InvariantCulture)
            : "";
        _maxTokensText = profile.MaxTokens is { } maxTokens
            ? maxTokens.ToString(CultureInfo.InvariantCulture)
            : "";
        _thinkingEnabled = profile.ThinkingEnabled;
        _thinkingEffortText = profile.ThinkingEffort ?? "";
        ThinkingChoices = BuildThinkingChoices(strings);
        ModelThinkingChoices = BuildModelThinkingChoices(strings);
        CallModeChoices = BuildCallModeChoices(strings);
        _previousPairs = previousName is null ? [] : connection.PairNames;
        foreach (var model in connection.DeclaredModels)
        {
            var pair = previousName is null ? null : ModelConnection.PairName(connection.Name, model.Alias);
            Models.Add(new ConnectionModelRowViewModel(this, model, pair, pair is null ? [] : owner.TeamsNaming(pair)));
        }

        SelectRow(Models.FirstOrDefault(row => string.Equals(row.Alias.Trim(), selectedAlias?.Trim(), StringComparison.Ordinal)) ?? Models[0]);
        _callMode = CallModeChoices.FirstOrDefault(c => string.Equals(c.Value, profile.CallMode, StringComparison.OrdinalIgnoreCase))?.Value ?? "Auto";
        // STUDIO-54: the card by its name — a title changes with the language —, recognised from
        // whatever an older setting holds; a setting always has one.
        var card = LlmPresets.CardOf(profile);
        _selectedProvider = providers.FirstOrDefault(p => string.Equals(p.Name, card, StringComparison.Ordinal));
        _otherProvidersExpanded = IsOtherProvider(_selectedProvider);
        // The ids crews may already write: the ones the connection's models were offered under when it opened.
        _originalHostIds = [.. _previousPairs.Select(owner.OfferedHostId).OfType<string>()];

        SaveCommand = new RelayCommand(Save, () => CanSave);
        CancelCommand = new RelayCommand(() => _owner.CancelEdit());
        SelectProviderCommand = new RelayCommand(p => SelectedProvider = p as LlmPresetInfo);
        PickModelCommand = new RelayCommand(name => { if (name is string picked) PickModel(picked); });
        AddModelCommand = new RelayCommand(AddModel);
        TestConnectionCommand = new AsyncRelayCommand(
            () => TestConnectionAsync(CancellationToken.None), () => !_isTestingConnection);
        StoreKeyCommand = new AsyncRelayCommand(StoreKeyAsync, () => _apiKeyInput.Trim().Length > 0);
        ReadBalanceCommand = new AsyncRelayCommand(() => ReadBalanceAsync(CancellationToken.None));
        OpenBalanceConsoleCommand = new RelayCommand(
            () => _opener?.Open(_balanceReading!.ConsoleUrl!.AbsoluteUri),
            () => BalanceOffersConsole && _opener is not null);

        // The line opens on what this session already read for the profile's account, if anything.
        if (_balances is not null && ProviderBalanceAccount.HasAccount(profile.BaseUrl))
            ShowBalance(_balances.Of(ProviderBalanceAccount.For(profile)));
    }

    /// <summary>
    /// The provider choices — the same catalogue `orkeon init` offers, in the interface's language:
    /// rebuilt when it switches (<see cref="RefreshTexts"/>).
    /// </summary>
    public IReadOnlyList<LlmPresetInfo> Providers { get; private set; }

    /// <summary>The name under which the profile was opened; null when creating.</summary>
    public string? PreviousName { get; }

    /// <summary>True when this editor creates a profile rather than reworking one.</summary>
    public bool IsNew => PreviousName is null;

    /// <summary>Profile name, the identity teams reference.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(NameCollision));
                RefreshRows();
                OnHostProfileChanged();
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The picked provider; picking one seeds the endpoint and the model.</summary>
    public LlmPresetInfo? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            var previous = _selectedProvider;
            if (!SetProperty(ref _selectedProvider, value) || value is null)
                return;

            if (IsOtherProvider(value))
                OtherProvidersExpanded = true;      // the picked card never hides in a closed fold
            BaseUrl = value.DefaultBaseUrl;
            Model = value.DefaultModel;
            _apiKeyEnv = value.DefaultApiKeyEnv;
            SeedTimeoutFor(value, previous);
            OnPropertyChanged(nameof(TimeoutHint));
            ShowVerdict(null);
            ReplaceAvailableModels([]);   // the catalogue was about the previous provider
            ShowBalance(null);
            OnPropertyChanged(nameof(RequiresApiKey));
            OnPropertyChanged(nameof(ApiKeyEnvName));
            OnPropertyChanged(nameof(HostKeyHint));     // the card's own variable (STUDIO-49)
            OnPropertyChanged(nameof(HasStoredKey));
            OnPropertyChanged(nameof(KeyStatusText));
            OnPropertyChanged(nameof(KeyBlockTitle));
            OnPropertyChanged(nameof(KeyConsoleUrl));
            OnPropertyChanged(nameof(IsNone));
            OnPropertyChanged(nameof(ShowFields));
            OnPropertyChanged(nameof(UrlAlwaysVisible));
            OnPropertyChanged(nameof(ShowLocalNote));
            OnPropertyChanged(nameof(EndpointReadingLine));
            OnPropertyChanged(nameof(ShowNoneNote));
            OnPropertyChanged(nameof(ShowTestRow));
            OnPropertyChanged(nameof(ShowBalanceRow));
            OnPropertyChanged(nameof(MaxTokensHint));   // an entry can hold on one provider only (LLM-10)
            OnHostProfileChanged();                     // « no model » is offered to no crew
            OnPropertyChanged(nameof(CanSave));
            SaveCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Endpoint base URL (expert field).</summary>
    [SuppressMessage("Design", "CA1056",
        Justification = "User-typed form value round-tripped verbatim into a JSON string field; " +
                        "a half-typed URL must be carried and re-shown, which System.Uri cannot do.")]
    public string? BaseUrl
    {
        get => _baseUrl;
        set
        {
            // The catch-all provider gates Save on this field: the button must wake up
            // as the URL is typed, not on the next unrelated notification.
            if (!SetProperty(ref _baseUrl, value))
                return;

            OnHostProfileChanged();
            SaveCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(EndpointReadingLine));
            OnPropertyChanged(nameof(ShowLocalNote));
            OnPropertyChanged(nameof(ShowWorkspace));
            ClearConnectionTestResult();   // the verdict was about the previous endpoint
            ReplaceAvailableModels([]);
        }
    }

    /// <summary>
    /// The Anthropic workspace the key acts in (<c>WorkspaceId</c>, Orkeon #468): an identity-linked key
    /// refuses every call without it. Not a secret; empty for every other key.
    /// </summary>
    public string? WorkspaceId
    {
        get => _workspaceId;
        set
        {
            if (!SetProperty(ref _workspaceId, value))
                return;

            OnPropertyChanged(nameof(ShowWorkspace));
            ClearConnectionTestResult();   // the verdict was about the previous workspace
        }
    }

    /// <summary>
    /// Whether the workspace field shows: for an address a run reads as Anthropic, the one provider
    /// that sends it, and for a setting that holds one, so it can be cleared.
    /// </summary>
    public bool ShowWorkspace =>
        !string.IsNullOrWhiteSpace(_workspaceId)
        || StudioEndpointReading.ProviderOf(_baseUrl) == LlmProviderKeys.Anthropic;

    /// <summary>The selected model's identifier (expert field): the model of the row the test is about.</summary>
    public string? Model
    {
        get => _selectedRow.Model;
        set => _selectedRow.Model = value;
    }

    /// <summary>The connection's models, in order: at least one.</summary>
    public ObservableCollection<ConnectionModelRowViewModel> Models { get; } = [];

    /// <summary>
    /// The model the connection test, the list of found models and the response-budget hint are about;
    /// picking a found model fills its identifier.
    /// </summary>
    public ConnectionModelRowViewModel SelectedModelRow
    {
        get => _selectedRow;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedRow) || !Models.Contains(value))
                return;

            SelectRow(value);
            OnPropertiesChanged(nameof(SelectedModelRow), nameof(Model), nameof(MaxTokensHint));
            ClearConnectionTestResult();   // the verdict was about the previous model
        }
    }

    /// <summary>Whether the connection declares several models: each then needs an alias, and shows its own id.</summary>
    public bool HasSeveralModels => Models.Count > 1;

    /// <summary>Adds an empty model to the connection and selects it, for the next pick or the typed identifier.</summary>
    public RelayCommand AddModelCommand { get; }

    /// <summary>The thinking choices of a model: the connection's, on, off.</summary>
    public IReadOnlyList<ThinkingChoice> ModelThinkingChoices { get; private set; }

    internal IStudioStrings Strings => _strings;

    private void SelectRow(ConnectionModelRowViewModel row)
    {
        if (_selectedRow is not null)
            _selectedRow.IsSelected = false;
        _selectedRow = row;
        row.IsSelected = true;
    }

    private void AddModel()
    {
        // The first model takes an alias as the second arrives: two models of one connection need two names.
        if (Models.Count == 1 && Models[0].Alias.Trim().Length == 0)
            Models[0].Alias = AliasFor(Models[0].Model) ?? "1";

        var row = new ConnectionModelRowViewModel(this, new ConnectionModel(), previousPairName: null, usedBy: []);
        Models.Add(row);
        SelectedModelRow = row;
        OnModelRowsChanged();
    }

    internal void RemoveModel(ConnectionModelRowViewModel row)
    {
        if (Models.Count <= 1 || !Models.Remove(row))
            return;

        if (ReferenceEquals(row, _selectedRow))
            SelectedModelRow = Models[0];
        OnModelRowsChanged();
    }

    private void PickModel(string picked)
    {
        _selectedRow.Model = picked;
        if (Models.Count > 1 && _selectedRow.Alias.Trim().Length == 0)
            _selectedRow.Alias = AliasFor(picked) ?? "";
    }

    /// <summary>The alias a picked identifier suggests: its last segment, « deepseek-v4-flash » for « deepseek/deepseek-v4-flash ».</summary>
    private static string? AliasFor(string? model)
    {
        var name = model?.Trim();
        if (string.IsNullOrEmpty(name))
            return null;

        var slash = name.LastIndexOf('/');
        return slash >= 0 && slash < name.Length - 1 ? name[(slash + 1)..] : name;
    }

    /// <summary>A row's alias, budget or settings changed: every row and the save say it again.</summary>
    internal void OnModelRowsChanged()
    {
        RefreshRows();
        OnPropertiesChanged(nameof(HasSeveralModels), nameof(NameCollision), nameof(CanSave));
        OnHostProfileChanged();
        SaveCommand.RaiseCanExecuteChanged();
    }

    /// <summary>A row's model or thinking switch changed: what the test and the hints read follows when it is the selected one.</summary>
    internal void OnModelRowChanged(ConnectionModelRowViewModel row)
    {
        OnModelRowsChanged();
        if (!ReferenceEquals(row, _selectedRow))
            return;

        OnPropertiesChanged(nameof(Model), nameof(MaxTokensHint));   // the hint follows the model, not the provider
        ClearConnectionTestResult();   // the test completion ran on the previous model
    }

    private void RefreshRows()
    {
        foreach (var row in Models)
        {
            row.Refresh();
            row.RaiseCanRemove();
        }
    }

    /// <summary>Whether a setting of another connection bears <paramref name="pairName"/>, or another connection the connection's name.</summary>
    internal bool IsPairNameTaken(string pairName) => _owner.IsPairNameTaken(pairName, PreviousName);

    /// <summary>What <paramref name="row"/>'s pair would be as a host profile, against the settings, the file and the rows above it.</summary>
    internal HostProfileCheck CheckPair(ConnectionModelRowViewModel row)
    {
        var above = Models.TakeWhile(other => !ReferenceEquals(other, row)).Select(other => other.PairName).Where(name => name.Length > 0).ToList();
        return _owner.CheckHostProfile(
            row.PairName.Length > 0 ? row.PairName : _name,
            _previousPairs,
            above,
            !string.IsNullOrWhiteSpace(row.Model) || !string.IsNullOrWhiteSpace(_baseUrl));
    }

    internal static int? PositiveInt(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;

    internal static double? FiniteDouble(string text) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;

    /// <summary>True when the picked provider authenticates requests.</summary>
    public bool RequiresApiKey => _selectedProvider?.RequiresApiKey == true;

    /// <summary>
    /// Name of the environment variable the key lives in — the profile's own when set,
    /// else the provider's conventional one, else the runtime's native variable.
    /// </summary>
    public string ApiKeyEnvName =>
        _apiKeyEnv is { Length: > 0 } explicitName
            ? explicitName
            : _selectedProvider?.DefaultApiKeyEnv ?? LlmPresets.DefaultApiKeyEnv;

    /// <summary>
    /// The pasted key, held only until it is remembered. Never pre-filled from the stored
    /// value — the editor shows whether a key is in place, not what it is.
    /// </summary>
    public string ApiKeyInput
    {
        get => _apiKeyInput;
        set
        {
            if (SetProperty(ref _apiKeyInput, value))
                StoreKeyCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The provider's card groups, for the three sections of the design.</summary>
    public IEnumerable<LlmPresetInfo> LocalProviders => Providers.Where(p => p.Kind == LlmPresetKind.Local);

    /// <inheritdoc cref="LocalProviders" />
    public IEnumerable<LlmPresetInfo> CloudProviders => Providers.Where(p => p.Kind == LlmPresetKind.Cloud);

    /// <inheritdoc cref="LocalProviders" />
    public IEnumerable<LlmPresetInfo> OtherProviders =>
        Providers.Where(p => p.Kind is LlmPresetKind.Other or LlmPresetKind.None);

    /// <summary>
    /// Whether the « Other providers » fold is open: closed by default (UX lot 04, 063), but open
    /// whenever the picked card is one of <see cref="OtherProviders"/>, so the selection is in sight.
    /// The user may still close it by hand.
    /// </summary>
    public bool OtherProvidersExpanded
    {
        get => _otherProvidersExpanded;
        set => SetProperty(ref _otherProvidersExpanded, value);
    }

    private static bool IsOtherProvider(LlmPresetInfo? provider) =>
        provider?.Kind is LlmPresetKind.Other or LlmPresetKind.None;

    /// <summary>True when the picked card is the echo fallback.</summary>
    public bool IsNone => _selectedProvider?.Kind == LlmPresetKind.None;

    /// <summary>URL/model fields are pointless without a model.</summary>
    public bool ShowFields => !IsNone;

    /// <summary>The catch-all card needs the URL from every user, not only experts.</summary>
    public bool UrlAlwaysVisible => _selectedProvider?.Kind == LlmPresetKind.Other;

    /// <summary>
    /// The line under the address: how a run reads it, with the reading's notice when it has one — or,
    /// on an Ollama card whose address is read otherwise, why that card cannot serve it (CH-08, CH-09).
    /// Empty for a blank address.
    /// </summary>
    public string EndpointReadingLine
    {
        get
        {
            if (StudioEndpointReading.Of(BaseUrl) is not { } reading)
                return string.Empty;

            if (OllamaReadElsewhere)
                return _strings.Format(StudioStringKeys.ProfileOllamaReadElsewhere, reading.ProviderKey);

            var line = _strings.Format(StudioStringKeys.ProfileEndpointReadAs, reading.ProviderKey);
            return reading.Notice == LlmEndpointNotice.None
                ? line
                : line + " " + _strings[EndpointNoticeKey];
        }
    }

    private const string EndpointNoticeKey = "Studio.Diagnostics.Code.STUDIO-LLM-ENDPOINT";

    /// <summary>True when an Ollama card holds an address a run reads as another provider: nothing is probed.</summary>
    private bool OllamaReadElsewhere =>
        ModelTest.IsOllamaReadElsewhere(_selectedProvider?.Name, BaseUrl);

    /// <summary>"No key needed — the model runs on your machine."</summary>
    public bool ShowLocalNote => _selectedProvider?.Kind == LlmPresetKind.Local && !OllamaReadElsewhere;

    /// <summary>"Without a model, runs answer as an echo."</summary>
    public bool ShowNoneNote => IsNone;

    /// <summary>The probe row makes no sense for the echo fallback.</summary>
    public bool ShowTestRow => _selectedProvider is not null && !IsNone;

    /// <summary>
    /// The balance line beside « Test » (STUDIO-35 D-04): for a card with a key, hence an
    /// account to ask, and only when a balance probe is wired.
    /// </summary>
    public bool ShowBalanceRow => ShowTestRow && RequiresApiKey && _balances is { CanRead: true };

    /// <summary>
    /// What the last balance read said for this endpoint — never the key; null before any. Formed
    /// as it is read, from the reading or the missing key, so it follows the language (STUDIO-56).
    /// </summary>
    public string? BalanceResult
    {
        get
        {
            if (_balanceKeyMissing)
                return _strings[StudioStringKeys.ProfileKeyMissingTest];
            if (_balanceReading is { } reading && _balances is not null)
                return BalanceText.Summary(reading, _balances, _strings);
            return null;
        }
    }

    /// <summary>The probe's own line on hover, in English: it is the raw evidence (STUDIO-33), like the cause quoted in the connection test's line.</summary>
    public string? BalanceDetail => _balanceReading?.Detail;

    /// <summary>Whether the balance read is under its provider's alert threshold (D-03).</summary>
    public bool IsBalanceLow => _balanceReading is { } reading && _balances is not null && _balances.IsUnderThreshold(reading);

    /// <summary>Whether the provider shows the balance in its console only — the line then offers to open it (D-01).</summary>
    public bool BalanceOffersConsole => _balanceReading is { } reading && BalanceText.OffersConsole(reading);

    /// <summary>True when a key is already in place under the profile's variable.</summary>
    public bool HasStoredKey => _keyStore.Peek(ApiKeyEnvName) is not null;

    /// <summary>
    /// Why the last remembered key was not kept for the next sessions (STUDIO-44): the key is in
    /// place for this one all the same. Null when the last write went through. The cause is kept,
    /// the line formed as it is read (STUDIO-56).
    /// </summary>
    public string? KeyStoreError => _keyStoreFailure is { } cause
        ? _strings.Format(StudioStringKeys.ProfileKeyPersistFailed, cause)
        : null;

    private void ShowKeyStoreFailure(string? cause)
    {
        _keyStoreFailure = cause;
        OnPropertyChanged(nameof(KeyStoreError));
    }

    /// <summary>Status chip of the key block: remembered, or not detected yet.</summary>
    public string KeyStatusText =>
        _strings[HasStoredKey ? StudioStringKeys.ProfileKeyStatusSet : StudioStringKeys.ProfileKeyStatusMissing];

    /// <summary>"API key {provider}" — or the generic service wording for the catch-all.</summary>
    public string KeyBlockTitle =>
        _selectedProvider?.Kind == LlmPresetKind.Other
            ? _strings[StudioStringKeys.ProfileKeyTitleService]
            : _strings.Format(
                StudioStringKeys.ProfileKeyTitleFor,
                _selectedProvider?.Title ?? "");

    /// <summary>Where to get a key, when the vendor has a console we can name.</summary>
    [SuppressMessage("Design", "CA1056",
        Justification = "Display text: a bare host/path shown as a hint, or a localized " +
                        "'on the provider's site' fallback — not a navigable Uri.")]
    public string KeyConsoleUrl =>
        _selectedProvider?.KeyConsoleUrl ?? _strings[StudioStringKeys.ProfileKeyOnVendorSite];

    /// <summary>
    /// A profile needs a name of its own; the catch-all additionally needs the endpoint
    /// and the model typed in.
    /// </summary>
    public bool CanSave =>
        _name.Trim().Length > 0
        && !NameCollision
        // STUDIO-48: « default » is the Llm section's own name, and an id another setting or a
        // hand-written entry holds would make two profiles one.
        && !HostProfile.BlocksSave
        && Models.All(row => row.IsValid)
        && !(UrlAlwaysVisible
             && (string.IsNullOrWhiteSpace(_baseUrl) || Models.Any(row => string.IsNullOrWhiteSpace(row.Model))))
        // STUDIO-55: an address the settings screen would then refuse — not an absolute http(s) URL.
        && (string.IsNullOrWhiteSpace(_baseUrl) || LlmSection.IsAbsoluteHttpUrl(_baseUrl.Trim()))
        // A typed tuning value that does not parse must block the save, not vanish silently.
        && (_temperatureText.Trim().Length == 0 || ParsedTemperature is not null)
        && (_timeoutText.Trim().Length == 0 || ParsedTimeoutSeconds is not null)
        && (_streamIdleText.Trim().Length == 0 || ParsedStreamIdleSeconds is not null)
        && (_maxTokensText.Trim().Length == 0 || ParsedMaxTokens is not null);

    /// <summary>
    /// True while the typed name already belongs to another connection, or — for a connection of one
    /// model — to another setting; with several models, each row says its own pair's collision.
    /// </summary>
    public bool NameCollision =>
        _owner.IsConnectionNameTaken(_name.Trim(), PreviousName)
        || (!HasSeveralModels && Models[0].NameCollision);

    /// <summary>
    /// What the typed name would make of the setting as a host profile (STUDIO-48): the id a crew
    /// writes, or why no crew could write one — against the other settings and the entries the
    /// settings file holds by hand.
    /// </summary>
    public HostProfileCheck HostProfile =>
        Models.Select(CheckPair).FirstOrDefault(check => check.BlocksSave) ?? CheckPair(Models[0]);

    /// <summary>
    /// The line under the name: <c>profile: &lt;id&gt;</c> as typed, or why it cannot be; null for a
    /// setting without a model, and while the name itself is taken (that line says it).
    /// </summary>
    /// With several models each row says its own id, and this line is silent.
    public string? HostIdText => NameCollision || HasSeveralModels ? null : HostProfileText.Describe(HostProfile, _strings);

    /// <summary>Whether that line is a problem rather than the name to write.</summary>
    public bool IsHostIdIssue => !NameCollision && !HasSeveralModels && HostProfileText.IsIssue(HostProfile);

    /// <summary>
    /// Said when a setting crews could name is renamed under another id: the crews that write the
    /// old one stop loading. Null otherwise.
    /// </summary>
    public string? HostIdRenamedText =>
        _originalHostIds.FirstOrDefault(previous => !Models.Any(row => string.Equals(CheckPair(row).Id, previous, StringComparison.OrdinalIgnoreCase))) is { } gone
            ? _strings.Format(StudioStringKeys.ProfileHostIdRenamed, gone)
            : null;

    /// <summary>
    /// The expert line of the key block: the variable a run outside Studio — a terminal, a
    /// scheduled team — reads this setting's key from: the one Studio remembers it in, which the
    /// settings file names (<c>ApiKeyEnvVar</c>, STUDIO-49) — never the key. Null when no crew can
    /// name the setting, or when it needs no key.
    /// </summary>
    public string? HostKeyHint =>
        RequiresApiKey && HostProfile is { IsOffered: true }
            ? _strings.Format(StudioStringKeys.ProfileHostKeyHint, ApiKeyEnvName)
            : null;

    private void OnHostProfileChanged() =>
        OnPropertiesChanged(
            nameof(HostProfile), nameof(HostIdText), nameof(IsHostIdIssue), nameof(HostIdRenamedText), nameof(HostKeyHint),
            nameof(CanSave));

    /// <summary>
    /// Outcome line of the last connection probe, in the interface's language: the step that
    /// failed, the URL, the time waited and the cause (STUDIO-43). Cleared whenever the endpoint,
    /// the model or the thinking switch changes, since the verdict was about the previous ones. The
    /// verdict keeps its state — in progress, key missing, the probe's result —, and the line is
    /// formed as it is read, so it follows the language (STUDIO-56).
    /// </summary>
    public string? ConnectionTestResult => _verdict switch
    {
        null => null,
        { Result: { } result } => LlmProbeText.Describe(result, _strings),
        { KeyMissing: true } => _strings[StudioStringKeys.ProfileKeyMissingTest],
        { ReadElsewhere: true } => EndpointReadingLine,
        _ => _strings[StudioStringKeys.LlmTesting],
    };

    private void ShowVerdict(ProbeVerdict? verdict)
    {
        _verdict = verdict;
        // A null verdict (the model changed, e.g. by a pick) keeps the list; any new result replaces it.
        if (verdict is not null)
            ReplaceAvailableModels(verdict.Result is { Succeeded: true } found ? found.ModelNames : []);
        OnPropertyChanged(nameof(ConnectionTestResult));
        OnPropertyChanged(nameof(ModelListNote));
    }

    private void ReplaceAvailableModels(IEnumerable<string> names)
    {
        AvailableModels.Clear();
        foreach (var name in names)
            AvailableModels.Add(name);

        RefreshFilteredModels();
        OnPropertyChanged(nameof(HasAvailableModels));
    }

    /// <summary>True while a connection test runs: the button is disabled and the line says so.</summary>
    public bool IsTestingConnection
    {
        get => _isTestingConnection;
        private set
        {
            if (SetProperty(ref _isTestingConnection, value))
                TestConnectionCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Forgets the shown verdict, and any verdict still in flight for the old settings.</summary>
    private void ClearConnectionTestResult()
    {
        _settingsGeneration++;
        ShowVerdict(null);
    }

    /// <summary>Commits the profile to the set.</summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>Closes the editor without touching the set.</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>Picks the provider row carried as the command parameter.</summary>
    public RelayCommand SelectProviderCommand { get; }

    /// <summary>Probes the endpoint with the setting's own key — never the default's (GAP-36).</summary>
    public AsyncRelayCommand TestConnectionCommand { get; }

    /// <summary>The remember-the-key action — stores the draft under the profile's variable, now.</summary>
    public AsyncRelayCommand StoreKeyCommand { get; }

    /// <summary>Reads the balance of the account behind the endpoint and the key (STUDIO-35 D-04).</summary>
    public AsyncRelayCommand ReadBalanceCommand { get; }

    /// <summary>Opens the vendor's console, where a balance no inference key reads is shown.</summary>
    public RelayCommand OpenBalanceConsoleCommand { get; }

    /// <summary>
    /// Remembers the pasted key under the profile's variable (STUDIO-44): in place for the
    /// session as soon as this starts, kept for the next sessions off the interface thread, and
    /// a failure of that second half shown in the editor. Public so tests can await it.
    /// </summary>
    public Task StoreKeyAsync() => StoreKeyCoreAsync(editorClosing: false);

    [SuppressMessage("Design", "CA1031",
        Justification = "Whatever the user scope throws (access denied, a failed broadcast), the key is " +
                        "already in place for the session: the reason belongs on a line of the screen, " +
                        "not in the command's fault handler.")]
    private async Task StoreKeyCoreAsync(bool editorClosing)
    {
        if (_apiKeyInput.Trim() is not { Length: > 0 } pastedKey)
            return;

        // The key goes into the environment under the profile's variable — never into the
        // profile store nor any settings file. The process half is written before this returns.
        var persisted = _keyStore.SaveAsync(ApiKeyEnvName, pastedKey);
        ApiKeyInput = "";
        ShowVerdict(null);
        ShowKeyStoreFailure(null);
        OnPropertyChanged(nameof(HasStoredKey));
        OnPropertyChanged(nameof(KeyStatusText));

        try
        {
            await persisted.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (editorClosing || !ReferenceEquals(_owner.Editor, this))
                _owner.ReportKeyStoreError(ex.Message);
            else
                ShowKeyStoreFailure(ex.Message);
        }

        OnPropertyChanged(nameof(HasStoredKey));
        OnPropertyChanged(nameof(KeyStatusText));
    }

    /// <summary>
    /// The pinned temperature as typed — empty for "let the engine decide". Tolerant of
    /// both decimal separators; an unparseable text simply pins nothing. Some vendors
    /// mandate a value per model (Kimi K3 accepts only 1): pinning it here makes the
    /// first request right, instead of paying the provider's adaptive retry every call.
    /// </summary>
    public string TemperatureText
    {
        get => _temperatureText;
        set
        {
            if (SetProperty(ref _temperatureText, value ?? ""))
                SaveCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// The pinned HTTP timeout in seconds, as typed — empty for the engine's default (30 s).
    /// Reasoning models (Kimi K3, thinking modes) need more than the default to answer.
    /// </summary>
    public string TimeoutText
    {
        get => _timeoutText;
        set
        {
            if (SetProperty(ref _timeoutText, value ?? ""))
                SaveCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// What the timeout field means for the picked provider (LLM-11): the engine's 30 s when
    /// empty, or the pre-filled recommendation and why — the provider's default model thinks
    /// before it answers, and a run that hits the timeout gets no answer at all.
    /// </summary>
    public string TimeoutHint =>
        _selectedProvider?.RecommendedTimeoutSeconds is { } recommended
            ? _strings.Format(StudioStringKeys.ProfileTimeoutHintReasoning,
                recommended.ToString(CultureInfo.CurrentCulture), _selectedProvider.Title)
            : _strings[StudioStringKeys.ProfileTimeoutHint];

    /// <summary>
    /// Pre-fills the timeout the picked provider recommends, and only over a field the user
    /// has not made theirs: empty, or still holding the previous card's own recommendation. A
    /// value the user typed stays; a recommendation leaves with the card that brought it.
    /// </summary>
    private void SeedTimeoutFor(LlmPresetInfo picked, LlmPresetInfo? previous)
    {
        var typed = _timeoutText.Trim();
        var previousSeed = previous?.RecommendedTimeoutSeconds?.ToString(CultureInfo.InvariantCulture);
        var untouched = typed.Length == 0 || string.Equals(typed, previousSeed, StringComparison.Ordinal);
        if (!untouched)
            return;

        TimeoutText = picked.RecommendedTimeoutSeconds?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>The three positions of the thinking switch: provider default, on, off — in the interface's language.</summary>
    public IReadOnlyList<ThinkingChoice> ThinkingChoices { get; private set; }

    /// <summary>The three call modes.</summary>
    public IReadOnlyList<CallModeChoice> CallModeChoices { get; private set; }

    /// <summary>The call mode chosen. Auto leaves the key out of the profile; Buffer or Stream pins it.</summary>
    public CallModeChoice SelectedCallMode
    {
        get => CallModeChoices.First(c => c.Value == _callMode);
        set
        {
            if (value is null || value.Value == _callMode)
                return;

            _callMode = value.Value;
            OnPropertyChanged();
        }
    }

    private static CallModeChoice[] BuildCallModeChoices(IStudioStrings strings) =>
    [
        new CallModeChoice("Auto", strings[StudioStringKeys.SettingsCallModeAuto]),
        new CallModeChoice("Buffer", strings[StudioStringKeys.SettingsCallModeBuffer]),
        new CallModeChoice("Stream", strings[StudioStringKeys.SettingsCallModeStream]),
    ];

    private static ThinkingChoice[] BuildModelThinkingChoices(IStudioStrings strings) =>
    [
        new ThinkingChoice(null, strings[StudioStringKeys.ProfileThinkingInherit]),
        new ThinkingChoice(true, strings[StudioStringKeys.ProfileThinkingOn]),
        new ThinkingChoice(false, strings[StudioStringKeys.ProfileThinkingOff]),
    ];

    private static ThinkingChoice[] BuildThinkingChoices(IStudioStrings strings) =>
    [
        new ThinkingChoice(null, strings[StudioStringKeys.ProfileThinkingProviderDefault]),
        new ThinkingChoice(true, strings[StudioStringKeys.ProfileThinkingOn]),
        new ThinkingChoice(false, strings[StudioStringKeys.ProfileThinkingOff]),
    ];

    /// <summary>
    /// The thinking switch this profile pins (LLM-11). Provider default lets the model decide
    /// — on for Kimi K2.6, DeepSeek V4 and GLM; off is the way out of a timeout when the task
    /// does not need the reasoning pass. Travels as <c>ORKEON_Llm__Thinking__Enabled</c>.
    /// </summary>
    public ThinkingChoice SelectedThinking
    {
        get => ThinkingChoices.First(c => c.Value == _thinkingEnabled);
        set
        {
            if (value is null || value.Value == _thinkingEnabled)
                return;

            _thinkingEnabled = value.Value;
            OnPropertyChanged();
            // STUDIO-43: the verdict shown was earned under the previous switch — the test
            // completion carries it — so it must not stay under the new one.
            ClearConnectionTestResult();
        }
    }

    /// <summary>What the switch does, under the field.</summary>
    public string ThinkingHint => _strings[StudioStringKeys.ProfileThinkingHint];

    /// <summary>The reasoning-effort hint as typed — empty for the provider's default.</summary>
    public string ThinkingEffortText
    {
        get => _thinkingEffortText;
        set => SetProperty(ref _thinkingEffortText, value ?? "");
    }

    /// <summary>The typed timeout, or null when empty, unparseable, or non-positive.</summary>
    public int? ParsedTimeoutSeconds =>
        int.TryParse(_timeoutText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;

    /// <summary>
    /// The longest silence allowed between two lines of a streamed answer, in seconds, as typed —
    /// empty for no idle bound: the stream then runs under the timeout alone, which bounds the
    /// whole call (LLM-12).
    /// </summary>
    public string StreamIdleText
    {
        get => _streamIdleText;
        set
        {
            if (SetProperty(ref _streamIdleText, value ?? ""))
                SaveCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The typed idle bound, or null when empty, unparseable, or non-positive.</summary>
    public int? ParsedStreamIdleSeconds =>
        int.TryParse(_streamIdleText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;

    /// <summary>
    /// The pinned maximum response length in tokens, as typed — empty leaves the cap to the
    /// engine, which sends the model's documented maximum (LLM-10); <see cref="MaxTokensHint"/>
    /// says what that is for this model. Before LLM-10 the engine sent 4096 for every model, a
    /// budget a reasoning model spends thinking before it writes a word (STUDIO-12 C5b).
    /// </summary>
    public string MaxTokensText
    {
        get => _maxTokensText;
        set
        {
            if (SetProperty(ref _maxTokensText, value ?? ""))
                SaveCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// What an empty response-budget field means for this profile (LLM-10): the model's
    /// documented maximum when the catalogue knows it, no cap at all for a vendor that
    /// documents none or for a local runtime, and the 4096 fallback — with the invitation to
    /// pin — for a model the catalogue does not know. Provider-aware: an entry can hold on one
    /// endpoint only (Together clamps to its window; HuggingFace's router does not).
    /// </summary>
    public string MaxTokensHint
    {
        get
        {
            var providerKey = _selectedProvider?.Name;
            if (string.Equals(providerKey, LlmProviderKeys.Ollama, StringComparison.OrdinalIgnoreCase))
                return _strings[StudioStringKeys.ProfileMaxTokensHintLocal];

            return MaxTokensHintFor(_selectedRow.Model);
        }
    }

    /// <summary>What an empty response-budget field means for <paramref name="model"/> on this connection's card.</summary>
    internal string MaxTokensHintFor(string? model)
    {
        {
            var providerKey = _selectedProvider?.Name;
            if (string.Equals(providerKey, LlmProviderKeys.Ollama, StringComparison.OrdinalIgnoreCase))
                return _strings[StudioStringKeys.ProfileMaxTokensHintLocal];

            return LlmModelOutputLimits.MaxOutputTokens(model, providerKey) switch
            {
                null => _strings[StudioStringKeys.ProfileMaxTokensHintUnknown],
                LlmModelOutputLimits.Unbounded => _strings[StudioStringKeys.ProfileMaxTokensHintUnbounded],
                { } documented => _strings.Format(
                    StudioStringKeys.ProfileMaxTokensHintKnown,
                    documented.ToString("N0", CultureInfo.CurrentCulture)),
            };
        }
    }

    /// <summary>The typed token budget, or null when empty, unparseable, or non-positive.</summary>
    public int? ParsedMaxTokens =>
        int.TryParse(_maxTokensText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;

    /// <summary>
    /// The typed temperature, or null when empty, unparseable or not a finite number (STUDIO-55):
    /// « NaN », « Infinity » and <c>1e400</c>, read as an infinity, are refused like « abc » — JSON
    /// has no text for them, and the run refuses them at start.
    /// </summary>
    public double? ParsedTemperature =>
        double.TryParse(_temperatureText.Trim().Replace(',', '.'),
            System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value)
            ? value
            : null;

    private void Save()
    {
        if (!CanSave)
            return;

        if (RequiresApiKey && _apiKeyInput.Trim().Length > 0)
            // A pasted-but-not-yet-remembered key must not be lost on save; the editor closes,
            // so a failure to keep it for the next sessions is said on the profile list.
            _ = StoreKeyCoreAsync(editorClosing: true);

        var models = Models.Select(row => row.ToModel()).ToList();
        _owner.CommitEdit(new ModelConnection
        {
            Name = _name.Trim(),
            Provider = _selectedProvider?.Name,
            Models = models,
            BaseUrl = _baseUrl,
            WorkspaceId = string.IsNullOrWhiteSpace(_workspaceId) ? null : _workspaceId.Trim(),
            Temperature = ParsedTemperature,
            TimeoutSeconds = ParsedTimeoutSeconds,
            StreamIdleSeconds = ParsedStreamIdleSeconds,
            CallMode = _callMode == "Auto" ? null : _callMode,
            MaxTokens = ParsedMaxTokens,
            ThinkingEnabled = _thinkingEnabled,
            ThinkingEffort = string.IsNullOrWhiteSpace(_thinkingEffortText) ? null : _thinkingEffortText.Trim(),
            KeyEnvName = RequiresApiKey ? ApiKeyEnvName : null,
        }, PreviousName, PreviousName is null ? null : [.. Models.Select(row => row.PreviousPairName)]);
    }

    /// <summary>
    /// Probes the endpoint, then runs a minimal completion on the profile's model with its
    /// thinking switch (STUDIO-43), under a deadline of 30 s or the profile's own when shorter —
    /// with the setting's key and it alone (GAP-36): a setting that needs one and has none is
    /// refused without a request. Public so tests can await it with a token.
    /// </summary>
    [SuppressMessage("Design", "CA1031",
        Justification = "The test is a convenience that must never fault the command: any unexpected " +
                        "failure is reported in the result line like every other unreachable endpoint.")]
    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        if (_isTestingConnection)
            return;

        if (OllamaReadElsewhere)
        {
            // A run would not reach Ollama there: nothing is sent, the line says why.
            ShowVerdict(ProbeVerdict.OllamaElsewhere);
            return;
        }

        var apiKey = SettingKey;
        if (RequiresApiKey && apiKey is null)
        {
            // The design refuses to probe into a guaranteed 401: name the missing step.
            ShowVerdict(ProbeVerdict.MissingKey);
            return;
        }

        var request = new LlmProbeRequest
        {
            BaseUrl = BaseUrl,
            ApiKey = apiKey,
            WorkspaceId = string.IsNullOrWhiteSpace(_workspaceId) ? null : _workspaceId.Trim(),
            Model = _selectedRow.Model,
            ThinkingEnabled = _selectedRow.Thinking.Value ?? _thinkingEnabled,
            CheckCompletion = true,
            Timeout = LlmProbeRequest.TimeoutFor(PositiveInt(_selectedRow.TimeoutText) ?? ParsedTimeoutSeconds),
            ModelThinkingOverrides = _owner.ModelThinkingOverrides,
        };

        IsTestingConnection = true;
        ShowVerdict(ProbeVerdict.InProgress);
        var generation = _settingsGeneration;
        LlmProbeResult result;
        try
        {
            result = await _probe.ProbeAsync(request, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            IsTestingConnection = false;
            ShowVerdict(null);
            throw;
        }
        catch (Exception ex)
        {
            result = LlmProbeResult.Unreachable(ex.Message);
        }

        IsTestingConnection = false;
        // A setting changed while the probe ran: its verdict is about settings no longer shown.
        if (generation == _settingsGeneration)
            ShowVerdict(ProbeVerdict.Of(result));
    }

    /// <summary>
    /// The key this setting presents to a probe (GAP-36): the one typed here, else the one
    /// remembered under its variable — and, for a setting that needs none, its card's placeholder,
    /// which a run presents too: Docker Model Runner's <c>not-needed</c>, nothing for Ollama
    /// (STUDIO-54) —, never a draft typed for another card. Never the runtime's own variable in place
    /// of a missing key: that is the default's key, which no run of another setting reads.
    /// </summary>
    private string? SettingKey
    {
        get
        {
            if (!RequiresApiKey)
                return LlmPresets.PlaceholderKeyOf(_selectedProvider?.Name);
            if (_apiKeyInput.Trim() is { Length: > 0 } typed)
                return typed;
            return _keyStore.Peek(ApiKeyEnvName);
        }
    }

    /// <summary>
    /// Reads the balance of the endpoint being edited, with the setting's key and it alone — the
    /// one typed here first, then the remembered one (GAP-36) — and, like the connection test,
    /// refuses to ask without a key rather than earn a certain refusal. Public so tests can await it.
    /// </summary>
    public async Task ReadBalanceAsync(CancellationToken cancellationToken)
    {
        if (_balances is not { CanRead: true } balances)
            return;

        var target = new ProviderBalanceTarget(
            ProviderBalanceAccount.For(BaseUrl, RequiresApiKey ? ApiKeyEnvName : null), BaseUrl, [_name.Trim()]);
        var typed = RequiresApiKey ? _apiKeyInput.Trim() : "";
        if (RequiresApiKey && target.RequestWith(_keyStore, typed).ApiKey is null)
        {
            ShowBalance(null);
            _balanceKeyMissing = true;
            OnPropertyChanged(nameof(BalanceResult));
            return;
        }

        ShowBalance(await balances.ReadEndpointAsync(target, typed.Length > 0 ? typed : null, cancellationToken).ConfigureAwait(true));
    }

    private void ShowBalance(ProviderBalanceResult? reading)
    {
        _balanceReading = reading;
        _balanceKeyMissing = false;
        OnPropertiesChanged(nameof(BalanceResult), nameof(BalanceDetail), nameof(IsBalanceLow), nameof(BalanceOffersConsole));
        OpenBalanceConsoleCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// The language switched (STUDIO-56): relayed by the tab — the editor lives for one edit, the
    /// port as long as the window, so it never subscribes itself (STUDIO-11). The catalogue and the
    /// thinking choices are rebuilt in the language, the card found again by its name and the switch
    /// by its value — assigned to the fields, never through the setters, which reseed the fields and
    /// clear the verdict —; then each list is announced before its choice, and every computed line.
    /// What was typed does not move.
    /// </summary>
    /// <param name="providers">The catalogue in the new language.</param>
    internal void RefreshTexts(IReadOnlyList<LlmPresetInfo> providers)
    {
        Providers = providers;
        if (_selectedProvider is { } selected)
            _selectedProvider = providers.FirstOrDefault(p => string.Equals(p.Name, selected.Name, StringComparison.Ordinal)) ?? selected;
        ThinkingChoices = BuildThinkingChoices(_strings);
        ModelThinkingChoices = BuildModelThinkingChoices(_strings);
        CallModeChoices = BuildCallModeChoices(_strings);
        OnPropertyChanged(nameof(ModelThinkingChoices));
        RefreshRows();

        OnPropertiesChanged(
            nameof(Providers), nameof(LocalProviders), nameof(CloudProviders), nameof(OtherProviders),
            nameof(SelectedProvider), nameof(ThinkingChoices), nameof(SelectedThinking), nameof(CallModeChoices), nameof(SelectedCallMode),
            nameof(KeyStatusText), nameof(KeyBlockTitle), nameof(KeyConsoleUrl), nameof(KeyStoreError),
            nameof(HostIdText), nameof(HostIdRenamedText), nameof(HostKeyHint),
            nameof(TimeoutHint), nameof(ThinkingHint), nameof(MaxTokensHint),
            nameof(ConnectionTestResult), nameof(ModelListNote), nameof(BalanceResult), nameof(EndpointReadingLine));
    }

    /// <summary>The state of the last connection test: in progress, refused for a missing key, or the probe's result.</summary>
    private sealed record ProbeVerdict(LlmProbeResult? Result, bool KeyMissing, bool ReadElsewhere = false)
    {
        public static ProbeVerdict InProgress { get; } = new(null, false);

        public static ProbeVerdict MissingKey { get; } = new(null, true);

        public static ProbeVerdict OllamaElsewhere { get; } = new(null, false, true);

        public static ProbeVerdict Of(LlmProbeResult result) => new(result, false);
    }
}

/// <summary>
/// The model-settings tab: the named, reusable model settings of this machine, the default
/// election, and the profile Studio's own assistant runs on. Every mutation is persisted to
/// the store immediately (its file is Studio state, like the history); electing a default
/// additionally mirrors it — whole — into the settings document's <c>Llm</c> section, which is
/// what the CLI reads when it runs outside Studio, and every setting that names a provider is
/// mirrored into <c>Llm:Profiles:&lt;id&gt;</c> — the host profile a crew names with
/// <c>profile: &lt;id&gt;</c> (STUDIO-48). Neither carries a key: each names the variable that
/// holds it (<c>ApiKeyEnvVar</c>, STUDIO-49), which a run outside Studio reads. Both writes go through the ordinary dirty/save cycle of
/// the settings screen. The entries of <c>Llm:Profiles</c> no setting owns are listed read-only,
/// and the RAG's model (<c>Orkeon:Rag:LlmProfile</c>) is chosen among all of them.
/// </summary>
public sealed class ModelProfilesViewModel : ObservableObject
{
    private readonly IModelProfileStore _store;
    private readonly LlmSectionViewModel _llm;
    private readonly IStudioStrings _strings;
    private readonly ILlmEndpointProbe _probe;
    private readonly IApiKeyStore _keyStore;
    private readonly BalanceReadings? _balances;
    private readonly IShellOpener? _opener;
    private ModelProfileSet _set = ModelProfileSet.Empty;
    private readonly Func<IReadOnlyList<TeamSummary>>? _loadTeams;
    private readonly Action<IReadOnlyList<(string From, string To)>>? _followRename;
    private ModelProfileEditorViewModel? _editor;
    private string? _loadFailure;
    private string? _keyStoreFailure;
    private string? _writeFailure;

    /// <summary>
    /// Builds the tab over its seams. <paramref name="links"/> ties it to the rest of the window:
    /// its balances are the provider balances read this session (STUDIO-35) — each row shows its
    /// account's, and the editor reads one; left out, neither shows —, its shell opener opens a
    /// vendor's console from the editor, and its rename hook is called with a setting's former
    /// name and its new one when the editor renames it, before the change reaches the cards, the
    /// settings file and the launchers (STUDIO-52): the teams naming it follow it first, so
    /// « Used by » and their launchers read them under the new name. Left out, a rename moves the
    /// elections alone.
    /// </summary>
    public ModelProfilesViewModel(
        IModelProfileStore? store,
        LlmSectionViewModel llm,
        IStudioStrings? strings = null,
        ILlmEndpointProbe? probe = null,
        IApiKeyStore? keyStore = null,
        ModelProfilesLinks? links = null)
    {
        ArgumentNullException.ThrowIfNull(llm);

        var balances = links?.Balances;
        _loadTeams = links?.LoadTeams;
        _followRename = links?.FollowRename;

        _store = store ?? new InMemoryModelProfileStore();
        _llm = llm;
        _strings = strings ?? EnglishStudioStrings.Instance;
        // Lives as long as the tab, which lives as long as the window.
        _probe = probe ?? HttpLlmEndpointProbe.ForCurrentMachine();
        _keyStore = keyStore ?? new EnvironmentApiKeyStore();
        _balances = balances;
        _opener = links?.ShellOpener;
        NewProfileCommand = new RelayCommand(BeginCreate);

        // A read landed, a threshold moved or the language switched: every row's chip says it again.
        if (balances is not null)
        {
            balances.Changed += (_, _) => RefreshBalances();
            _strings.CultureChanged += (_, _) => RefreshBalances();
        }

        // STUDIO-48: the host profile lines speak the interface's language, and a settings file
        // opened in place of the previous one brings its own entries and its own RAG choice.
        _strings.CultureChanged += (_, _) => RefreshHostTexts();
        _llm.PropertyChanged += OnSettingsDocumentChanged;
    }

    /// <summary>The profile cards, rebuilt after every mutation.</summary>
    public ObservableCollection<ModelProfileItemViewModel> Profiles { get; } = [];

    /// <summary>The cards grouped by connection, in the order of the set: what the tab shows.</summary>
    public ObservableCollection<ModelConnectionItemViewModel> Connections { get; } = [];

    /// <summary>
    /// Raised after a change of the settings moved the host profile of some setting names — one
    /// created, removed, renamed, switched to « no model » (STUDIO-50): a team whose companion file
    /// names one of them runs elsewhere outside Studio, and the window writes its launchers again.
    /// </summary>
    public event EventHandler<HostProfilesChangedEventArgs>? HostProfilesChanged;

    /// <summary>The profile names, for the assistant picker.</summary>
    public ObservableCollection<string> ProfileNames { get; } = [];

    /// <summary>
    /// The settings screen's API-keys rows (one per distinct environment-variable name
    /// the profiles resolve), rebuilt with the profile list.
    /// </summary>
    public ObservableCollection<SecretRowViewModel> Secrets { get; } = [];

    /// <summary>Whether the secrets card shows at all.</summary>
    public bool HasSecrets => Secrets.Count > 0;

    /// <summary>The current set, for consumers outside this tab (the wizard's gate).</summary>
    public ModelProfileSet Set => _set;

    /// <summary>
    /// The <c>ORKEON_Llm__*</c> overrides a launch under <paramref name="profile"/> lays over its
    /// child process — the key resolved through the key store (STUDIO-44), so a key held only in
    /// the user scope reaches the child as well as one in Studio's own environment — on top of
    /// <see cref="LaunchEnvironment"/>, every setting as the host profile a crew may name. A
    /// profile that runs in place of the elected default lays every field, blank when it leaves it
    /// unset (STUDIO-49, decision 4): nothing of the default — its key, the variable holding it —
    /// reaches another endpoint. The elected default itself lays what it sets: the section already
    /// is that setting, and a key the file holds there is its own.
    /// </summary>
    public IReadOnlyDictionary<string, string> LaunchEnvironmentOf(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var isDefault = string.Equals(profile.Name, _set.DefaultProfile, StringComparison.Ordinal);
        var environment = new Dictionary<string, string>(LaunchEnvironment(), StringComparer.Ordinal);
        foreach (var (key, value) in profile.EnvironmentOverrides(_keyStore.Peek))
        {
            if (!isDefault || value.Length > 0)
                environment[key] = value;
        }

        return environment;
    }

    /// <summary>
    /// What every launch lays over its child, whatever profile it runs on (STUDIO-48): each
    /// setting offered to crews as <c>ORKEON_Llm__Profiles__&lt;id&gt;__*</c>, its key resolved
    /// through the key store — so a crew that writes <c>profile: claude</c> runs on « Claude »,
    /// whether or not the settings file was saved since, and whichever file the launch reads.
    /// </summary>
    public IReadOnlyDictionary<string, string> LaunchEnvironment() =>
        HostLlmProfiles.LaunchEnvironment(_set, _keyStore.Peek);

    /// <summary>
    /// The entries of <c>Llm:Profiles</c> no setting owns — written by hand in the settings file,
    /// shown read-only (STUDIO-48): Studio never rewrites nor removes them.
    /// </summary>
    public ObservableCollection<HandWrittenProfileViewModel> HandWrittenProfiles { get; } = [];

    /// <summary>Whether the « written in the settings file » block shows.</summary>
    public bool HasHandWrittenProfiles => HandWrittenProfiles.Count > 0;

    /// <summary>
    /// The choices of the RAG's model (<c>Orkeon:Rag:LlmProfile</c>, GAP-19): the default, every
    /// setting offered to crews, every entry written by hand — and a name the file holds without
    /// defining it, so the picker shows what is there (the validation warns about it).
    /// </summary>
    public ObservableCollection<RagProfileChoice> RagProfileChoices { get; } = [];

    /// <summary>
    /// The profile the RAG calls. The default removes the key; a null selection — what a picker
    /// writes while its list is being rebuilt — changes nothing.
    /// </summary>
    public RagProfileChoice? SelectedRagProfile
    {
        get
        {
            var current = _llm.RagLlmProfile;
            return LlmProfilesSection.IsDefault(current)
                ? RagProfileChoices.FirstOrDefault(choice => choice.Id is null)
                : RagProfileChoices.FirstOrDefault(choice =>
                    string.Equals(choice.Id, current!.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        set
        {
            if (value is null)
                return;

            _llm.RagLlmProfile = value.Id;
        }
    }

    /// <summary>Name of the assistant's profile; null while unconfigured.</summary>
    public string? StudioProfileName
    {
        get => _set.StudioProfile;
        set
        {
            if (value is not { Length: > 0 } || string.Equals(_set.StudioProfile, value, StringComparison.Ordinal))
                return;

            Mutate(_set.WithStudio(value));
        }
    }

    /// <summary>True once the assistant has a model — the creation wizard is gated on this.</summary>
    public bool HasStudioProfile => _set.Studio is not null;

    /// <summary>Name of the default profile, or null.</summary>
    public string? DefaultProfileName => _set.DefaultProfile;

    /// <summary>True while the set holds no profile at all — the empty-state hint shows.</summary>
    public bool IsEmpty => _set.Profiles.Count == 0;

    /// <summary>
    /// Why the profile file could not be read, when it exists and could not be; null after a
    /// clean load. A file that fails to parse used to load as the empty set — the same screen
    /// as a first run, over a file the user had hand-written (STUDIO-12 C6). The next change
    /// overwrites that file, so the line says so. The cause is kept and the line formed as it is
    /// read, so it follows the language (STUDIO-56).
    /// </summary>
    public string? LoadError => _loadFailure is { } cause
        ? _strings.Format(StudioStringKeys.ProfileFileUnreadable, cause)
        : null;

    /// <summary>Whether the unreadable-file line shows.</summary>
    public bool HasLoadError => _loadFailure is not null;

    /// <summary>
    /// Why a key remembered by an editor that has closed since — a key pasted and saved with the
    /// profile — was not kept for the next sessions (STUDIO-44). It is in place for this one.
    /// Cleared when an editor opens. Formed from its cause as it is read (STUDIO-56).
    /// </summary>
    public string? KeyStoreError => _keyStoreFailure is { } cause
        ? _strings.Format(StudioStringKeys.ProfileKeyPersistFailed, cause)
        : null;

    /// <summary>Whether the key-not-kept line shows on the profile list.</summary>
    public bool HasKeyStoreError => _keyStoreFailure is not null;

    /// <summary>Says on the list why a key an editor remembered as it closed was not kept.</summary>
    /// <param name="cause">What the user scope answered.</param>
    internal void ReportKeyStoreError(string? cause)
    {
        _keyStoreFailure = cause;
        OnPropertiesChanged(nameof(KeyStoreError), nameof(HasKeyStoreError));
    }

    /// <summary>
    /// Why the last write of the model profiles file failed (STUDIO-56) — a read-only or locked file,
    /// a set the serializer refuses —; null once a write goes through. The settings stay as shown
    /// for the session, and the next change writes them again. Formed from its cause as it is read.
    /// </summary>
    public string? ProfileWriteError => _writeFailure is { } cause
        ? _strings.Format(StudioStringKeys.ProfileFileNotWritten, cause)
        : null;

    /// <summary>Whether the file-not-written line shows.</summary>
    public bool HasProfileWriteError => _writeFailure is not null;

    /// <summary>The chain of writes of the model profiles file, for a test to await what a gesture wrote.</summary>
    internal Task PendingWrite => _persist;

    /// <summary>Deleting is allowed only while more than one profile remains.</summary>
    public bool CanDelete => _set.Profiles.Count > 1;

    /// <summary>The editor overlay; null while closed.</summary>
    public ModelProfileEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            if (!SetProperty(ref _editor, value))
                return;

            OnPropertyChanged(nameof(IsEditorOpen));
            if (value is not null)
                ReportKeyStoreError(null);
        }
    }

    /// <summary>Whether the editor overlay is showing.</summary>
    public bool IsEditorOpen => _editor is not null;

    /// <summary>Opens the editor over a fresh profile seeded from the first preset.</summary>
    public RelayCommand NewProfileCommand { get; }

    /// <summary>Loads the persisted set. Called once, from the window's deferred initialize.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);
        _set = loaded.Set;
        _loadFailure = loaded.Error;
        OnPropertiesChanged(nameof(LoadError), nameof(HasLoadError));
        Rebuild();
    }

    /// <summary>Elects <paramref name="name"/> as the default and mirrors it into the settings.</summary>
    public void SetDefault(string name)
    {
        var mutated = _set.WithDefault(name);
        if (ReferenceEquals(mutated, _set))
            return;

        Mutate(mutated);
        ApplyDefaultToSettings();
    }

    /// <summary>Opens the editor over the connection of <paramref name="profile"/>, its model selected.</summary>
    internal void BeginEdit(ModelProfile profile)
    {
        var connection = ConnectionOf(profile);
        Editor = new ModelProfileEditorViewModel(this, ProviderCatalog(), connection, connection.Name, EditorSeams(), profile.Alias);
    }

    /// <summary>Adds a copy of the connection of <paramref name="profile"/>, every model with it.</summary>
    internal void Duplicate(ModelProfile profile)
    {
        var connection = ConnectionOf(profile);
        // A copy is a host profile of its own per model: none of its pairs may give an id already answered to.
        var name = _set.CopyNameFor(
            connection.Name,
            _strings[StudioStringKeys.ProfileCopySuffix],
            candidate => _set.FindConnection(candidate) is not null
                || (connection with { Name = candidate }).Flatten().Any(pair =>
                    _set.Find(pair.Name) is not null || CheckHostProfile(pair.Name, previousName: null, pair.DescribesProvider).BlocksSave));
        Mutate(_set.UpsertConnection(connection with { Name = name }).Set);
    }

    /// <summary>The connection <paramref name="profile"/> is a model of; a setting built on its own is its own connection.</summary>
    private ModelConnection ConnectionOf(ModelProfile profile) =>
        _set.FindConnection(profile.Connection) ?? ModelConnection.Of(profile);

    internal void Delete(string name)
    {
        if (!CanDelete)
            return;

        var wasDefault = string.Equals(_set.DefaultProfile, name, StringComparison.Ordinal);
        Mutate(_set.Remove(name));
        if (wasDefault)
            ApplyDefaultToSettings();
    }

    /// <summary>Saves a setting as a connection of one model (<see cref="ModelConnection.Of"/>).</summary>
    internal void CommitEdit(ModelProfile profile, string? previousName) =>
        CommitEdit(ModelConnection.Of(profile), previousName, previousPairNames: null);

    /// <summary>
    /// Saves <paramref name="connection"/>, which replaces the one named <paramref name="previousName"/>;
    /// each model's previous pair is <paramref name="previousPairNames"/> at its index
    /// (<see cref="ModelProfileSet.UpsertConnection"/>).
    /// </summary>
    internal void CommitEdit(ModelConnection connection, string? previousName, IReadOnlyList<string?>? previousPairNames)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var oldPairs = previousName is null ? [] : _set.FindConnection(previousName)?.PairNames ?? [];
        var wasDefault = _set.DefaultProfile is { } elected && oldPairs.Contains(elected, StringComparer.Ordinal);
        var upsert = _set.UpsertConnection(connection, previousName, previousPairNames);

        // A rename (STUDIO-52): the teams naming each renamed pair follow it first — compared as every
        // launch finds a setting, ordinal —, all at once, so a swap or a chain never sends a team
        // twice; then the change rebuilds « Used by », mirrors the settings file and writes the
        // launchers of the teams it moved.
        if (upsert.Renames.Count > 0)
            _followRename?.Invoke(upsert.Renames);

        Mutate(upsert.Set, upsert.Renames);
        Editor = null;

        if (wasDefault || (_set.DefaultProfile is { } now && connection.Normalized().PairNames.Contains(now, StringComparer.Ordinal)))
            ApplyDefaultToSettings();
    }

    internal void CancelEdit() => Editor = null;

    /// <summary>
    /// <paramref name="text"/> without the keys in place: the ones the settings name, the decision
    /// providers' and the runtime's own. What is copied out of Studio goes through it.
    /// </summary>
    internal string MaskKeys(string text) => SecretMasker.Apply(
        text,
        _set.Profiles.Select(profile => profile.KeyEnvName)
            .Concat(Orkeon.Infrastructure.Decision.SystemOne.DecisionProviderDescriptor.All.Select(d => d.DefaultApiKeyVariable))
            .Append(LlmApiKeyVariable)
            .OfType<string>()
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Select(_keyStore.Peek));

    /// <summary>The variable of the runtime's own key.</summary>
    private const string LlmApiKeyVariable = DecisionSection.EnvironmentPrefix + "Llm__ApiKey";

    private void RefreshBalances()
    {
        foreach (var row in Profiles)
            row.RefreshBalance();
    }

    /// <summary>
    /// The <c>Llm:ModelCapabilities</c> entries of the settings document as it stands now: the
    /// connection test narrows the thinking switch by them, as a run does.
    /// </summary>
    internal IReadOnlyDictionary<string, Orkeon.Domain.SharedKernel.ValueObjects.ThinkingSupport?> ModelThinkingOverrides =>
        _llm.ModelThinkingOverrides;

    /// <summary>Whether <paramref name="name"/> already belongs to a profile other than the one being edited.</summary>
    internal bool IsNameTaken(string name, string? previousName) =>
        _set.Profiles.Any(p =>
            string.Equals(p.Name, name, StringComparison.Ordinal)
            && !string.Equals(p.Name, previousName, StringComparison.Ordinal));

    /// <summary>
    /// Whether <paramref name="pairName"/> already names a setting of a connection other than
    /// <paramref name="connection"/>, the one being edited.
    /// </summary>
    internal bool IsPairNameTaken(string pairName, string? connection) =>
        _set.Profiles.Any(p =>
            string.Equals(p.Name, pairName, StringComparison.Ordinal)
            && !string.Equals(p.Connection, connection, StringComparison.Ordinal));

    /// <summary>Whether another connection than <paramref name="previousName"/> is named <paramref name="name"/>.</summary>
    internal bool IsConnectionNameTaken(string name, string? previousName) =>
        !string.Equals(name, previousName, StringComparison.Ordinal) && _set.FindConnection(name) is not null;

    /// <summary>The teams whose setting is <paramref name="pairName"/>.</summary>
    internal IReadOnlyList<string> TeamsNaming(string pairName) =>
        TeamsByProfile().Usage.TryGetValue(pairName, out var teams) ? teams : [];

    /// <summary>The standing one model of the connection being edited would have as a host profile.</summary>
    internal HostProfileCheck CheckHostProfile(string pairName, IReadOnlyCollection<string> replaced, IReadOnlyList<string> above, bool describesProvider) =>
        _llm.CheckHostProfile(pairName, replaced, above, describesProvider, _set);

    /// <summary>The standing <paramref name="name"/> would have as a host profile (STUDIO-48).</summary>
    internal HostProfileCheck CheckHostProfile(string name, string? previousName, bool describesProvider) =>
        _llm.CheckHostProfile(name, previousName, describesProvider, _set);

    /// <summary>The id the setting <paramref name="name"/> is offered to crews under; null when it is not.</summary>
    internal string? OfferedHostId(string name) =>
        HostLlmProfiles.Offered(_set).TryGetValue(name, out var id) ? id : null;

    private void BeginCreate()
    {
        var catalog = ProviderCatalog();
        var seed = catalog.Count > 0 ? catalog[0] : null;
        Editor = new ModelProfileEditorViewModel(
            this,
            catalog,
            new ModelProfile
            {
                // UX lot 04 (044): the very first model is « Main », never a name that reads as the application.
                Name = _strings[IsEmpty ? StudioStringKeys.ProfileFirstName : StudioStringKeys.ProfileNewName],
                Provider = seed?.Name,
                Model = seed?.DefaultModel,
                BaseUrl = seed?.DefaultBaseUrl,
            },
            previousName: null,
            EditorSeams());
    }

    private ModelProfileEditorSeams EditorSeams() => new(_probe, _keyStore, _strings, _balances, _opener);

    private IReadOnlyList<LlmPresetInfo> ProviderCatalog() => LlmPresets.ProviderCatalogFor(_strings);

    private Task _persist = Task.CompletedTask;

    private void Mutate(ModelProfileSet set, IReadOnlyList<(string From, string To)>? renames = null)
    {
        renames ??= [];
        var before = _set;
        _set = set;
        // STUDIO-48: the settings are the host's profiles — the settings file follows every change
        // (a rename moves the entry, a removal takes it out, the key never goes in).
        _llm.MirrorModelProfiles(before, set, renames);
        Rebuild();
        // STUDIO-50: the launchers of the teams naming a moved setting say it again. A renamed
        // setting's teams already name its new name (STUDIO-52): they move with its former one — a
        // setting renamed to a name no crew can write takes the profile out of their launchers.
        var moved = new HashSet<string>(HostLlmProfiles.MovedNames(before, set), StringComparer.Ordinal);
        foreach (var (from, to) in renames)
        {
            if (moved.Contains(from))
                moved.Add(to);
        }

        if (moved.Count > 0)
            HostProfilesChanged?.Invoke(this, new HostProfilesChangedEventArgs(moved));
        // Writes are chained so two rapid mutations can never interleave on the file. Each write
        // answers for itself (STUDIO-56): a refused one is said on the tab and never fails the next,
        // which writes the whole set again — the profile set lives on in memory for the session.
        _persist = Persist(_persist, set);
    }

    [SuppressMessage("Design", "CA1031",
        Justification = "Whatever the write throws — a read-only file, a set the serializer refuses —, the set " +
                        "lives on for the session: the cause belongs on the tab's line, and the next write must " +
                        "not inherit the failure.")]
    private async Task Persist(Task previous, ModelProfileSet set)
    {
        await previous.ConfigureAwait(true);
        string? failure = null;
        try
        {
            await _store.SaveAsync(set).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        _writeFailure = failure;
        OnPropertiesChanged(nameof(ProfileWriteError), nameof(HasProfileWriteError));
    }

    private void ApplyDefaultToSettings()
    {
        if (_set.Default is not { } profile)
            return;

        // Mirrored into the Llm section so that a bare orkeon run follows the same election —
        // whole (STUDIO-49): its timeout, its thinking switch and the variable holding its key,
        // not the model and the endpoint alone. The write lands in the settings document and
        // travels through the screen's own edit then save cycle: Studio never saves the settings
        // file behind the user's back.
        _llm.ElectDefault(profile);
    }

    /// <summary>
    /// Reads the teams again for « Used by » (STUDIO-73): a team's model changed on its card or from
    /// the Run screen, or a team adopted, moves it from one card to another.
    /// </summary>
    public void RefreshTeamUsage() => Rebuild();

    private void Rebuild()
    {
        var (usage, following) = TeamsByProfile();
        var standing = HostLlmProfiles.Classify(_set);

        Profiles.Clear();
        foreach (var profile in _set.Profiles)
        {
            var profileStanding = new ModelProfileStanding(
                IsDefault: string.Equals(profile.Name, _set.DefaultProfile, StringComparison.Ordinal),
                IsStudio: string.Equals(profile.Name, _set.StudioProfile, StringComparison.Ordinal),
                UsedByTeams: usage.TryGetValue(profile.Name, out var usedBy) ? usedBy : [],
                Host: standing.GetValueOrDefault(profile.Name),
                FollowingTeams: string.Equals(profile.Name, _set.DefaultProfile, StringComparison.Ordinal) ? following : 0);
            Profiles.Add(new ModelProfileItemViewModel(profile, profileStanding, this, _balances, _strings));
        }

        Connections.Clear();
        foreach (var connection in _set.Connections.Where(connection => !string.IsNullOrWhiteSpace(connection.Name)))
        {
            var cards = Profiles.Where(card => string.Equals(card.Profile.Connection, connection.Name, StringComparison.Ordinal)).ToList();
            Connections.Add(new ModelConnectionItemViewModel(connection, cards, _strings));
        }

        RebuildProfileNames();
        RebuildSecrets();
        RebuildHostProfiles();

        OnPropertyChanged(nameof(HasSecrets));

        OnPropertiesChanged(
            nameof(StudioProfileName), nameof(HasStudioProfile),
            nameof(DefaultProfileName), nameof(IsEmpty), nameof(CanDelete), nameof(Set));
    }

    /// <summary>
    /// The entries written by hand and the RAG's choices, read from the settings document as it
    /// stands. The choices are replaced only when they changed: the picker's selection is TwoWay,
    /// and a list cleared under it is a selection WPF nulls (see <see cref="RebuildProfileNames"/>).
    /// </summary>
    private void RebuildHostProfiles()
    {
        var handWritten = _llm.HandWrittenProfiles(_set);
        HandWrittenProfiles.Clear();
        foreach (var entry in handWritten)
            HandWrittenProfiles.Add(new HandWrittenProfileViewModel(entry, _strings));

        var choices = new List<RagProfileChoice> { new(null, _strings[StudioStringKeys.ProfileRagLlmDefault]) };
        choices.AddRange(HostLlmProfiles.Offered(_set).Select(offered =>
            new RagProfileChoice(offered.Value, string.Create(CultureInfo.InvariantCulture, $"{offered.Value} — {offered.Key}"))));
        choices.AddRange(handWritten.Select(entry =>
            new RagProfileChoice(entry.Id, entry.Model is { Length: > 0 } model
                ? string.Create(CultureInfo.InvariantCulture, $"{entry.Id} — {model}")
                : entry.Id)));
        if (_llm.RagLlmProfile is { } current
            && !LlmProfilesSection.IsDefault(current)
            && !choices.Any(choice => string.Equals(choice.Id, current.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new RagProfileChoice(current.Trim(), current.Trim()));
        }

        if (!RagProfileChoices.SequenceEqual(choices))
        {
            RagProfileChoices.Clear();
            foreach (var choice in choices)
                RagProfileChoices.Add(choice);
        }

        OnPropertiesChanged(nameof(HasHandWrittenProfiles), nameof(SelectedRagProfile));
    }

    /// <summary>
    /// The language switched: the cards' titles (STUDIO-54), the host profile lines and the default
    /// choice say it again — and the open editor, the key rows and the tab's three error lines
    /// (STUDIO-56): the tab relays the signal to what lives shorter than the port.
    /// </summary>
    private void RefreshHostTexts()
    {
        foreach (var row in Profiles)
            row.RefreshTexts();
        foreach (var secret in Secrets)
            secret.RefreshTexts();
        RebuildHostProfiles();
        _editor?.RefreshTexts(ProviderCatalog());
        OnPropertiesChanged(nameof(LoadError), nameof(KeyStoreError), nameof(ProfileWriteError));
    }

    /// <summary>
    /// The settings document was replaced — a file opened, a new one started —: what the screen
    /// reads from it is read again. The RAG's profile moved: the picker says it — without touching
    /// its list, which this very selection may be writing through.
    /// </summary>
    private void OnSettingsDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName))
            Rebuild();
        else if (e.PropertyName == nameof(LlmSectionViewModel.RagLlmProfile))
            OnPropertyChanged(nameof(SelectedRagProfile));
    }

    /// <summary>
    /// The "used by" chips of the audit of 07/16: which adopted teams name each profile in
    /// their sidecar. Best-effort — an unreadable teams root simply yields no chips. An adopted team
    /// that follows the default is listed under the elected default, and counted apart (STUDIO-73);
    /// a folder without a companion file is no adopted team and is listed nowhere.
    /// </summary>
    private (Dictionary<string, List<string>> Usage, int Following) TeamsByProfile()
    {
        var usage = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var following = 0;
        if (_loadTeams is null)
            return (usage, following);

        foreach (var team in _loadTeams().Where(team => team.HasMetadata))
        {
            var profileName = team.LlmChoice.UseDefault ? _set.DefaultProfile : team.LlmChoice.Profile;
            if (profileName is not { Length: > 0 })
                continue;

            if (team.LlmChoice.UseDefault)
                following++;

            if (!usage.TryGetValue(profileName, out var teams))
                usage[profileName] = teams = [];

            teams.Add(team.Name);
        }

        return (usage, following);
    }

    /// <summary>
    /// The names feed ComboBoxes with a TwoWay SelectedItem (the assistant picker, the
    /// wizard's adopt step). Electing a profile changes no name, and clearing the list
    /// mid-write makes WPF null the selection and swallow the correcting PropertyChanged
    /// (re-entrancy guard) — the election then LOOKS unsaved. So the list is only touched
    /// when the names actually changed.
    /// </summary>
    private void RebuildProfileNames()
    {
        if (ProfileNames.SequenceEqual(_set.Profiles.Select(p => p.Name), StringComparer.Ordinal))
            return;

        ProfileNames.Clear();
        foreach (var profile in _set.Profiles)
            ProfileNames.Add(profile.Name);
    }

    /// <summary>
    /// The secrets card: one row per distinct key variable the profiles name. The value is
    /// peeked from the environment, never read from any file — there is nothing to read.
    /// </summary>
    private void RebuildSecrets()
    {
        Secrets.Clear();
        foreach (var group in _set.Profiles
                     .Where(p => p.KeyEnvName is { Length: > 0 })
                     .GroupBy(p => p.KeyEnvName!, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Secrets.Add(new SecretRowViewModel(
                group.Key,
                string.Join(", ", group.Select(p => p.Name)),
                _keyStore,
                _strings));
        }
    }
}
