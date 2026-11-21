using Orkeon.Application.Crew.Skills;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// Typed view over <c>Orkeon:Skills</c>: where the skills an agent may carry are read from, and how
/// much of them goes into its prompt. A skill is a Markdown file of the library, a virtual folder of
/// the run; an agent lists its skills, or lets System One pick them (<c>skills: { auto: true }</c>).
/// Every key is optional: absent, the engine's default stands, and the defaults here are the engine's
/// own constants.
/// </summary>
public sealed class SkillsSection
{
    /// <summary>Configuration path of the section.</summary>
    public const string SectionPath = AgentSkillsOptions.SectionName;

    /// <summary>Default of <c>Library</c>: the virtual folder the skills are read from.</summary>
    public const string DefaultLibrary = AgentSkillsOptions.DefaultLibrary;

    /// <summary>Default of <c>MaxSkillChars</c>: the characters kept of one skill.</summary>
    public const int DefaultMaxSkillChars = AgentSkillsOptions.DefaultMaxSkillChars;

    /// <summary>Default of <c>MaxBlockChars</c>: the characters of the whole block of skills in a prompt.</summary>
    public const int DefaultMaxBlockChars = AgentSkillsOptions.DefaultMaxBlockChars;

    /// <summary>The least <c>MaxBlockChars</c> the engine accepts.</summary>
    public const int MinBlockChars = AgentSkillsOptions.MinBlockChars;

    private readonly AppSettingsDocument _document;

    internal SkillsSection(AppSettingsDocument document) => _document = document;

    /// <summary>True when the section carries at least one key.</summary>
    public bool Exists => _document.SectionExists(_document.PathAsWritten(SectionPath));

    /// <summary>The virtual folder the skills are read from (an absolute virtual path).</summary>
    public string? Library
    {
        get => _document.GetString(Key("Library"));
        set => _document.SetString(Key("Library"), string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    /// <summary>The characters kept of one skill, 1 or more.</summary>
    public int? MaxSkillChars
    {
        get => _document.GetInt32(Key("MaxSkillChars"));
        set => _document.SetInt32(Key("MaxSkillChars"), value);
    }

    /// <summary>The characters of the whole block of skills in a prompt, <see cref="MinBlockChars"/> or more.</summary>
    public int? MaxBlockChars
    {
        get => _document.GetInt32(Key("MaxBlockChars"));
        set => _document.SetInt32(Key("MaxBlockChars"), value);
    }

    /// <summary>
    /// What the host's start refuses in the section, as the engine binds and validates it
    /// (<c>AgentSkillsOptions.Problems</c>): a value the binder cannot convert, a library that is no
    /// absolute virtual path, a size too small, a key that is no setting of the section. Each with the
    /// path of its key; empty when accepted.
    /// </summary>
    public IReadOnlyList<(string Path, string Problem)> Problems()
    {
        var configuration = DecisionSection.ToConfiguration(_document.Root);
        var problems = new List<(string, string)>();
        var skills = new AgentSkillsOptions();
        if (SettingsBinding.TryBind(configuration, SectionPath, skills, problems))
            problems.AddRange(AgentSkillsOptions.Problems(skills).Select(problem => (SettingsBinding.PathOf(problem, SectionPath), problem)));

        problems.AddRange(SettingsBinding.UnknownKeys(configuration, SectionPath, typeof(AgentSkillsOptions)));
        return problems;
    }

    /// <summary>Removes the whole section.</summary>
    public void Remove() => _document.Remove(_document.PathAsWritten(SectionPath));

    // Each key where the file spells it, in whatever letter case, as the configuration reads it.
    private string Key(string key) => _document.PathAsWritten($"{SectionPath}:{key}");
}
