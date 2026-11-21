using System.Globalization;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Wpf.ViewModels.Config;

/// <summary>
/// The <c>Orkeon:Skills</c> form: the virtual folder the skills are read from and how much of them
/// goes into a prompt. Each field carries the engine's default as a watermark; what the engine's start
/// refuses is said under the fields, in the engine's words.
/// </summary>
public sealed class SkillsSectionViewModel : DocumentSectionViewModel
{
    /// <summary>Binds the form to the <c>Orkeon:Skills</c> section of the document.</summary>
    public SkillsSectionViewModel(Func<AppSettingsDocument> document, Action onChanged)
        : base(document, onChanged)
    {
    }

    private SkillsSection Section => Document.Skills;

    /// <inheritdoc />
    public override bool Exists => Section.Exists;

    /// <summary>The virtual folder the skills are read from; empty, the engine's.</summary>
    public string Library
    {
        get => Section.Library ?? string.Empty;
        set => SetValue(Library, value?.Trim() ?? string.Empty, v => Section.Library = v);
    }

    /// <summary>The watermark of <see cref="Library"/>.</summary>
    public static string LibraryDefault => SkillsSection.DefaultLibrary;

    /// <summary>The characters kept of one skill.</summary>
    public int? MaxSkillChars
    {
        get => Section.MaxSkillChars;
        set => SetValue(Section.MaxSkillChars, value, v => Section.MaxSkillChars = v);
    }

    /// <summary>The watermark of <see cref="MaxSkillChars"/>.</summary>
    public static string MaxSkillCharsDefault => SkillsSection.DefaultMaxSkillChars.ToString(CultureInfo.InvariantCulture);

    /// <summary>The characters of the whole block of skills in a prompt.</summary>
    public int? MaxBlockChars
    {
        get => Section.MaxBlockChars;
        set => SetValue(Section.MaxBlockChars, value, v => Section.MaxBlockChars = v);
    }

    /// <summary>The watermark of <see cref="MaxBlockChars"/>.</summary>
    public static string MaxBlockCharsDefault => SkillsSection.DefaultMaxBlockChars.ToString(CultureInfo.InvariantCulture);

    /// <summary>What the engine's start refuses in the section, one line each; empty when it is accepted.</summary>
    public string ProblemsText => string.Join("\n", Section.Problems().Select(problem => problem.Problem));

    /// <inheritdoc />
    protected override void OnSectionChanged() => OnPropertiesChanged(
        nameof(Exists), nameof(Library), nameof(MaxSkillChars), nameof(MaxBlockChars), nameof(ProblemsText));
}
