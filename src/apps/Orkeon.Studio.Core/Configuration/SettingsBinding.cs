using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// Reads a settings document as the engine's host does at start: flattened as the JSON configuration
/// provider flattens a file, bound onto an options class with the binder's own conversions, its
/// indifference to letter case and its reading of lists, and its keys judged against the shape of that
/// class. Never a second reading of the JSON.
/// </summary>
internal static partial class SettingsBinding
{
    // How many values of one section may fail to convert before the reading gives up on the rest.
    private const int MaxUnconverted = 32;

    /// <summary>
    /// The document as the JSON configuration provider of .NET flattens a file, key by key in the
    /// file's order: a string as written, a number in its JSON text, a boolean as <c>True</c> or
    /// <c>False</c>, a JSON <c>null</c> and an empty object as a key without a value, an empty array as
    /// an empty text (which the binder then refuses for anything but a string or a list). Keys are
    /// compared whatever their letter case: two objects that differ only by case merge, and a value
    /// written at a key that already holds one makes the provider refuse the whole file, which
    /// <paramref name="duplicateKey"/> then names as the provider names it (the first such key, spelt
    /// as its second writing spells it); the last value written is kept here all the same.
    /// </summary>
    public static Dictionary<string, string?> Flatten(JsonObject root, out string? duplicateKey)
    {
        ArgumentNullException.ThrowIfNull(root);
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        duplicateKey = null;
        VisitObject(root, path: null, data, ref duplicateKey);
        return data;
    }

    private static void VisitObject(JsonObject node, string? path, Dictionary<string, string?> data, ref string? duplicateKey)
    {
        foreach (var (key, child) in node)
            Visit(child, path is null ? key : $"{path}:{key}", data, ref duplicateKey);
        if (node.Count == 0 && path is not null)
            data[path] = null;
    }

    private static void Visit(JsonNode? node, string path, Dictionary<string, string?> data, ref string? duplicateKey)
    {
        switch (node)
        {
            case JsonObject child:
                VisitObject(child, path, data, ref duplicateKey);
                break;
            case JsonArray items:
                for (var i = 0; i < items.Count; i++)
                    Visit(items[i], $"{path}:{i.ToString(CultureInfo.InvariantCulture)}", data, ref duplicateKey);
                if (items.Count == 0)
                    data[path] = string.Empty;
                break;
            default:
                // A value, a null included: the one writing the provider refuses twice at one key.
                if (data.ContainsKey(path))
                    duplicateKey ??= path;
                data[path] = node is JsonValue value ? Text(value) : null;
                break;
        }
    }

    // A number JSON cannot write (an infinity set in memory) has no text: read as a key without a value.
    private static string? Text(JsonValue value)
    {
        try
        {
            return value.GetValueKind() switch
            {
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Null => null,
                _ => AppSettingsDocument.ReadString(value),
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Binds <paramref name="sectionPath"/> onto a new <typeparamref name="T"/>, every value that
    /// converts. Each value that does not adds the binder's sentence to <paramref name="problems"/>, at
    /// the key it names, and its key keeps the default: the host's start stops on the first such value
    /// and says that one only, so the first sentence added is the host's, and the following ones are
    /// what it says once the first is mended.
    /// </summary>
    public static T Bind<T>(IConfiguration configuration, string sectionPath, List<(string Path, string Problem)> problems)
        where T : class, new()
    {
        for (var unconverted = 0; ; unconverted++)
        {
            var options = new T();
            try
            {
                configuration.GetSection(sectionPath).Bind(options);
                return options;
            }
            catch (InvalidOperationException ex)
            {
                var named = ConvertedPath().Match(ex.Message);
                problems.Add((named.Success ? named.Groups[1].Value : sectionPath, ex.Message));
                if (!named.Success || unconverted >= MaxUnconverted)
                    return options;

                configuration = Without(configuration, named.Groups[1].Value);
            }
        }
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="sectionPath"/> or a key under it, in any letter case.</summary>
    public static bool IsUnder(string path, string sectionPath) =>
        string.Equals(path, sectionPath, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(sectionPath + ConfigurationPath.KeyDelimiter, StringComparison.OrdinalIgnoreCase);

    // The configuration with the key at the path, and everything under it, left out.
    private static IConfiguration Without(IConfiguration configuration, string path) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(configuration.AsEnumerable().Where(pair => !IsUnder(pair.Key, path)))
            .Build();

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
    /// The keys of <paramref name="sectionPath"/> that are no setting of it, at any depth, as the
    /// host's start walks a declared section (<c>SettingsValidation.Walk</c>): a key no reader of the
    /// section declares, a misspelt one first of all; a key under a single value, an item of a list of
    /// names among them; and a number read as a float or a double that is not finite. The settings are
    /// the public properties of <paramref name="options"/> as the binder reads them (a dictionary takes
    /// any key, a list any index, a value none), plus the sub-sections another reader declares. In the
    /// engine's sentences, which may say more after them: a closest name, where the keys are listed.
    /// </summary>
    public static IEnumerable<(string Path, string Problem)> UnknownKeys(
        IConfiguration configuration, string sectionPath, Type options, params (string Key, Type Options)[] subsections)
    {
        var shape = Shape.Of(options);
        foreach (var (key, type) in subsections)
            shape.Named[key] = Shape.Of(type);
        return Walk(configuration.GetSection(sectionPath), shape);
    }

    private static IEnumerable<(string Path, string Problem)> Walk(IConfigurationSection section, Shape shape)
    {
        foreach (var child in section.GetChildren())
        {
            if (shape.Child(child.Key) is not { } below)
            {
                yield return (child.Path, shape.TakesKeys
                    ? $"{child.Path} is not a setting: {section.Path} carries {string.Join(", ", shape.Named.Keys)}."
                    : $"{child.Path} is not a setting: {section.Path} is a single value.");
                continue;
            }

            if (IsNotFinite(child.Value, below.Number))
            {
                yield return (child.Path, $"{child.Path} is '{child.Value}', which is not a finite number.");
                continue;
            }

            foreach (var problem in Walk(child, below))
                yield return problem;
        }
    }

    // NaN, an infinity, or a number past the type's range, which the binder reads as one.
    private static bool IsNotFinite(string? value, Type? number)
    {
        const NumberStyles Styles = NumberStyles.Float | NumberStyles.AllowThousands;
        if (value is null || number is null)
            return false;

        return number == typeof(float)
            ? float.TryParse(value, Styles, CultureInfo.InvariantCulture, out var single) && !float.IsFinite(single)
            : double.TryParse(value, Styles, CultureInfo.InvariantCulture, out var real) && !double.IsFinite(real);
    }

    /// <summary>
    /// The keys a type takes as the binder reads it, restated from the engine's <c>SettingsShape</c>
    /// (internal to its host): a public readable property is a key, a dictionary takes any key, a list
    /// any index, and a value (anything the binder converts from text) none.
    /// </summary>
    private sealed class Shape
    {
        /// <summary>The keys the node names, each with the shape below it, in the order the type declares them.</summary>
        public Dictionary<string, Shape> Named { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The shape of any other key (a dictionary's entries, a list's items); null when the node names its keys.</summary>
        public Shape? Entries { get; private init; }

        /// <summary>Whether anything goes below the node.</summary>
        public bool Open { get; private init; }

        /// <summary>The float or double the binder reads the node's value as, which must then be finite; else null.</summary>
        public Type? Number { get; private init; }

        public bool TakesKeys => Open || Entries is not null || Named.Count > 0;

        public Shape? Child(string key) => Open ? this : Named.GetValueOrDefault(key) ?? Entries;

        public static Shape Of(Type type) => Of(type, []);

        private static Shape Of(Type type, HashSet<Type> visiting)
        {
            var target = Nullable.GetUnderlyingType(type) ?? type;
            if (Leaf(target, visiting) is { } leaf)
                return leaf;

            // A type that contains itself takes anything below its second appearance.
            if (!visiting.Add(target))
                return new Shape { Open = true };

            var shape = new Shape();
            foreach (var property in target.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetMethod is null)
                    continue;

                shape.Named[property.GetCustomAttribute<ConfigurationKeyNameAttribute>()?.Name ?? property.Name] = Of(property.PropertyType, visiting);
            }

            visiting.Remove(target);
            return shape;
        }

        private static Shape? Leaf(Type target, HashSet<Type> visiting)
        {
            if (target == typeof(object) || typeof(IConfiguration).IsAssignableFrom(target)
                || target.Namespace is "System.Text.Json" or "System.Text.Json.Nodes")
            {
                return new Shape { Open = true };
            }

            if (target.IsPrimitive || target.IsEnum || target == typeof(string) || target == typeof(decimal)
                || TypeDescriptor.GetConverter(target).CanConvertFrom(typeof(string)))
            {
                return new Shape { Number = target == typeof(double) || target == typeof(float) ? target : null };
            }

            var interfaces = target.IsInterface ? [target, .. target.GetInterfaces()] : target.GetInterfaces();
            var dictionary = interfaces.FirstOrDefault(candidate => candidate.IsGenericType
                && (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) || candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
                && candidate.GetGenericArguments()[0] == typeof(string));
            if (dictionary is not null)
                return new Shape { Entries = Of(dictionary.GetGenericArguments()[1], visiting) };
            if (typeof(IDictionary).IsAssignableFrom(target))
                return new Shape { Open = true };

            var items = target.IsArray
                ? target.GetElementType()
                : interfaces.FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    ?.GetGenericArguments()[0];
            return items is null ? null : new Shape { Entries = Of(items, visiting) };
        }
    }

    // The binder's sentence names the key: Failed to convert configuration value '…' at 'Section:Key' to type '…'.
    [GeneratedRegex(@" at '([^']+)' to type '[^']+'\.$")]
    private static partial Regex ConvertedPath();
}
