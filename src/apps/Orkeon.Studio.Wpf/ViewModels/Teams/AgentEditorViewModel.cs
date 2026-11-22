using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Orkeon.Domain.FileSystem;
using Orkeon.Studio.Core.Forge;
using Orkeon.Studio.Core.FileSystem;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Wpf.ViewModels.Mounts;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;

namespace Orkeon.Studio.Wpf.ViewModels.Teams;

/// <summary>One togglable capability chip of the agent editor.</summary>
public sealed class AgentToolChipViewModel : ObservableObject
{
    private bool _isSelected;

    internal AgentToolChipViewModel(string tool, WizardToolChip chip, bool isSelected, Action changed)
    {
        ArgumentNullException.ThrowIfNull(chip);

        Tool = tool;
        Label = chip.Label;
        Icon = chip.Icon;
        _isSelected = isSelected;
        ToggleCommand = new RelayCommand(() =>
        {
            IsSelected = !IsSelected;
            changed();
        });
    }

    /// <summary>The tool identifier, as the blueprint spells it: the tooltip, and the expert's mono line.</summary>
    public string Tool { get; }

    /// <summary>What the tool does, in words (« lire /docs »); the identifier when Studio has no words for it (UX lot 02).</summary>
    public string Label { get; }

    /// <summary>The chip's Lucide icon.</summary>
    public string Icon { get; }

    /// <summary>Whether the agent keeps this capability.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        private set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Flips the chip.</summary>
    public RelayCommand ToggleCommand { get; }
}

/// <summary>
/// The agent editor modal (remediation v2, F-01): name ↔ the blueprint's <c>role</c>,
/// "ce qu'il fait" ↔ its <c>goal</c>, capabilities as togglable chips. Save hands the
/// amended blueprint JSON to the wizard, which sends it over the engine's <c>edit</c>
/// arbitration — the engine re-validates everything, so this modal never has to be right
/// about referential rules, only honest about what the user asked.
/// </summary>
public sealed class AgentEditorViewModel : ObservableObject
{
    private readonly IStudioStrings _strings;
    private string _blueprintJson = "";
    private string? _agentKey;
    private Action<string>? _apply;
    private string _name = "";
    private string _whatItDoes = "";
    private string _scopeInfo = "";
    private bool _isOpen;

    /// <summary>Builds the editor.</summary>
    public AgentEditorViewModel(IStudioStrings? strings = null)
    {
        _strings = strings ?? EnglishStudioStrings.Instance;
        Tools = new ToolPaletteViewModel(_strings);
        SaveCommand = new RelayCommand(Save, () => CanSave);
        RemoveCommand = new RelayCommand(Remove, () => !IsNew);
        CancelCommand = new RelayCommand(Close);
    }

    /// <summary>Whether the modal is showing.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    /// <summary>
    /// Shows the editor over <paramref name="blueprintJson"/> — for the agent named by
    /// <paramref name="agentKey"/>, or a new one when null. <paramref name="teamMounts"/>
    /// feeds the informative "On which folder" line (rights live per mount, not per
    /// agent); <paramref name="apply"/> receives the amended blueprint JSON.
    /// <para>
    /// The line names the mounts the way the agent addresses them — the virtual path with
    /// its rights in words — never the folder on this machine. An agent-facing screen showing
    /// <c>C:\Users\…</c> is the same category of leak as an agent prompt showing it.
    /// </para>
    /// </summary>
    public void Open(string blueprintJson, string? agentKey, IReadOnlyList<string> teamMounts, Action<string> apply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blueprintJson);
        ArgumentNullException.ThrowIfNull(teamMounts);
        ArgumentNullException.ThrowIfNull(apply);

        _blueprintJson = blueprintJson;
        _agentKey = agentKey;
        _apply = apply;
        _scopeInfo = DescribeScope(teamMounts, _strings);

        var agents = ReadAgents(blueprintJson);
        var current = agentKey is null
            ? default
            : agents.FirstOrDefault(a => string.Equals(a.Key, agentKey, StringComparison.Ordinal));
        _name = current.Role ?? "";
        _whatItDoes = current.Goal ?? "";

        // The chip set: the tools the team holds (its agents' and its tasks'), then the rest of
        // the catalogue a forged crew may draw from. The engine's catalogue re-checks names.
        Tools.Load(
            blueprintJson,
            current.Tools ?? [],
            FirstScope(teamMounts, readWrite: false),
            FirstScope(teamMounts, readWrite: true),
            OnChipsChanged);

        IsOpen = true;
        OnPropertiesChanged(nameof(Name), nameof(WhatItDoes), nameof(ScopeInfo), nameof(Title), nameof(IsNew), nameof(KeyLine), nameof(SaveLabel));
        SaveCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
    }

    /// <summary>True when the editor adds an agent rather than amending one.</summary>
    public bool IsNew => _agentKey is null;

    /// <summary>« Ajouter un agent » / « Modifier l'agent ».</summary>
    public string Title => _strings[IsNew ? StudioStringKeys.AgentEditorTitleAdd : StudioStringKeys.AgentEditorTitleEdit];

    /// <summary>The add-to-team / save button label.</summary>
    public string SaveLabel => _strings[IsNew ? StudioStringKeys.AgentEditorAdd : StudioStringKeys.ActSave];

    /// <summary>The display name — the blueprint's <c>role</c>.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertiesChanged(nameof(KeyLine));
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>One sentence of purpose — the blueprint's <c>goal</c>.</summary>
    public string WhatItDoes
    {
        get => _whatItDoes;
        set => SetProperty(ref _whatItDoes, value);
    }

    /// <summary>The capability chips: the team's, then the rest of the catalogue.</summary>
    public ToolPaletteViewModel Tools { get; }

    /// <summary>The team's capability chips (<see cref="ToolPaletteViewModel.Chips"/>).</summary>
    public ObservableCollection<AgentToolChipViewModel> Chips => Tools.Chips;

    /// <summary>The team's folders, informative — rights are per mount, never per agent.</summary>
    public string ScopeInfo => _scopeInfo;

    /// <summary>
    /// The team's mounts as the agents see them: virtual path plus rights, joined. An
    /// unparsable entry is named as such rather than dumped verbatim — the raw mount string
    /// carries the physical folder, which is exactly what must not appear here.
    /// </summary>
    internal static string DescribeScope(IReadOnlyList<string> mountStrings, IStudioStrings strings) =>
        MountLabels.DescribeAll(mountStrings, strings);

    /// <summary>
    /// The virtual path of the first team mount with the given rights; null when none, so the
    /// chip says what the tool does without inventing a folder.
    /// </summary>
    internal static string? FirstScope(IReadOnlyList<string> mountStrings, bool readWrite)
    {
        foreach (var mountString in mountStrings)
        {
            if (MountDefinition.TryParse(mountString, out var mount, out _) && mount is not null
                && (mount.Rights != MountRights.ReadOnly) == readWrite)
                return mount.VirtualPath;
        }

        return null;
    }

    /// <summary>The expert mono line: <c>id: key · tools: […]</c>.</summary>
    public string KeyLine =>
        $"id: {_agentKey ?? NewKey()} · tools: [{string.Join(", ", Tools.Selected)}]";

    /// <summary>A name is the one thing an agent cannot exist without.</summary>
    public bool CanSave => _name.Trim().Length > 0;

    /// <summary>Applies the edit and closes.</summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>The remove-from-team button — removes the agent (the engine refuses an orphaned task, loudly).</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>Closes without applying.</summary>
    public RelayCommand CancelCommand { get; }

    private void OnChipsChanged() => OnPropertiesChanged(nameof(KeyLine));

    private void Save()
    {
        var apply = _apply;
        var json = BuildAmendedJson(remove: false);
        Close();
        if (json is not null)
            apply?.Invoke(json);
    }

    private void Remove()
    {
        var apply = _apply;
        var json = BuildAmendedJson(remove: true);
        Close();
        if (json is not null)
            apply?.Invoke(json);
    }

    private void Close()
    {
        _apply = null;
        IsOpen = false;
    }

    /// <summary>
    /// The amended blueprint: role/goal/tools written on the agent, an appended entry for
    /// a new one, the entry dropped on a removal. Null when the stored JSON cannot be
    /// parsed at all — the caller then simply has nothing to send.
    /// </summary>
    private string? BuildAmendedJson(bool remove)
    {
        // Nothing opened yet: no blueprint, nothing to amend, and nothing for the parser.
        if (string.IsNullOrWhiteSpace(_blueprintJson))
            return null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(_blueprintJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (root is not JsonObject blueprint)
            return null;

        if (blueprint["agents"] is not JsonArray agents)
        {
            agents = [];
            blueprint["agents"] = agents;
        }

        if (remove)
        {
            for (var i = agents.Count - 1; i >= 0; i--)
            {
                if (agents[i] is JsonObject o && string.Equals((string?)o["key"], _agentKey, StringComparison.Ordinal))
                    agents.RemoveAt(i);
            }

            return blueprint.ToJsonString();
        }

        var tools = new JsonArray([.. Tools.Selected.Select(tool => (JsonNode)tool)]);
        var target = _agentKey is { } key
            ? agents.OfType<JsonObject>().FirstOrDefault(o => string.Equals((string?)o["key"], key, StringComparison.Ordinal))
            : null;

        if (target is null)
        {
            target = new JsonObject { ["key"] = _agentKey ?? NewKey() };
            agents.Add(target);
        }

        target["role"] = _name.Trim();
        if (_whatItDoes.Trim() is { Length: > 0 } goal)
            target["goal"] = goal;
        else
            target.Remove("goal");   // an emptied field is a removal, never a silent keep
        target["tools"] = tools;

        return blueprint.ToJsonString();
    }

    /// <summary>A slug key for a new agent, unique among the blueprint's keys.</summary>
    private string NewKey() =>
        BlueprintJson.UniqueKey(_name, "agent", ReadAgents(_blueprintJson).Select(a => a.Key));

    private static List<(string Key, string? Role, string? Goal, IReadOnlyList<string> Tools)> ReadAgents(string blueprintJson)
    {
        // The editor's summary line is bound, and read, before any blueprint is opened: an empty
        // one names no agent, and is not handed to the parser, which would refuse it with an
        // exception at every start.
        if (string.IsNullOrWhiteSpace(blueprintJson))
            return [];

        try
        {
            if (JsonNode.Parse(blueprintJson) is not JsonObject blueprint || blueprint["agents"] is not JsonArray agents)
                return [];

            var result = new List<(string, string?, string?, IReadOnlyList<string>)>();
            foreach (var node in agents.OfType<JsonObject>())
            {
                if ((string?)node["key"] is not { Length: > 0 } key)
                    continue;
                IReadOnlyList<string> tools = node["tools"] is JsonArray array
                    ? [.. array.OfType<JsonValue>().Select(v => (string?)v).Where(t => t is { Length: > 0 }).Cast<string>()]
                    : [];
                result.Add((key, (string?)node["role"], (string?)node["goal"], tools));
            }

            return result;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
