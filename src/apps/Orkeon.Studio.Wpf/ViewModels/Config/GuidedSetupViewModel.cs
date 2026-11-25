using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Orkeon.Constants.Llm;
using Orkeon.Infrastructure.Decision.SystemOne;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Decision;
using Orkeon.Studio.Core.Llm;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Core.Presets;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;

namespace Orkeon.Studio.Wpf.ViewModels.Config;

/// <summary>
/// The guided setup: a provider and its key, its models, the decision provider if any, then the
/// tests of what was entered. Everything is written at « Finish », nothing before; closing writes nothing.
/// </summary>
public sealed class GuidedSetupViewModel : ObservableObject
{
    private const int StepCount = 4;

    private readonly ConfigTabViewModel _config;
    private readonly ModelProfilesViewModel _profiles;
    private readonly ModelTestSeams _seams;
    private readonly ILlmEndpointProbe _probe;
    private readonly IApiKeyStore _keyStore;
    private readonly IStudioStrings _strings;
    private readonly Func<bool> _savesAtOnce;
    private CancellationTokenSource? _verifying;
    private Func<string>? _verifyText;
    private IReadOnlyList<string> _found = [];
    private bool _isOpen;
    private int _step = 1;
    private LlmPresetInfo? _provider;
    private string _baseUrl = "";
    private string _apiKeyInput = "";
    private string _name = "";
    private string _modelFilter = "";
    private object? _assistant;
    private object? _default;
    private string _workspaceId = "";
    private Func<string>? _finishFailure;
    private DecisionProviderChoiceItem? _decision;
    private string _decisionKeyInput = "";
    private bool _usesLadder;
    private List<string> _ladder = [];
    private bool _isFinishing;

    // Every key typed and tested since the dialog opened: one replaced since stays masked in the journal.
    private readonly HashSet<string> _testedKeys = new(StringComparer.Ordinal);

    internal GuidedSetupViewModel(
        ConfigTabViewModel config,
        ModelProfilesViewModel profiles,
        ModelTestSeams seams,
        Func<bool> savesAtOnce)
    {
        _config = config;
        _profiles = profiles;
        _probe = seams.Probe ?? HttpLlmEndpointProbe.ForCurrentMachine();
        _keyStore = seams.KeyStore ?? new EnvironmentApiKeyStore();
        _strings = seams.Strings ?? EnglishStudioStrings.Instance;
        _savesAtOnce = savesAtOnce;
        _seams = seams with
        {
            Probe = _probe,
            KeyStore = _keyStore,
            Strings = _strings,
            Mask = MaskKeys,
            ThinkingOverrides = seams.ThinkingOverrides ?? (() => profiles.ModelThinkingOverrides),
        };
        Tests = new ModelTestRunner(_seams);
        Tests.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModelTestRunner.IsRunning))
                RaiseCommands();
        };
        StartCommand = new RelayCommand(Start);
        CancelCommand = new RelayCommand(Close, () => !_isFinishing);
        BackCommand = new RelayCommand(() => GoTo(_step - 1), () => _step > 1 && !_isFinishing && !HasFinishFailure);
        NextCommand = new RelayCommand(() => GoTo(_step + 1), () => _step < StepCount && Problem is null);
        FinishCommand = new AsyncRelayCommand(FinishAsync, () => _step == StepCount && Problem is null && !_isFinishing && !HasFinishFailure);
        VerifyCommand = new AsyncRelayCommand(VerifyAsync, () => _provider is not null && _verifying is null && AddressProblem is null);
        SelectProviderCommand = new RelayCommand(p => Provider = p as LlmPresetInfo);
        PickModelCommand = new RelayCommand(p => { if (p is string model) PickModel(model); });
        AddModelCommand = new RelayCommand(() => AddModel(""));
        _strings.CultureChanged += (_, _) =>
        {
            if (_isOpen)
                OnPropertyChanged(string.Empty);
        };
    }

    /// <summary>The tests of what was entered, on step 4.</summary>
    public ModelTestRunner Tests { get; }

    /// <summary>Whether the dialog shows.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    /// <summary>Whether the dialog is writing: the document is saved once, at the end.</summary>
    internal bool IsFinishing => _isFinishing;

    /// <summary>The step shown, from 1.</summary>
    public int Step => _step;

    /// <summary>« Step 2 of 4 — The models ».</summary>
    public string StepText => _strings.Format(
        StudioStringKeys.GuidedStep,
        _step,
        StepCount,
        _strings[_step switch
        {
            1 => StudioStringKeys.GuidedStepProvider,
            2 => StudioStringKeys.GuidedStepModels,
            3 => StudioStringKeys.GuidedStepDecision,
            _ => StudioStringKeys.GuidedStepTest,
        }]);

    /// <summary>Whether the provider step shows.</summary>
    public bool IsProviderStep => _step == 1;

    /// <summary>Whether the models step shows.</summary>
    public bool IsModelsStep => _step == 2;

    /// <summary>Whether the decision step shows.</summary>
    public bool IsDecisionStep => _step == 3;

    /// <summary>Whether the tests step shows.</summary>
    public bool IsTestStep => _step == StepCount;

    /// <summary>Opens the dialog, blank.</summary>
    public RelayCommand StartCommand { get; }

    /// <summary>Closes it; nothing is written.</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>The step before.</summary>
    public RelayCommand BackCommand { get; }

    /// <summary>The step after, once this one is complete.</summary>
    public RelayCommand NextCommand { get; }

    /// <summary>Writes everything and closes.</summary>
    public AsyncRelayCommand FinishCommand { get; }

    /// <summary>Asks the provider its models, with the key typed.</summary>
    public AsyncRelayCommand VerifyCommand { get; }

    /// <summary>Picks a provider's card.</summary>
    public RelayCommand SelectProviderCommand { get; }

    /// <summary>Adds a model of the list found.</summary>
    public RelayCommand PickModelCommand { get; }

    /// <summary>Adds an empty model, its identifier to type.</summary>
    public RelayCommand AddModelCommand { get; }

    // -- Step 1: the provider and its key --

    private IEnumerable<LlmPresetInfo> Catalog => LlmPresets.ProviderCatalogFor(_strings);

    /// <summary>The providers that run on this machine.</summary>
    public IEnumerable<LlmPresetInfo> LocalProviders => Catalog.Where(p => p.Kind == LlmPresetKind.Local);

    /// <summary>The hosted ones.</summary>
    public IEnumerable<LlmPresetInfo> CloudProviders => Catalog.Where(p => p.Kind == LlmPresetKind.Cloud);

    /// <summary>The catch-all.</summary>
    public IEnumerable<LlmPresetInfo> OtherProviders => Catalog.Where(p => p.Kind == LlmPresetKind.Other);

    /// <summary>The provider picked; another one starts the models over.</summary>
    public LlmPresetInfo? Provider
    {
        get => _provider;
        set
        {
            if (value is null || value.Name == _provider?.Name)
                return;

            _provider = value;
            _baseUrl = value.DefaultBaseUrl ?? "";
            _apiKeyInput = _workspaceId = "";
            _name = FreeName(value.Title);
            Models.Clear();
            if (!string.IsNullOrWhiteSpace(value.DefaultModel))
                Models.Add(new GuidedModelViewModel(this, value.DefaultModel));
            ForgetVerification();
            OnPropertiesChanged(
                nameof(Provider), nameof(BaseUrl), nameof(ApiKeyInput), nameof(Name), nameof(RequiresApiKey), nameof(AsksAddress),
                nameof(ShowWorkspace), nameof(WorkspaceId),
                nameof(KeyStatusText), nameof(KeyConsoleUrl));
            OnModelsChanged();
        }
    }

    /// <summary>Whether the address is asked: the card has none of its own.</summary>
    public bool AsksAddress =>
        _provider is { } provider && (provider.Kind != LlmPresetKind.Cloud || AsksItsOwnAddress(provider));

    private static bool AsksItsOwnAddress(LlmPresetInfo card) => !LlmSection.IsAbsoluteHttpUrl(card.DefaultBaseUrl ?? "");

    /// <summary>The address.</summary>
    [SuppressMessage("Design", "CA1056",
        Justification = "A user-typed form value: a half-typed URL must be carried and re-shown, which System.Uri cannot do.")]
    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (!SetProperty(ref _baseUrl, value ?? ""))
                return;
            ForgetVerification();
            OnPropertyChanged(nameof(ShowWorkspace));
            OnModelsChanged();
        }
    }

    /// <summary>Whether the Anthropic workspace is asked: an identity-linked key answers in none without it.</summary>
    public bool ShowWorkspace => StudioEndpointReading.ProviderOf(_baseUrl) == LlmProviderKeys.Anthropic;

    /// <summary>The Anthropic workspace the key acts in.</summary>
    public string WorkspaceId
    {
        get => _workspaceId;
        set
        {
            if (SetProperty(ref _workspaceId, value ?? ""))
                ForgetVerification();
        }
    }

    private string? Workspace => ShowWorkspace && _workspaceId.Trim() is { Length: > 0 } id ? id : null;

    /// <summary>Whether the provider needs a key.</summary>
    public bool RequiresApiKey => _provider?.RequiresApiKey == true;

    /// <summary>Where the provider's keys are made.</summary>
    [SuppressMessage("Design", "CA1056", Justification = "Shown as text, like the catalogue carries it.")]
    public string? KeyConsoleUrl => _provider?.KeyConsoleUrl;

    private string KeyVariable => _provider?.DefaultApiKeyEnv ?? LlmPresets.DefaultApiKeyEnv;

    /// <summary>The key typed, held until « Finish » puts it in the user environment; never read back from it.</summary>
    public string ApiKeyInput
    {
        get => _apiKeyInput;
        set
        {
            if (!SetProperty(ref _apiKeyInput, value ?? ""))
                return;
            ForgetVerification();
        }
    }

    /// <summary>Whether a key is in place already: leaving the field empty keeps it.</summary>
    public string KeyStatusText => _strings[_keyStore.Peek(KeyVariable) is null
        ? StudioStringKeys.GuidedKeyNone
        : StudioStringKeys.GuidedKeyInPlace];

    /// <summary>What the provider answered.</summary>
    public string? VerifyText => _verifyText?.Invoke();

    /// <summary>The models found, narrowed by the filter.</summary>
    public IReadOnlyList<string> FoundModels =>
        [.. _found.Where(name => name.Contains(_modelFilter.Trim(), StringComparison.OrdinalIgnoreCase))];

    /// <summary>Whether the provider listed its models.</summary>
    public bool HasFoundModels => _found.Count > 0;

    /// <summary>Narrows the list found.</summary>
    public string ModelFilter
    {
        get => _modelFilter;
        set
        {
            if (SetProperty(ref _modelFilter, value ?? ""))
                OnPropertyChanged(nameof(FoundModels));
        }
    }

    // -- Step 2: the models --

    /// <summary>The connection's name.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? ""))
                OnModelsChanged();
        }
    }

    /// <summary>The models picked.</summary>
    public ObservableCollection<GuidedModelViewModel> Models { get; } = [];

    /// <summary>Whether the aliases are asked: one model is named by its connection.</summary>
    public bool HasSeveralModels => Models.Count > 1;

    /// <summary>The settings the assistant and the default can be: the ones there are, then the ones being made.</summary>
    public IReadOnlyList<string> ElectionChoices =>
        [.. _profiles.Set.Profiles.Select(profile => profile.Name).Concat(NewNames).Distinct(StringComparer.Ordinal)];

    /// <summary>The setting the assistant uses: its own when it has one, else the first model.</summary>
    public string? Assistant
    {
        get => ElectionOf(_assistant, _profiles.StudioProfileName);
        set
        {
            // A null comes from the list rebuilt under its selection, never from a pick.
            if (value is null || value == Assistant)
                return;
            _assistant = PickOf(value);
            OnPropertyChanged();
        }
    }

    /// <summary>The setting the teams run on by default.</summary>
    public string? Default
    {
        get => ElectionOf(_default, _profiles.DefaultProfileName);
        set
        {
            if (value is null || value == Default)
                return;
            _default = PickOf(value);
            OnPropertyChanged();
        }
    }

    // -- Step 3: the decision provider --

    /// <summary>« Later », then each decision provider.</summary>
    public IReadOnlyList<DecisionProviderChoiceItem> DecisionChoices =>
    [
        new(null, _strings[StudioStringKeys.GuidedDecisionLater]),
        .. DecisionSection.Providers.Select(d => new DecisionProviderChoiceItem(d.Key, d.DisplayName)),
    ];

    /// <summary>The decision provider picked; « Later » leaves the settings as they are.</summary>
    public DecisionProviderChoiceItem SelectedDecision
    {
        get => DecisionChoices.First(choice => choice.Key == _decision?.Key);
        set
        {
            if (value is null || value.Key == _decision?.Key)
                return;

            _decision = value;
            _decisionKeyInput = "";
            OnPropertiesChanged(
                nameof(SelectedDecision), nameof(HasDecision), nameof(DecisionKeyRequired), nameof(DecisionKeyInput),
                nameof(DecisionKeyStatusText), nameof(OffersLadder));
        }
    }

    private DecisionProviderDescriptor? DecisionDescriptor =>
        _decision?.Key is { } key ? DecisionProviderDescriptor.Find(key) : null;

    /// <summary>Whether a decision provider is picked.</summary>
    public bool HasDecision => DecisionDescriptor is not null;

    /// <summary>Whether it needs a key.</summary>
    public bool DecisionKeyRequired => DecisionDescriptor?.KeyRequired == true;

    private string? DecisionKeyVariable =>
        DecisionDescriptor is { } descriptor ? _config.Decision.DocumentForCards.Decision.For(descriptor).EffectiveApiKeyEnvVar : null;

    /// <summary>The decision provider's key, held until « Finish ».</summary>
    public string DecisionKeyInput
    {
        get => _decisionKeyInput;
        set => SetProperty(ref _decisionKeyInput, value ?? "");
    }

    /// <summary>Whether its key is in place already.</summary>
    public string DecisionKeyStatusText =>
        _strings[DecisionKeyVariable is { } variable && _keyStore.Peek(variable) is not null
            ? StudioStringKeys.GuidedKeyInPlace
            : StudioStringKeys.GuidedKeyNone];

    /// <summary>Whether the ladder is offered: a provider decides, and there are two models to pick among.</summary>
    public bool OffersLadder => HasDecision && HasSeveralModels;

    /// <summary>Whether the decision provider picks among the models; off at first where a ladder exists already.</summary>
    public bool UsesLadder
    {
        get => _usesLadder;
        set => SetProperty(ref _usesLadder, value);
    }

    /// <summary>The ladder, cheapest first.</summary>
    public IReadOnlyList<GuidedRungViewModel> Rungs =>
        [.. _ladder.Select((name, index) => new GuidedRungViewModel(this, index, _ladder.Count, name))];

    // -- What stands in the way --

    /// <summary>What keeps the current step from the next one; null when nothing does.</summary>
    public string? Problem => _step switch
    {
        1 => _provider is null ? _strings[StudioStringKeys.GuidedPickProvider] : AddressProblem,
        _ => _provider is null ? _strings[StudioStringKeys.GuidedPickProvider] : AddressProblem ?? ModelsProblem,
    };

    private string? AddressProblem =>
        LlmSection.IsAbsoluteHttpUrl(_baseUrl.Trim()) ? null : _strings[StudioStringKeys.GuidedAddressInvalid];

    private string? ModelsProblem
    {
        get
        {
            if (_name.Trim().Length == 0)
                return _strings[StudioStringKeys.GuidedNameMissing];
            if (Models.Count == 0 || Models.Any(row => row.Model.Trim().Length == 0))
                return _strings[StudioStringKeys.GuidedModelMissing];

            var names = NewNames;
            if (_profiles.IsConnectionNameTaken(_name.Trim(), null)
                || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() < names.Count
                || (HasSeveralModels && Models.Any(row => row.Alias.Trim().Length == 0))
                || names.Any(name => _profiles.IsPairNameTaken(name, null)))
            {
                return _strings[StudioStringKeys.GuidedNameTaken];
            }

            for (var index = 0; index < names.Count; index++)
            {
                var check = _profiles.CheckHostProfile(names[index], [], [.. names.Take(index)], describesProvider: true);
                if (check.BlocksSave)
                    return HostProfileText.Describe(check, _strings);
            }

            return null;
        }
    }

    internal void OnModelsChanged()
    {
        var names = NewNames;
        // The order chosen survives an edit: a rung whose model is still there keeps its place.
        if (_ladder.Count == names.Count && _ladder.Except(names).Count() <= 1)
        {
            var replacement = names.Except(_ladder).FirstOrDefault();
            _ladder = [.. _ladder.Select(name => names.Contains(name) ? name : replacement ?? name)];
        }
        else
        {
            _ladder = [.. _ladder.Where(names.Contains), .. names.Where(name => !_ladder.Contains(name))];
        }

        OnPropertiesChanged(
            nameof(HasSeveralModels), nameof(ElectionChoices), nameof(Assistant), nameof(Default),
            nameof(OffersLadder), nameof(Rungs), nameof(Problem));
        RaiseCommands();
    }

    internal void RemoveModel(GuidedModelViewModel row)
    {
        if (Models.Remove(row))
            OnModelsChanged();
    }

    internal void MoveRung(int index, int offset)
    {
        var target = index + offset;
        if (index < 0 || index >= _ladder.Count || target < 0 || target >= _ladder.Count)
            return;

        (_ladder[index], _ladder[target]) = (_ladder[target], _ladder[index]);
        OnPropertyChanged(nameof(Rungs));
    }

    /// <summary>Stops what is in flight: the window closes or another screen is shown.</summary>
    public void StopActivity()
    {
        if (_verifying is not null)
            ForgetVerification();
        Tests.Stop();
    }

    private IReadOnlyList<string> NewNames =>
        _name.Trim().Length == 0 ? [] : [.. BuildConnection().PairNames];

    /// <summary>A pick among the models being made is the row itself: it follows its name.</summary>
    private object PickOf(string name)
    {
        var index = NewNames.ToList().IndexOf(name);
        return index >= 0 && index < Models.Count ? Models[index] : name;
    }

    private string? ElectionOf(object? pick, string? current)
    {
        var choices = ElectionChoices;
        var names = NewNames;
        var row = pick is GuidedModelViewModel model ? Models.IndexOf(model) : -1;
        var picked = row >= 0 && row < names.Count ? names[row] : pick as string;
        if (picked is not null && choices.Contains(picked, StringComparer.Ordinal))
            return picked;
        return current is not null && choices.Contains(current, StringComparer.Ordinal) ? current : names.Count > 0 ? names[0] : null;
    }

    private ModelConnection BuildConnection() => new()
    {
        Name = _name.Trim(),
        Provider = _provider?.Name,
        BaseUrl = _baseUrl.Trim() is { Length: > 0 } address ? address : null,
        KeyEnvName = RequiresApiKey ? KeyVariable : null,
        WorkspaceId = Workspace,
        TimeoutSeconds = _provider?.RecommendedTimeoutSeconds,
        Models = Models.Count == 1
            ? [new ConnectionModel { Model = Models[0].Model.Trim() }]
            : [.. Models.Select(row => new ConnectionModel { Alias = row.Alias.Trim(), Model = row.Model.Trim() })],
    };

    private string FreeName(string title)
    {
        var name = title;
        for (var number = 2; IsTaken(name); number++)
            name = $"{title} {number}";
        return name;
    }

    private bool IsTaken(string name) =>
        _profiles.IsConnectionNameTaken(name, null)
        || _profiles.IsPairNameTaken(name, null)
        || _profiles.CheckHostProfile(name, [], [], describesProvider: true).BlocksSave;

    /// <summary>The key a run of what was entered would read: the one typed, else the one in place.</summary>
    private string? KeyOf(string variable)
    {
        if (DecisionKeyVariable == variable && _decisionKeyInput.Trim() is { Length: > 0 } decision)
            return decision;
        if (RequiresApiKey && KeyVariable == variable && _apiKeyInput.Trim() is { Length: > 0 } typed)
            return typed;
        return _keyStore.Peek(variable);
    }

    private string MaskKeys(string text) =>
        SecretMasker.Apply(_profiles.MaskKeys(text), [.. _testedKeys]);

    private void Start()
    {
        _step = 1;
        _provider = null;
        _finishFailure = null;
        _baseUrl = _apiKeyInput = _workspaceId = _name = _decisionKeyInput = _modelFilter = "";
        _assistant = _default = null;
        _decision = null;
        _ladder = [];
        _usesLadder = _config.Decision.DocumentForCards.Decision.RouteLadder.Count == 0;
        _verifyText = null;
        _found = [];
        Models.Clear();
        Tests.SetRows([]);
        Tests.ClearJournalCommand.Execute(null);
        IsOpen = true;
        OnPropertyChanged(string.Empty);
        RaiseCommands();
    }

    private void Close()
    {
        StopActivity();
        // A key typed and not kept does not outlive the dialog.
        _apiKeyInput = _decisionKeyInput = "";
        Tests.ClearJournalCommand.Execute(null);
        _testedKeys.Clear();
        OnPropertiesChanged(nameof(ApiKeyInput), nameof(DecisionKeyInput));
        IsOpen = false;
    }

    private void GoTo(int step)
    {
        if (step is < 1 or > StepCount)
            return;

        // A check of the key goes on: its model list is for the next step.
        Tests.Stop();
        _step = step;
        if (IsTestStep)
            PrepareTests();
        OnPropertiesChanged(
            nameof(Step), nameof(StepText), nameof(IsProviderStep), nameof(IsModelsStep), nameof(IsDecisionStep), nameof(IsTestStep),
            nameof(Problem));
        RaiseCommands();
    }

    private void PrepareTests()
    {
        foreach (var key in new[] { _apiKeyInput.Trim(), _decisionKeyInput.Trim() }.Where(key => key.Length > 0))
            _testedKeys.Add(key);

        List<ModelTestRowViewModel> rows =
            [.. BuildConnection().Flatten().Select(profile => new ModelTestRowViewModel(Tests, profile, KeyOf))];
        if (DecisionDescriptor is { } descriptor)
        {
            rows.Add(new ModelTestRowViewModel(
                Tests,
                descriptor.DisplayName,
                () => DecisionProbeRequest.For(_config.Decision.DocumentForCards.Decision, descriptor, KeyOf)));
        }

        Tests.SetRows(rows);
    }

    private async Task VerifyAsync()
    {
        if (_provider is not { } provider || _verifying is not null)
            return;

        var key = RequiresApiKey ? KeyOf(KeyVariable) : LlmPresets.PlaceholderKeyOf(provider.Name);
        if (RequiresApiKey && string.IsNullOrWhiteSpace(key))
        {
            ShowVerification(() => _strings[StudioStringKeys.ProfileKeyMissingTest], []);
            return;
        }

        if (ModelTest.IsOllamaReadElsewhere(provider.Name, _baseUrl))
        {
            var elsewhere = StudioEndpointReading.Of(_baseUrl)?.ProviderKey ?? "";
            ShowVerification(() => _strings.Format(StudioStringKeys.ProfileOllamaReadElsewhere, elsewhere), []);
            return;
        }

        using var source = new CancellationTokenSource();
        _verifying = source;
        ShowVerification(() => _strings[StudioStringKeys.SandboxRunning], []);
        RaiseCommands();
        try
        {
            var result = await _probe.ProbeAsync(
                new LlmProbeRequest
                {
                    BaseUrl = _baseUrl.Trim(),
                    ApiKey = key,
                    WorkspaceId = Workspace,
                    CheckCompletion = false,
                    Timeout = LlmProbeRequest.TimeoutFor(null),
                },
                source.Token).ConfigureAwait(true);
            if (!source.IsCancellationRequested)
                ShowVerification(() => LlmProbeText.Describe(result, _strings), result.Succeeded ? [.. result.ModelNames] : []);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // Closed, or a field the answer was about changed: whoever did it says what shows.
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !source.IsCancellationRequested)
        {
            var cause = SecretMasker.Apply(ex.Message, [key]);
            ShowVerification(() => cause, []);
        }
        finally
        {
            if (ReferenceEquals(_verifying, source))
                _verifying = null;
            RaiseCommands();
        }
    }

    private void ForgetVerification()
    {
        _verifying?.Cancel();
        _verifying = null;
        ShowVerification(null, []);
        RaiseCommands();
    }

    private void ShowVerification(Func<string>? text, IReadOnlyList<string> models)
    {
        _verifyText = text;
        _found = models;
        OnPropertiesChanged(nameof(VerifyText), nameof(FoundModels), nameof(HasFoundModels));
    }

    private void PickModel(string model)
    {
        if (Models.Any(row => string.Equals(row.Model.Trim(), model, StringComparison.Ordinal)))
            return;

        // An empty row waiting for its identifier takes it.
        if (Models.FirstOrDefault(row => row.Model.Trim().Length == 0) is { } empty)
            empty.Model = model;
        else
            AddModel(model);
    }

    private void AddModel(string model)
    {
        Models.Add(new GuidedModelViewModel(this, model));
        OnModelsChanged();
    }

    private async Task FinishAsync()
    {
        if (Problem is not null || _isFinishing)
            return;

        StopActivity();
        _isFinishing = true;
        RaiseCommands();
        var kept = new List<Task>();
        try
        {
            // The expert who has unsaved edits saves them all with the bar, these included.
            var saves = _savesAtOnce() || !_config.HasPendingChanges;
            var (assistant, elected) = (Assistant, Default);
            _profiles.ReportKeyStoreError(null);
            if (RequiresApiKey && _apiKeyInput.Trim() is { Length: > 0 } key)
                kept.Add(_keyStore.SaveAsync(KeyVariable, key));
            if (DecisionKeyRequired && _decisionKeyInput.Trim() is { Length: > 0 } decisionKey)
                kept.Add(_keyStore.SaveAsync(DecisionKeyVariable!, decisionKey));

            _profiles.CommitEdit(BuildConnection(), previousName: null, previousPairNames: null);
            if (assistant is not null)
                _profiles.StudioProfileName = assistant;
            if (elected is not null)
                _profiles.SetDefault(elected);
            WriteDecision();
            if (saves && !await _config.SaveAsync().ConfigureAwait(true))
                FailFinish(() => _config.StatusMessage ?? "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var cause = MaskKeys(ex.Message);
            FailFinish(() => cause);
        }
        finally
        {
            await ReportKeysAsync(kept).ConfigureAwait(true);
            _isFinishing = false;
            RaiseCommands();
        }

        if (!HasFinishFailure)
            Close();
    }

    /// <summary>
    /// What kept Finish from writing everything. Part of it may be in place by then, so Finish is not run
    /// again: the dialog stays to say it, with its closing as the one way on.
    /// </summary>
    public string? FinishFailure => _finishFailure is { } cause
        ? _strings.Format(StudioStringKeys.GuidedFinishFailed, cause())
        : null;

    /// <summary>Whether Finish left something unwritten.</summary>
    public bool HasFinishFailure => _finishFailure is not null;

    private void FailFinish(Func<string> cause)
    {
        _finishFailure = cause;
        OnPropertiesChanged(nameof(FinishFailure), nameof(HasFinishFailure));
    }

    private void WriteDecision()
    {
        var section = _config.Decision;
        if (DecisionDescriptor is { } descriptor)
        {
            section.SelectedProvider = section.ProviderChoices.First(choice => choice.Key == descriptor.Key);
            section.Enabled = true;
            foreach (var card in section.Providers)
            {
                card.Refresh();
                card.RefreshTexts();
            }

            var rungs = _ladder.Select(_profiles.OfferedHostId).OfType<string>().ToList();
            if (OffersLadder && _usesLadder && rungs.Count > 1)
                section.RouteLadderText = string.Join(", ", rungs);
        }

        // The keys are in place since: the status reads them.
        section.RefreshVerdict();
    }

    private async Task ReportKeysAsync(IReadOnlyList<Task> kept)
    {
        foreach (var key in kept)
        {
            try
            {
                await key.ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _profiles.ReportKeyStoreError(ex.Message);
            }
        }
    }

    private void RaiseCommands()
    {
        CancelCommand.RaiseCanExecuteChanged();
        BackCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        FinishCommand.RaiseCanExecuteChanged();
        VerifyCommand.RaiseCanExecuteChanged();
    }
}
