using System.Collections.ObjectModel;
using Orkeon.Studio.Core.Forge;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;

namespace Orkeon.Studio.Wpf.ViewModels.Teams;

/// <summary>One family of the catalogue under « Plus d'outils… »: its label and its chips.</summary>
public sealed class ToolFamilyChipsViewModel
{
    internal ToolFamilyChipsViewModel(string label, IReadOnlyList<AgentToolChipViewModel> chips)
    {
        Label = label;
        Chips = chips;
    }

    /// <summary>The family's label, in the user's language.</summary>
    public string Label { get; }

    /// <summary>Its tools, as togglable chips.</summary>
    public IReadOnlyList<AgentToolChipViewModel> Chips { get; }
}

/// <summary>
/// The tool chips of an editor of a forged blueprint (the agent editor, the task editor): the
/// tools the team already holds, shown, then the rest of the catalogue a forged crew may draw
/// from, by family, behind « Plus d'outils… » (<see cref="ForgeToolPalette"/>). The engine
/// re-checks every name against its own catalogue when the edit reaches it.
/// </summary>
public sealed class ToolPaletteViewModel : ObservableObject
{
    private readonly IStudioStrings _strings;
    private bool _showMore;

    /// <summary>Builds an empty palette.</summary>
    public ToolPaletteViewModel(IStudioStrings? strings = null)
    {
        _strings = strings ?? EnglishStudioStrings.Instance;
        ToggleMoreCommand = new RelayCommand(() => ShowMore = !ShowMore);
    }

    /// <summary>The tools an agent or a task of the team already names.</summary>
    public ObservableCollection<AgentToolChipViewModel> Chips { get; } = [];

    /// <summary>The rest of the catalogue, by family.</summary>
    public ObservableCollection<ToolFamilyChipsViewModel> MoreFamilies { get; } = [];

    /// <summary>Whether the rest of the catalogue is unfolded.</summary>
    public bool ShowMore
    {
        get => _showMore;
        set
        {
            if (SetProperty(ref _showMore, value))
                OnPropertiesChanged(nameof(ToggleMoreLabel));
        }
    }

    /// <summary>Whether the catalogue holds tools the team does not.</summary>
    public bool HasMore => MoreFamilies.Count > 0;

    /// <summary>« Plus d'outils… » folded, « Moins d'outils » unfolded.</summary>
    public string ToggleMoreLabel => _strings[ShowMore ? StudioStringKeys.ToolPaletteLess : StudioStringKeys.ToolPaletteMore];

    /// <summary>Folds or unfolds the rest of the catalogue.</summary>
    public RelayCommand ToggleMoreCommand { get; }

    /// <summary>Every chip, the team's first.</summary>
    public IEnumerable<AgentToolChipViewModel> AllChips => Chips.Concat(MoreFamilies.SelectMany(family => family.Chips));

    /// <summary>The selected tools: the team's in their order, then the catalogue's.</summary>
    public IReadOnlyList<string> Selected => [.. AllChips.Where(chip => chip.IsSelected).Select(chip => chip.Tool)];

    /// <summary>
    /// Fills the palette from <paramref name="blueprintJson"/>, <paramref name="selected"/> on. The
    /// chips say what a tool does on the team's folders, as on the creation screen: the first
    /// read-only mount for a reading tool, the first writable one for a writing tool. A selected
    /// tool the palette does not hold is listed among the team's, so a save never drops it.
    /// </summary>
    internal void Load(string blueprintJson, IReadOnlyList<string> selected, string? readScope, string? writeScope, Action changed)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(changed);

        var palette = ForgeToolPalette.For(blueprintJson);
        var team = palette.TeamTools.Concat(selected.Where(tool => !palette.TeamTools.Contains(tool, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal);

        AgentToolChipViewModel ChipOf(string tool) => new(
            tool,
            WizardToolChips.For(tool, _strings, readScope, writeScope),
            selected.Contains(tool, StringComparer.Ordinal),
            changed);

        Chips.Clear();
        foreach (var tool in team)
            Chips.Add(ChipOf(tool));

        MoreFamilies.Clear();
        foreach (var family in palette.Families)
        {
            var chips = family.Tools.Where(tool => !selected.Contains(tool, StringComparer.Ordinal)).Select(ChipOf).ToList();
            if (chips.Count > 0)
                MoreFamilies.Add(new ToolFamilyChipsViewModel(_strings[ToolFamilyLabels.KeyFor(family.FamilyKey)], chips));
        }

        ShowMore = false;
        OnPropertiesChanged(nameof(HasMore), nameof(ToggleMoreLabel));
    }
}
