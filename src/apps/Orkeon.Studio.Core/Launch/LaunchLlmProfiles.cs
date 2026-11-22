using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Core.Launch;

/// <summary>
/// The LLM profiles a spawned <c>orkeon</c> run is offered besides the ones its settings file defines:
/// the host profiles a front-end lays in the run's environment, and the ones the environment the CLI
/// inherits lays itself. One rule for every launcher, so a rung of the decision ladder served by a
/// variable alone is served in the window as in the terminal, as the engine serves it.
/// </summary>
public static class LaunchLlmProfiles
{
    private const string Separator = "__";

    /// <summary>
    /// The LLM profiles the variables of <paramref name="names"/> lay, as the host of the spawned CLI,
    /// which inherits them, reads its environment: a variable is a setting with <c>__</c> for <c>:</c>,
    /// with or without the <c>ORKEON_</c> prefix, in any letter case, and any key under
    /// <c>Llm:Profiles:&lt;name&gt;</c> (<c>ORKEON_llm__profiles__Strong__BaseUrl</c>) makes the profile
    /// one the host offers, although the settings file does not define it. Compared in any letter case.
    /// </summary>
    public static HashSet<string> LaidIn(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var profiles = LlmProfilesSection.SectionPath.Replace(":", Separator, StringComparison.Ordinal) + Separator;
        var laid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            // The host reads every variable twice: as it is, and without the prefix when it has it.
            foreach (var setting in name.StartsWith(DecisionSection.EnvironmentPrefix, StringComparison.OrdinalIgnoreCase)
                         ? new[] { name, name[DecisionSection.EnvironmentPrefix.Length..] }
                         : [name])
            {
                if (!setting.StartsWith(profiles, StringComparison.OrdinalIgnoreCase))
                    continue;

                var end = setting.IndexOf(Separator, profiles.Length, StringComparison.Ordinal);
                var profile = end < 0 ? setting[profiles.Length..] : setting[profiles.Length..end];
                if (profile.Length > 0)
                    laid.Add(profile);
            }
        }

        return laid;
    }

    /// <summary>
    /// Every profile a run is offered beyond its settings file: <paramref name="hostProfiles"/>, the ids a
    /// front-end lays in the run's environment, and those <paramref name="environmentNames"/>, the names of
    /// the variables the CLI inherits, lay (<see cref="LaidIn"/>). Each id once, in any letter case.
    /// </summary>
    public static IReadOnlyCollection<string> Offered(IEnumerable<string> hostProfiles, IEnumerable<string> environmentNames)
    {
        ArgumentNullException.ThrowIfNull(hostProfiles);
        var offered = LaidIn(environmentNames);
        offered.UnionWith(hostProfiles);
        return offered;
    }

    /// <summary>The names of the variables of this process's environment, which a spawned CLI inherits.</summary>
    public static IEnumerable<string> ProcessEnvironmentNames() =>
        Environment.GetEnvironmentVariables().Keys.Cast<string>();
}
