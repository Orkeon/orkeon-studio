using System.Globalization;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Config.Presentation;

/// <summary>
/// The <c>Orkeon:Tools:Browser</c> section (Orkeon #440): the engine the <c>browser</c> tool drives,
/// whether it shows a window, the profile it starts on, whether it stays open from one task to the next
/// (<c>Lifetime</c>) and whether agents working at once share it (<c>SharedInstance</c>). The runtime's
/// defaults are written as no key; a value
/// written moves a lone former <c>Orkeon:Tools:Firefox</c> section under the new name first
/// (<see cref="BrowserToolsSection"/>), so the file never holds the two the run refuses together.
/// </summary>
internal sealed class BrowserForm : ISettingsForm
{
    /// <inheritdoc />
    public string Title => "Browser";

    /// <summary>Shown above the fields: what the section is for.</summary>
    public const string Notice = "The browser the browser tool drives.";

    /// <summary>Shown when the file still names the section by its former name.</summary>
    public const string LegacyNotice = "In Orkeon:Tools:Firefox: a change renames it.";

    /// <summary>Shown when the file names a lifetime the run refuses (the value itself is not shown).</summary>
    public const string UnknownLifetimeNotice = "The file names a lifetime the run refuses: set the switch.";

    // The paths of the keys as the file holds them: under the former section while it is the one read.
    private string _headlessPath = BrowserToolsSection.SectionPath + ":Headless";
    private string _sharedPath = BrowserToolsSection.SectionPath + ":SharedInstance";
    private string _lifetimePath = BrowserToolsSection.SectionPath + ":Lifetime";

    /// <summary>
    /// The engines the chooser shows: the five the runtime knows, then the one the file writes when it
    /// is none of them, kept as written for the validator to judge.
    /// </summary>
    public IReadOnlyList<string> EngineChoices { get; private set; } = BrowserToolsSection.Engines;

    /// <summary>The engine chosen, in the runtime's spelling.</summary>
    public string Engine { get; set; } = BrowserToolsSection.DefaultEngine;

    /// <summary>Whether the browser starts without a window; null when the key is absent.</summary>
    public bool? Headless { get; set; }

    /// <summary>The text <c>Headless</c> holds when it reads as no boolean, else null (see <see cref="FieldText.UnreadableSwitch"/>).</summary>
    public string? HeadlessAsWritten { get; set; }

    /// <summary>The profile as typed: blank for the disposable one.</summary>
    public string Profile { get; set; } = "";

    /// <summary>
    /// Whether the browser stays open from one task to the next: true for <c>Host</c>, false for
    /// <c>Run</c>, null when the key is absent, or names a lifetime the runtime does not know
    /// (<see cref="LifetimeAsWritten"/>). Null removes the key.
    /// </summary>
    public bool? KeepsBrowserOpen { get; set; }

    /// <summary>
    /// The lifetime the file names when it is neither <c>Run</c> nor <c>Host</c>, else null: applying
    /// refuses it, as the run does, until the switch changes state and clears it.
    /// </summary>
    public string? LifetimeAsWritten { get; set; }

    /// <summary>Whether agents working at once share one browser per profile; null when the key is absent.</summary>
    public bool? SharedInstance { get; set; }

    /// <summary>The text <c>SharedInstance</c> holds when it reads as no boolean, else null.</summary>
    public string? SharedInstanceAsWritten { get; set; }

    /// <summary>Whether the file names the section by its former name alone.</summary>
    public bool UsesLegacySection { get; private set; }

    /// <summary>Index of <see cref="Engine"/> in <see cref="EngineChoices"/>.</summary>
    public int EngineChoiceIndex =>
        Math.Max(0, EngineChoices.ToList().FindIndex(e => string.Equals(e, Engine, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Selects an engine by its index in <see cref="EngineChoices"/>.</summary>
    public void SelectEngine(int index) =>
        Engine = index >= 0 && index < EngineChoices.Count ? EngineChoices[index] : BrowserToolsSection.DefaultEngine;

    /// <inheritdoc />
    public void LoadFrom(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var section = document.BrowserTools;
        var written = section.EffectiveEngine;
        var known = BrowserToolsSection.Engines.FirstOrDefault(e => string.Equals(e, written, StringComparison.OrdinalIgnoreCase));
        EngineChoices = known is null ? [.. BrowserToolsSection.Engines, written] : BrowserToolsSection.Engines;
        Engine = known ?? written;
        Headless = section.Headless;
        _headlessPath = section.HeadlessPath;
        HeadlessAsWritten = FieldText.UnreadableSwitch(document, _headlessPath);
        SharedInstance = section.SharedInstance;
        Profile = section.Profile ?? "";
        KeepsBrowserOpen = section.Lifetime switch
        {
            null => null,
            var lifetime when string.Equals(lifetime, BrowserToolsSection.HostLifetime, StringComparison.OrdinalIgnoreCase) => true,
            var lifetime when string.Equals(lifetime, BrowserToolsSection.DefaultLifetime, StringComparison.OrdinalIgnoreCase) => false,
            _ => null,
        };
        LifetimeAsWritten = KeepsBrowserOpen is null ? section.Lifetime : null;
        ReadPaths(document);
        SharedInstanceAsWritten = FieldText.UnreadableSwitch(document, _sharedPath);
        UsesLegacySection = section.LegacyExists && !section.Exists;
    }

    private void ReadPaths(AppSettingsDocument document)
    {
        var section = document.BrowserTools;
        _headlessPath = section.HeadlessPath;
        _sharedPath = section.KeyPath("SharedInstance");
        _lifetimePath = section.KeyPath("Lifetime");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ApplyTo(AppSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var errors = new List<string>();
        FieldText.RefuseUnreadableSwitch(HeadlessAsWritten, _headlessPath, errors);
        FieldText.RefuseUnreadableSwitch(SharedInstanceAsWritten, _sharedPath, errors);
        if (LifetimeAsWritten is not null)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture,
                $"{_lifetimePath}: the run knows the lifetimes {string.Join(" and ", BrowserToolsSection.Lifetimes)} only, and refuses this one. Set the switch to replace it."));
        }
        if (string.Equals(Engine, BrowserToolsSection.SafariEngine, StringComparison.OrdinalIgnoreCase) && Headless is true)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture,
                $"{_headlessPath}: Safari has no headless mode, and the run refuses it. Turn it off or pick another engine."));
        }

        if (errors.Count > 0)
            return errors;

        var section = document.BrowserTools;
        // The runtime reads the engine without regard to case: "Chrome" as written stays as written.
        if (!string.Equals(section.EffectiveEngine, Engine, StringComparison.OrdinalIgnoreCase))
            section.Engine = Engine;
        // A switch is written when it says otherwise than the file, or when the file held a value that
        // reads as no boolean and the switch was changed since: its new state, unset included, replaces it.
        if (section.Headless != Headless || FieldText.UnreadableSwitch(document, _headlessPath) is not null)
            section.Headless = Headless;
        if (section.SharedInstance != SharedInstance || FieldText.UnreadableSwitch(document, _sharedPath) is not null)
            section.SharedInstance = SharedInstance;
        // "disposable" and blank are the same profile: neither rewrites the other.
        if (!string.Equals(BrowserToolsSection.NormalizeProfile(Profile), BrowserToolsSection.NormalizeProfile(section.Profile), StringComparison.Ordinal))
            section.Profile = Profile;

        // The lifetime as the switch says it: Host when on, Run (as written, or no key) when off, no key when unset.
        var host = string.Equals(section.Lifetime, BrowserToolsSection.HostLifetime, StringComparison.OrdinalIgnoreCase);
        var run = string.Equals(section.Lifetime, BrowserToolsSection.DefaultLifetime, StringComparison.OrdinalIgnoreCase);
        if (KeepsBrowserOpen is true && !host)
            section.Lifetime = BrowserToolsSection.HostLifetime;
        else if (KeepsBrowserOpen is false && section.Lifetime is not null && !run)
            section.Lifetime = null;
        else if (KeepsBrowserOpen is null && section.Lifetime is not null)
            section.Lifetime = null;

        // After a rename the keys live under the new section, and the field shows the profile as kept.
        ReadPaths(document);
        Profile = section.Profile ?? "";
        UsesLegacySection = section.LegacyExists && !section.Exists;

        return [];
    }
}
