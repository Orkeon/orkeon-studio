using Orkeon.Application.Interfaces.Ports;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Profiles;

namespace Orkeon.Studio.Wpf.ViewModels.Config;

/// <summary>One profile a ladder rung or the compaction may name.</summary>
/// <param name="Id">The name written to the file: the host profile's id, <c>default</c>, or a decision profile.</param>
/// <param name="Label">What the list shows: the setting's name (« mydeepseek.flash ») when a setting offers the id, else the id.</param>
public sealed record ProfileChoiceItem(string Id, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// The profiles a run of the settings knows, as lists offer them: the default, then the model settings
/// in their order — the models of a connection side by side —, then the names the file defines that no
/// setting owns (an entry written by hand, a decision profile).
/// </summary>
internal static class ProfileChoices
{
    public static IReadOnlyList<ProfileChoiceItem> Of(AppSettingsDocument document, ModelProfileSet? set, bool withDefault)
    {
        ArgumentNullException.ThrowIfNull(document);
        var choices = new List<ProfileChoiceItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string id, string label)
        {
            if (seen.Add(id))
                choices.Add(new ProfileChoiceItem(id, label));
        }

        if (withDefault)
            Add(LlmProfiles.Default, LlmProfiles.Default);

        if (set is not null)
        {
            var offered = HostLlmProfiles.Offered(set);
            foreach (var profile in set.Profiles)
            {
                if (offered.TryGetValue(profile.Name, out var id))
                    Add(id, string.Equals(id, profile.Name, StringComparison.Ordinal) ? id : profile.Name);
            }
        }

        foreach (var name in document.Decision.ProfileNames())
        {
            if (!LlmProfiles.IsDefault(name) && !LlmProfiles.IsAuto(name))
                Add(name, name);
        }

        return choices;
    }

    /// <summary>The label of <paramref name="id"/> among <paramref name="choices"/>, else the id itself.</summary>
    public static string LabelOf(IReadOnlyList<ProfileChoiceItem> choices, string id) =>
        choices.FirstOrDefault(choice => string.Equals(choice.Id, id, StringComparison.OrdinalIgnoreCase))?.Label ?? id;
}
