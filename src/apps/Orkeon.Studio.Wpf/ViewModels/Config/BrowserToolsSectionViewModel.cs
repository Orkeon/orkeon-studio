using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;

namespace Orkeon.Studio.Wpf.ViewModels.Config;

/// <summary>
/// The <c>Orkeon:Tools:Browser</c> form of the Browser tab (expert, Orkeon #440): the browser the
/// <c>browser</c> tool drives and whether it shows a window. The default engine and a visible window
/// are written as no key at all; a change moves a lone former <c>Orkeon:Tools:Firefox</c> section under
/// the new name first, so the file never holds the two the run refuses together.
/// </summary>
public sealed class BrowserToolsSectionViewModel : DocumentSectionViewModel
{
    /// <summary>Binds the form to the <c>Orkeon:Tools:Browser</c> section of the document.</summary>
    public BrowserToolsSectionViewModel(Func<AppSettingsDocument> document, Action onChanged)
        : base(document, onChanged)
    {
        MoveLegacySectionCommand = new RelayCommand(MoveLegacySection);
        RemoveLegacySectionCommand = new RelayCommand(RemoveLegacySection);
    }

    /// <summary>Renames a lone former <c>Orkeon:Tools:Firefox</c> section (<see cref="MoveLegacySection"/>).</summary>
    public RelayCommand MoveLegacySectionCommand { get; }

    /// <summary>Removes a former section set beside the new one (<see cref="RemoveLegacySection"/>).</summary>
    public RelayCommand RemoveLegacySectionCommand { get; }

    /// <summary>The engines the picker offers, in the runtime's spelling.</summary>
    public static IReadOnlyList<string> Engines => BrowserToolsSection.Engines;

    /// <summary>
    /// The name of an engine as its browser is called, the same in every language (a product name);
    /// a name the runtime does not know is shown as written.
    /// </summary>
    public static string EngineLabel(string engine) => engine switch
    {
        "firefox" => "Firefox",
        "chrome" => "Chrome",
        "edge" => "Edge",
        "brave" => "Brave",
        "safari" => "Safari",
        _ => engine,
    };

    /// <summary>
    /// The choices of the picker: <see cref="Engines"/>, and the engine the file writes when the
    /// runtime does not know it, so the picker shows what the validator refuses instead of a blank.
    /// </summary>
    public IReadOnlyList<BrowserEngineChoice> EngineChoices
    {
        get
        {
            var choices = Engines.Select(e => new BrowserEngineChoice(e, EngineLabel(e))).ToList();
            if (!Engines.Contains(Engine, StringComparer.Ordinal))
                choices.Add(new BrowserEngineChoice(Engine, Engine));
            return choices;
        }
    }

    private BrowserToolsSection Section => Document.BrowserTools;

    /// <inheritdoc />
    public override bool Exists => Section.Exists;

    /// <summary>
    /// The engine, one of <see cref="Engines"/> in their spelling; a value the file writes otherwise
    /// (another case, an unknown name) shows as written, for the validator to judge.
    /// </summary>
    public string Engine
    {
        get
        {
            var written = Section.EffectiveEngine;
            return Engines.FirstOrDefault(e => string.Equals(e, written, StringComparison.OrdinalIgnoreCase)) ?? written;
        }
        set => SetValue(Engine, string.IsNullOrWhiteSpace(value) ? BrowserToolsSection.DefaultEngine : value, v =>
        {
            Section.Engine = v;
            // Safari has no headless mode: the run refuses the pair, so picking Safari turns it off.
            if (IsSafari && Section.Headless is true)
                Section.Headless = false;
        });
    }

    /// <summary>Whether the browser starts without a window; off (the default) removes the key.</summary>
    public bool Headless
    {
        get => Section.Headless ?? false;
        set => SetValue(Headless, value, v => Section.Headless = v);
    }

    /// <summary>Whether the engine can run headless: Safari cannot.</summary>
    public bool CanRunHeadless => !IsSafari;

    /// <summary>
    /// Whether the switch can be used: always under an engine that runs headless, and under Safari
    /// while the file still turns it on, so the refused pair can be undone from here.
    /// </summary>
    public bool CanToggleHeadless => CanRunHeadless || Headless;

    /// <summary>Whether the engine is Safari, which runs on macOS alone and always with a window.</summary>
    public bool IsSafari => string.Equals(Section.EffectiveEngine, BrowserToolsSection.SafariEngine, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the file still names the section by its former name, which the next write renames.</summary>
    public bool UsesLegacySection => Section.LegacyExists && !Section.Exists;

    /// <summary>Whether the file sets the section under both names, which the run refuses.</summary>
    public bool HasBothSections => Section.BothSet;

    /// <summary>Renames a lone former section, keys and all.</summary>
    public void MoveLegacySection()
    {
        if (!UsesLegacySection)
            return;

        Section.MoveLegacySection();
        NotifyDocumentChanged();
    }

    /// <summary>Removes the former section set beside the new one: the run reads the new one.</summary>
    public void RemoveLegacySection()
    {
        if (!HasBothSections)
            return;

        Section.RemoveLegacySection();
        NotifyDocumentChanged();
    }

    /// <inheritdoc />
    protected override void OnSectionChanged() =>
        OnPropertiesChanged(nameof(Exists), nameof(Engine), nameof(EngineChoices), nameof(Headless), nameof(IsSafari),
            nameof(CanRunHeadless), nameof(CanToggleHeadless), nameof(UsesLegacySection), nameof(HasBothSections));
}

/// <summary>A choice of the browser engine picker: the runtime's id and the name its browser goes by.</summary>
public sealed record BrowserEngineChoice(string Id, string Label);
