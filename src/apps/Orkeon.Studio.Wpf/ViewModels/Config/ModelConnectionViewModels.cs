using System.Collections.ObjectModel;
using System.Globalization;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Core.Presets;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;

namespace Orkeon.Studio.Wpf.ViewModels.Config;

/// <summary>
/// One connection on the Models tab (<see cref="ModelConnection"/>): its name, card and endpoint once,
/// and a card per model under it — the settings teams name, <see cref="ModelProfileItemViewModel"/>. A
/// connection of one model shows that model's card alone, as a setting before connections did.
/// </summary>
public sealed class ModelConnectionItemViewModel
{
    private readonly IStudioStrings _strings;

    internal ModelConnectionItemViewModel(ModelConnection connection, IReadOnlyList<ModelProfileItemViewModel> models, IStudioStrings strings)
    {
        Connection = connection;
        Models = models;
        _strings = strings;
    }

    /// <summary>The connection being shown.</summary>
    public ModelConnection Connection { get; }

    /// <summary>The connection's name.</summary>
    public string Name => Connection.Name;

    /// <summary>The title of the connection's card, in the interface's language.</summary>
    public string Provider => Models.Count > 0 ? Models[0].Provider : "";

    /// <summary>A card per model, in the connection's order.</summary>
    public IReadOnlyList<ModelProfileItemViewModel> Models { get; }

    /// <summary>Whether the connection declares several models: its header and its cards are then shown as a group.</summary>
    public bool HasSeveralModels => Models.Count > 1;

    /// <summary>The header's line under the name: « DeepSeek · 2 models ».</summary>
    public string HeaderLine => $"{Provider} · {_strings.Format(StudioStringKeys.ProfileConnectionModelCount, Models.Count.ToString(CultureInfo.CurrentCulture))}";
}

/// <summary>
/// One model of the connection the editor holds: its alias, its identifier and, in expert mode, the
/// settings it pins over the connection's — an empty field inherits it. Its pair is named after the
/// connection (<see cref="ModelConnection.PairName"/>), and judged as a host profile against the other
/// settings and the models above it (<see cref="HostLlmProfiles.Check(string, IReadOnlyCollection{string}, IReadOnlyList{string}, bool, ModelProfileSet, Orkeon.Studio.Core.Configuration.AppSettingsDocument)"/>).
/// </summary>
public sealed class ConnectionModelRowViewModel : ObservableObject
{
    private readonly ModelProfileEditorViewModel _editor;
    private string _alias;
    private string? _model;
    private bool? _thinking;
    private string _effortText;
    private string _maxTokensText;
    private string _timeoutText;
    private string _temperatureText;
    private bool _isSelected;

    internal ConnectionModelRowViewModel(ModelProfileEditorViewModel editor, ConnectionModel model, string? previousPairName, IReadOnlyList<string> usedBy)
    {
        _editor = editor;
        PreviousPairName = previousPairName;
        UsedByTeams = usedBy;
        _alias = model.Alias ?? "";
        _model = model.Model;
        _thinking = model.ThinkingEnabled;
        _effortText = model.ThinkingEffort ?? "";
        _maxTokensText = Text(model.MaxTokens);
        _timeoutText = Text(model.TimeoutSeconds);
        _temperatureText = model.Temperature?.ToString(CultureInfo.InvariantCulture) ?? "";
        // What the model pins that the row does not show is kept as it was.
        Kept = model with { Alias = null, Model = null, ThinkingEnabled = null, ThinkingEffort = null, MaxTokens = null, TimeoutSeconds = null, Temperature = null };
        RemoveCommand = new RelayCommand(() => _editor.RemoveModel(this), () => _editor.Models.Count > 1);
        SelectCommand = new RelayCommand(() => _editor.SelectedModelRow = this);
    }

    /// <summary>The pair this model was when the editor opened; null for a model the edit added.</summary>
    public string? PreviousPairName { get; }

    /// <summary>The teams naming this model's pair when the editor opened: removing it leaves them without their setting.</summary>
    public IReadOnlyList<string> UsedByTeams { get; }

    /// <summary>The line saying which teams name the model, or null.</summary>
    public string? UsedByText => UsedByTeams.Count > 0
        ? _editor.Strings.Format(StudioStringKeys.ProfileModelUsedBy, string.Join(", ", UsedByTeams))
        : null;

    private ConnectionModel Kept { get; }

    /// <summary>The model's short name in its pair; optional while the connection has one model.</summary>
    public string Alias
    {
        get => _alias;
        set
        {
            if (SetProperty(ref _alias, value ?? ""))
                _editor.OnModelRowsChanged();
        }
    }

    /// <summary>Model identifier, as the endpoint expects it.</summary>
    public string? Model
    {
        get => _model;
        set
        {
            if (!SetProperty(ref _model, value))
                return;

            _editor.OnModelRowChanged(this);
        }
    }

    /// <summary>
    /// The thinking switch the model pins: the connection's, on, or off. Held by its value and found
    /// again among the editor's choices, so a language switch, which rebuilds them under new labels,
    /// keeps the row on its choice.
    /// </summary>
    public ThinkingChoice Thinking
    {
        get => _editor.ModelThinkingChoices.FirstOrDefault(choice => choice.Value == _thinking) ?? _editor.ModelThinkingChoices[0];
        set
        {
            if (value is not null && SetProperty(ref _thinking, value.Value))
                _editor.OnModelRowChanged(this);
        }
    }

    /// <summary>The reasoning effort the model pins; empty for the connection's.</summary>
    public string EffortText
    {
        get => _effortText;
        set => SetProperty(ref _effortText, value ?? "");
    }

    /// <summary>The response budget the model pins; empty for the connection's.</summary>
    public string MaxTokensText
    {
        get => _maxTokensText;
        set
        {
            if (SetProperty(ref _maxTokensText, value ?? ""))
                _editor.OnModelRowsChanged();
        }
    }

    /// <summary>The timeout, in seconds, the model pins; empty for the connection's.</summary>
    public string TimeoutText
    {
        get => _timeoutText;
        set
        {
            if (SetProperty(ref _timeoutText, value ?? ""))
                _editor.OnModelRowsChanged();
        }
    }

    /// <summary>The temperature the model pins; empty for the connection's.</summary>
    public string TemperatureText
    {
        get => _temperatureText;
        set
        {
            if (SetProperty(ref _temperatureText, value ?? ""))
                _editor.OnModelRowsChanged();
        }
    }

    /// <summary>Whether the row is the one the connection test, the model list and the budget hint are about.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    /// <summary>The name of the model's pair, as teams and the elections will name it.</summary>
    public string PairName => _editor.Name.Trim().Length == 0 ? "" : ModelConnection.PairName(_editor.Name.Trim(), _alias);

    /// <summary>Whether the alias is missing while the connection has several models.</summary>
    public bool AliasMissing => _editor.Models.Count > 1 && _alias.Trim().Length == 0;

    /// <summary>Whether a model above this one has the same alias.</summary>
    public bool AliasTaken => _alias.Trim().Length > 0 && _editor.Models.TakeWhile(row => !ReferenceEquals(row, this))
        .Any(row => string.Equals(row.Alias.Trim(), _alias.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether another setting already bears the pair's name.</summary>
    public bool NameCollision => PairName.Length > 0 && _editor.IsPairNameTaken(PairName);

    /// <summary>What the pair would be as a host profile.</summary>
    public HostProfileCheck HostProfile => _editor.CheckPair(this);

    /// <summary>The line under the model: the alias problem, <c>profile: &lt;id&gt;</c>, or why no crew can name it.</summary>
    public string? HostIdText =>
        AliasMissing ? _editor.Strings[StudioStringKeys.ProfileAliasRequired]
        : AliasTaken ? _editor.Strings[StudioStringKeys.ProfileAliasTaken]
        : NameCollision ? _editor.Strings[StudioStringKeys.ProfilePairNameTaken]
        : HostProfileText.Describe(HostProfile, _editor.Strings);

    /// <summary>Whether that line is a problem.</summary>
    public bool IsHostIdIssue => AliasMissing || AliasTaken || NameCollision || HostProfileText.IsIssue(HostProfile);

    /// <summary>Whether the row can be saved: an alias when one is needed, no collision, numbers that parse.</summary>
    public bool IsValid =>
        !AliasMissing && !AliasTaken && !NameCollision && !HostProfile.BlocksSave
        && (_maxTokensText.Trim().Length == 0 || ModelProfileEditorViewModel.PositiveInt(_maxTokensText) is not null)
        && (_timeoutText.Trim().Length == 0 || ModelProfileEditorViewModel.PositiveInt(_timeoutText) is not null)
        && (_temperatureText.Trim().Length == 0 || ModelProfileEditorViewModel.FiniteDouble(_temperatureText) is not null);

    /// <summary>The response-budget hint for this model, as the connection's card reads it.</summary>
    public string MaxTokensHint => _editor.MaxTokensHintFor(_model);

    /// <summary>Removes the model from the connection (disabled on the last one).</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>Makes this row the one the connection test is about.</summary>
    public RelayCommand SelectCommand { get; }

    /// <summary>The model as the connection stores it.</summary>
    internal ConnectionModel ToModel() => Kept with
    {
        Alias = _alias.Trim().Length == 0 ? null : _alias.Trim(),
        Model = _model,
        ThinkingEnabled = _thinking,
        ThinkingEffort = _effortText.Trim().Length == 0 ? null : _effortText.Trim(),
        MaxTokens = ModelProfileEditorViewModel.PositiveInt(_maxTokensText),
        TimeoutSeconds = ModelProfileEditorViewModel.PositiveInt(_timeoutText),
        Temperature = ModelProfileEditorViewModel.FiniteDouble(_temperatureText),
    };

    /// <summary>The name, the rows or the language changed: every derived line says it again.</summary>
    internal void Refresh() =>
        OnPropertiesChanged(
            nameof(PairName), nameof(AliasMissing), nameof(AliasTaken), nameof(NameCollision), nameof(HostProfile),
            nameof(HostIdText), nameof(IsHostIdIssue), nameof(IsValid), nameof(MaxTokensHint), nameof(UsedByText), nameof(Thinking));

    internal void RaiseCanRemove() => RemoveCommand.RaiseCanExecuteChanged();

    private static string Text(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
}

