using Microsoft.Extensions.Configuration;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// Binds a section of the settings to the engine's own options class, as the host does at start, so a
/// section is judged by the engine's rules and never by a copy of them.
/// </summary>
internal static class SettingsBinding
{
    /// <summary>
    /// Binds <paramref name="sectionPath"/> onto <paramref name="options"/>. False, with the binder's
    /// message added to <paramref name="problems"/> at the section's path, when a value does not convert.
    /// </summary>
    public static bool TryBind(
        IConfiguration configuration, string sectionPath, object options, List<(string Path, string Problem)> problems)
    {
        try
        {
            configuration.GetSection(sectionPath).Bind(options);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            problems.Add((sectionPath, ex.Message));
            return false;
        }
    }

    /// <summary>
    /// The path of the key a problem of the engine names: its messages start with the key's full path
    /// (<c>Orkeon:Skills:Library is …</c>). The section's own path when the message names no key of it.
    /// </summary>
    public static string PathOf(string problem, string sectionPath)
    {
        if (!problem.StartsWith(sectionPath + ":", StringComparison.Ordinal))
            return sectionPath;

        var end = problem.IndexOf(' ', StringComparison.Ordinal);
        return end < 0 ? sectionPath : problem[..end].TrimEnd(':');
    }
}
