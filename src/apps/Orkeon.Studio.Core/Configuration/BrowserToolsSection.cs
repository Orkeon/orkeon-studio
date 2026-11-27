using System.Text.Json.Nodes;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// Typed view over <c>Orkeon:Tools:Browser</c>, the section of the <c>browser</c> tool (Orkeon #440):
/// which browser the run drives (<see cref="Engine"/>) and whether it shows a window
/// (<see cref="Headless"/>). Read by <c>AddOrkeonWebTools</c> in <c>Orkeon.Tools.Web</c>
/// (<c>FirefoxToolOptions.BrowserSectionName</c>), which Studio does not reference, so the path is
/// spelt here and pinned by a drift test.
/// <para>
/// The section was <c>Orkeon:Tools:Firefox</c> before (<see cref="LegacySectionPath"/>): the engine
/// still reads it when <c>Orkeon:Tools:Browser</c> is absent, without its <c>Engine</c>, and refuses
/// a file that holds both. So a write here first moves a lone former section under the new name,
/// keys and all, and the file never holds the two.
/// </para>
/// <para>
/// The keys this view does not model (profiles, timeouts, lifetime...) are kept as written.
/// </para>
/// </summary>
public sealed class BrowserToolsSection
{
    /// <summary>Configuration path of the section.</summary>
    public const string SectionPath = "Orkeon:Tools:Browser";

    /// <summary>The former path of the section, deprecated by Orkeon and read only when <see cref="SectionPath"/> is absent.</summary>
    public const string LegacySectionPath = "Orkeon:Tools:Firefox";

    /// <summary>The engine the runtime drives when none is set.</summary>
    public const string DefaultEngine = "firefox";

    /// <summary>The engine that runs on macOS alone and has no headless mode.</summary>
    public const string SafariEngine = "safari";

    /// <summary>The engines the runtime knows, in the spelling it documents (it ignores the case).</summary>
    public static IReadOnlyList<string> Engines { get; } = [DefaultEngine, "chrome", "edge", "brave", SafariEngine];

    private readonly AppSettingsDocument _document;

    internal BrowserToolsSection(AppSettingsDocument document) => _document = document;

    /// <summary>True when the section is present with at least one key.</summary>
    public bool Exists => _document.SectionExists(_document.PathAsWritten(SectionPath));

    /// <summary>True when the former section is present with at least one key.</summary>
    public bool LegacyExists => _document.SectionExists(_document.PathAsWritten(LegacySectionPath));

    /// <summary>
    /// Whether the run sees both sections, which it refuses: a section exists for the configuration
    /// as soon as it holds a value or a child, so a scalar or a list, even an empty one (read as an
    /// empty value), counts as well as an object with a key; an empty object does not.
    /// </summary>
    public bool BothSet => IsSet(SectionPath) && IsSet(LegacySectionPath);

    /// <summary>
    /// The engine as written; null when the key is absent, which the runtime reads as
    /// <see cref="DefaultEngine"/>. Writing blank or the default removes the key.
    /// </summary>
    public string? Engine
    {
        get => _document.GetString(_document.PathAsWritten($"{SectionPath}:Engine"));
        set
        {
            var engine = string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), DefaultEngine, StringComparison.OrdinalIgnoreCase)
                ? null
                : value.Trim();
            Write("Engine", engine is null ? null : JsonValue.Create(engine));
        }
    }

    /// <summary>The engine the run drives: <see cref="Engine"/>, else <see cref="DefaultEngine"/>.</summary>
    public string EffectiveEngine => string.IsNullOrWhiteSpace(Engine) ? DefaultEngine : Engine.Trim();

    /// <summary>
    /// The path of <c>Headless</c> the run reads: under <see cref="SectionPath"/>, or under a lone
    /// <see cref="LegacySectionPath"/>, whose keys but <c>Engine</c> the runtime still reads.
    /// </summary>
    public string HeadlessPath => _document.PathAsWritten($"{(!Exists && LegacyExists ? LegacySectionPath : SectionPath)}:Headless");

    /// <summary>
    /// Whether the browser starts without a window, as the run reads it (<see cref="HeadlessPath"/>);
    /// null when the key is absent (a window). Writing false removes the key, the runtime's default.
    /// </summary>
    public bool? Headless
    {
        get => _document.GetBoolean(HeadlessPath);
        set => Write("Headless", value is true ? JsonValue.Create(true) : null);
    }

    /// <summary>
    /// Moves a lone <see cref="LegacySectionPath"/> under <see cref="SectionPath"/>, its keys as written
    /// but <c>Engine</c>: the run never reads that key under the former name (the former section knows
    /// one engine, Firefox), so carrying it across would switch on an engine nobody picked. Nothing
    /// happens when the new section is already there or the former one is absent.
    /// </summary>
    public void MoveLegacySection()
    {
        var legacyPath = _document.PathAsWritten(LegacySectionPath);
        if (Exists || _document.GetNode(legacyPath) is not JsonObject legacy)
            return;

        var moved = (JsonObject)legacy.DeepClone();
        SectionNodes.Remove(moved, "Engine");
        _document.Remove(legacyPath);
        if (moved.Count > 0)
            _document.SetNode(_document.PathAsWritten(SectionPath), moved);
    }

    /// <summary>
    /// Removes a former section the run refuses beside <see cref="SectionPath"/>: the run reads the new
    /// one, so the former goes. Nothing happens unless both are set.
    /// </summary>
    public void RemoveLegacySection()
    {
        if (BothSet)
            _document.Remove(_document.PathAsWritten(LegacySectionPath));
    }

    private bool IsSet(string sectionPath) =>
        _document.GetNode(_document.PathAsWritten(sectionPath)) switch
        {
            null => false,
            JsonObject node => node.Count > 0,
            _ => true,
        };

    private void Write(string key, JsonNode? value)
    {
        // A value written, or a key removed that a lone former section holds, renames that section first.
        if (value is not null || _document.ContainsPath(_document.PathAsWritten($"{LegacySectionPath}:{key}")))
            MoveLegacySection();

        var path = _document.PathAsWritten($"{SectionPath}:{key}");
        if (value is null && !_document.ContainsPath(path))
            return;

        _document.SetNode(path, value);
        // A section emptied by its last key leaves no empty object behind.
        var section = _document.PathAsWritten(SectionPath);
        if (_document.GetNode(section) is JsonObject { Count: 0 })
            _document.Remove(section);
    }
}
