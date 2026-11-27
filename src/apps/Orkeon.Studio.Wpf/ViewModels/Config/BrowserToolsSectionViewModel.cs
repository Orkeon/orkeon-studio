using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;

namespace Orkeon.Studio.Wpf.ViewModels.Config;

/// <summary>
/// The <c>Orkeon:Tools:Browser</c> form of the Browser tab (expert, Orkeon #440): the browser the
/// <c>browser</c> tool drives, whether it shows a window, the profile it starts on, whether it stays
/// open from one task to the next (<c>Lifetime</c>) and whether agents working at once share it
/// (<c>SharedInstance</c>). The runtime's defaults are written as no key at all; a change moves a lone former <c>Orkeon:Tools:Firefox</c> section under
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
        RemoveLifetimeCommand = new RelayCommand(RemoveLifetime);
    }

    /// <summary>Removes a lifetime the runtime does not know (<see cref="RemoveLifetime"/>).</summary>
    public RelayCommand RemoveLifetimeCommand { get; }

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

    // The profile as typed: the section keeps it trimmed, and a box that showed the trimmed text back
    // would eat a blank typed inside a folder path.
    private string? _profileText;

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

    /// <summary>
    /// The profile a browser starts on when the agent names none: blank (or <c>disposable</c>) for a
    /// fresh folder removed when the browser closes, which writes no key; else a Firefox profile name
    /// or the path of a folder, written trimmed.
    /// </summary>
    public string Profile
    {
        get => _profileText ?? Section.Profile ?? "";
        set
        {
            if (string.Equals(value, Profile, StringComparison.Ordinal))
                return;

            var before = Section.Profile;
            _profileText = value;
            Section.Profile = value;
            OnPropertiesChanged(nameof(Profile), nameof(CanEditProfile));
            if (!string.Equals(before, Section.Profile, StringComparison.Ordinal))
                NotifyDocumentChanged();
        }
    }

    /// <summary>
    /// Whether the browser stays open from one task to the next (<c>Lifetime: Host</c>) rather than
    /// closing at the end of each task (<c>Run</c>, the default, written as no key). A lifetime the
    /// runtime does not know reads as off and stays as written, for the validator to refuse, until
    /// the switch is set.
    /// </summary>
    public bool KeepsBrowserOpen
    {
        get => string.Equals(Section.EffectiveLifetime, BrowserToolsSection.HostLifetime, StringComparison.OrdinalIgnoreCase);
        set => SetValue(KeepsBrowserOpen, value, v => Section.Lifetime = v ? BrowserToolsSection.HostLifetime : null);
    }

    /// <summary>Whether agents working at the same time share one browser per profile; off (the default) removes the key.</summary>
    public bool SharedInstance
    {
        get => Section.SharedInstance ?? false;
        set => SetValue(SharedInstance, value, v => Section.SharedInstance = v);
    }

    /// <summary>
    /// Whether the file names a lifetime the runtime refuses (neither <c>Run</c> nor <c>Host</c>):
    /// the switch then reads as off, and the tab offers to remove the value.
    /// </summary>
    public bool HasUnknownLifetime =>
        Section.Lifetime is { } lifetime && !BrowserToolsSection.Lifetimes.Contains(lifetime, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the profile can be edited: always, but under Safari, which runs on the disposable
    /// profile alone; a profile the file still names stays editable there, to clear it.
    /// </summary>
    public bool CanEditProfile => !IsSafari || Profile.Length > 0;

    /// <summary>Removes a lifetime the runtime does not know: the browser then closes with each task, the default.</summary>
    public void RemoveLifetime()
    {
        if (!HasUnknownLifetime)
            return;

        Section.Lifetime = null;
        NotifyDocumentChanged();
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
    public override void Refresh()
    {
        _profileText = null;
        base.Refresh();
    }

    /// <inheritdoc />
    protected override void OnSectionChanged()
    {
        // The text typed stays while it is what the section holds; a profile changed elsewhere replaces it.
        if (_profileText is not null && !string.Equals(BrowserToolsSection.NormalizeProfile(_profileText), Section.Profile, StringComparison.Ordinal))
            _profileText = null;

        OnPropertiesChanged(nameof(Exists), nameof(Engine), nameof(EngineChoices), nameof(Headless), nameof(IsSafari),
            nameof(CanRunHeadless), nameof(CanToggleHeadless), nameof(UsesLegacySection), nameof(HasBothSections),
            nameof(Profile), nameof(KeepsBrowserOpen), nameof(SharedInstance), nameof(HasUnknownLifetime), nameof(CanEditProfile));
    }
}

/// <summary>A choice of the browser engine picker: the runtime's id and the name its browser goes by.</summary>
public sealed record BrowserEngineChoice(string Id, string Label);
