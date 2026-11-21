using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// Binds a section of the settings to an options class as the engine's host does at start, so a section
/// is read with the binder's own conversions, its indifference to letter case and its reading of lists,
/// and never by a second reading of the JSON.
/// </summary>
internal static partial class SettingsBinding
{
    /// <summary>
    /// Binds <paramref name="sectionPath"/> onto <paramref name="options"/>. False, with the binder's
    /// sentence added to <paramref name="problems"/> at the key it names, when a value does not convert:
    /// the host's start stops there, on the first such value.
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
            problems.Add((ConvertedPath().Match(ex.Message) is { Success: true } named ? named.Groups[1].Value : sectionPath, ex.Message));
            return false;
        }
    }

    /// <summary>
    /// The path of the key a problem of the engine names: its sentences start with the key's full path
    /// (<c>Orkeon:Skills:Library is …</c>). The section's own path when the sentence names no key of it.
    /// </summary>
    public static string PathOf(string problem, string sectionPath)
    {
        if (!problem.StartsWith(sectionPath + ":", StringComparison.Ordinal))
            return sectionPath;

        var end = problem.IndexOf(' ', StringComparison.Ordinal);
        return end < 0 ? sectionPath : problem[..end].TrimEnd(':');
    }

    /// <summary>
    /// The keys of <paramref name="sectionPath"/> that are no setting of it: the host's start refuses a
    /// key no reader of the section declares, a misspelt one first of all. The settings are the public
    /// properties of <paramref name="options"/>, plus the sub-sections another reader declares.
    /// </summary>
    public static IEnumerable<(string Path, string Problem)> UnknownKeys(
        IConfiguration configuration, string sectionPath, Type options, params string[] subsections)
    {
        var known = options.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Concat(subsections)
            .ToList();
        foreach (var child in configuration.GetSection(sectionPath).GetChildren())
        {
            if (!known.Contains(child.Key, StringComparer.OrdinalIgnoreCase))
                yield return (child.Path, $"{child.Path} is not a setting: {sectionPath} carries {string.Join(", ", known)}.");
        }
    }

    // The binder's sentence names the key: Failed to convert configuration value '…' at 'Section:Key' to type '…'.
    [GeneratedRegex(@" at '([^']+)' to type ")]
    private static partial Regex ConvertedPath();
}
