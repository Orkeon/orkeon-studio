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
    /// <summary>
    /// The words of the start's refusal of a key written without a value on a value type (Orkeon #450):
    /// <c>{key} is written without a value, which would bind it to ...</c>.
    /// </summary>
    public const string WrittenWithoutValue = " is written without a value, ";

    /// <summary>What the walk of the keys found at a key (the engine's <c>SettingsValidation.Found</c>).</summary>
    internal enum WalkFinding
    {
        /// <summary>A key no declaration knows.</summary>
        Key,

        /// <summary>A value the binder would take, drop or misread without a word.</summary>
        Value,

        /// <summary>A key written without a value, which the binder would bind to its type's default.</summary>
        Empty,
    }

    /// <summary>One finding of the walk: the path of its key, the engine's sentence, and what it is.</summary>
    internal readonly record struct Walked(string Path, string Problem, WalkFinding Kind);

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
    /// names among them. And a value the binder would take, drop or misread without a word: a number
    /// read as a float or a double that is not finite; a key written without a value (a JSON null, an
    /// empty object) on a setting the binder would bind to its type's default (Orkeon #450); a single
    /// value where a list or a section is expected (#454, #455), unless the list's reader also takes
    /// one comma-separated value; inside a list or a dictionary, where the binder drops what it cannot
    /// convert, a boolean or a number that does not convert (#454). The settings are the public
    /// properties of <paramref name="options"/> the binder writes or fills (a computed one is none,
    /// #449; a dictionary takes any key, a list any index, a value none), plus the sub-sections another
    /// reader declares. In the engine's sentences, which may say more after them: a closest name, where
    /// the keys are listed. Merge them with the rules of the section by <see cref="AddWalked"/>.
    /// </summary>
    public static IEnumerable<Walked> UnknownKeys(
        IConfiguration configuration, string sectionPath, Type options, params (string Key, Type Options)[] subsections)
    {
        var shape = Shape.Of(options);
        foreach (var (key, type) in subsections)
            shape.Named[key] = Shape.Of(type);
        return Walk(configuration.GetSection(sectionPath), shape, inCollection: false);
    }

    /// <summary>
    /// As <see cref="UnknownKeys(IConfiguration, string, Type, ValueTuple{string, Type}[])"/>, for a
    /// section several readers declare, in the order the host meets them: a reader with no key adds its
    /// properties to the section's own keys, one with a key declares the sub-section of that name. The
    /// order is the one the engine's sentence lists the keys in.
    /// </summary>
    public static IEnumerable<Walked> UnknownKeysOfReaders(
        IConfiguration configuration, string sectionPath, params (string? Key, Type Options)[] readers)
    {
        var shape = new Shape();
        foreach (var (key, type) in readers)
        {
            var read = Shape.Of(type);
            if (key is not null)
            {
                shape.Named[key] = read;
                continue;
            }

            foreach (var (name, below) in read.Named)
                shape.Named.TryAdd(name, below);
        }

        return Walk(configuration.GetSection(sectionPath), shape, inCollection: false);
    }

    /// <summary>
    /// Adds the findings of a walk to the problems the section's rules and its binder found, one per
    /// key, as the host's start merges them: a key written without a value replaces what a rule said of
    /// the default the binder then wrote (<c>0</c> for a file that says null); a value the walk refuses
    /// is said only when no sentence already names its key (or, for a list item, its list).
    /// </summary>
    public static void AddWalked(List<(string Path, string Problem)> problems, IEnumerable<Walked> walked)
    {
        ArgumentNullException.ThrowIfNull(problems);
        ArgumentNullException.ThrowIfNull(walked);
        foreach (var found in walked.ToList())
        {
            if (found.Kind == WalkFinding.Empty)
                problems.RemoveAll(earlier => NamesExactly(earlier.Problem, found.Path));
            else if (found.Kind == WalkFinding.Value && problems.Exists(earlier => Names(earlier.Problem, found.Path)))
                continue;

            problems.Add((found.Path, found.Problem));
        }
    }

    // Whether the sentence names the key at the path or, for a list item, the list: a whole key, not the start of a longer one.
    private static bool Names(string problem, string path)
    {
        var last = path.LastIndexOf(':');
        var parent = last > 0 && path[(last + 1)..].All(char.IsAsciiDigit) ? path[..last] : null;
        return NamesExactly(problem, path) || (parent is not null && NamesExactly(problem, parent));
    }

    private static bool NamesExactly(string problem, string key)
    {
        for (var at = problem.IndexOf(key, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = problem.IndexOf(key, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + key.Length;
            if (end == problem.Length || !char.IsAsciiLetterOrDigit(problem[end]))
                return true;
        }

        return false;
    }

    private static IEnumerable<Walked> Walk(IConfigurationSection section, Shape shape, bool inCollection)
    {
        foreach (var child in section.GetChildren())
        {
            if (shape.Child(child.Key) is not { } below)
            {
                yield return new Walked(child.Path, shape.TakesKeys
                    ? $"{child.Path} is not a setting: {section.Path} carries {string.Join(", ", shape.Named.Keys)}."
                    : $"{child.Path} is not a setting: {section.Path} is a single value.", WalkFinding.Key);
                continue;
            }

            var item = inCollection || (!shape.Open && shape.Entries is not null && !shape.Named.ContainsKey(child.Key));
            if (ValueProblem(child, below, item) is { } problem)
            {
                yield return new Walked(child.Path, problem, child.Value is null ? WalkFinding.Empty : WalkFinding.Value);
                continue;
            }

            foreach (var found in Walk(child, below, item))
                yield return found;
        }
    }

    // What is wrong with the value of a key, as the engine's walk judges it (SettingsValidation.ValueRefusal); null when nothing.
    private static string? ValueProblem(IConfigurationSection child, Shape below, bool inCollection)
    {
        if (below.Open || child.GetChildren().Any())
            return null;

        var value = child.Value;
        if (value is null)
        {
            // A JSON null and {} read alike: no value, no child, and the binder writes the type's default.
            var bound = below.TakesKeys ? null : below.Values.FirstOrDefault(IsDefaultedWhenEmpty);
            return bound is null
                ? null
                : $"{child.Path}{WrittenWithoutValue}which would bind it to {Default(bound)}: "
                  + $"write {Expected(bound)}, or leave the key out and its default applies.";
        }

        if (below.Values.Count == 0)
        {
            // An empty JSON array reads as an empty value; a list whose reader splits one
            // comma-separated value takes that form.
            if (!below.TakesKeys || value.Length == 0 || (below.IsList && below.TakesCommaSeparated))
                return null;

            // The value is not echoed: written where a section is expected, it may be a secret.
            return below.IsList
                ? $"{child.Path} is a single value where a list is expected: write its items as a list, or leave the key out."
                : $"{child.Path} is a single value where a section of keys is expected: write its keys, or leave the key out.";
        }

        if (IsNotFinite(value, below.Number))
            return $"{child.Path} is '{value}', which is not a finite number.";

        return inCollection ? below.Values.Select(declared => Unconverted(child.Path, value, declared)).FirstOrDefault(problem => problem is not null) : null;
    }

    // A value type the binder writes its default to when the key holds no value: false, 0, the first member.
    private static bool IsDefaultedWhenEmpty(Type type) => type.IsValueType && Nullable.GetUnderlyingType(type) is null;

    private static string Default(Type type) =>
        type == typeof(bool) ? "false"
        : IsNumber(type) ? "0"
        : type.IsEnum ? Enum.ToObject(type, 0).ToString() ?? type.Name
        : $"the default of {type.Name}";

    private static string Expected(Type type) =>
        type == typeof(bool) ? "true or false"
        : type.IsEnum ? "one of " + string.Join(", ", Enum.GetNames(type))
        : IsNumber(type) ? "a number"
        : $"a {type.Name}";

    private static bool IsNumber(Type type) =>
        !type.IsEnum && Type.GetTypeCode(type) is >= TypeCode.SByte and <= TypeCode.Decimal;

    // The binder's sentence for a value it cannot convert, or null; neither a string nor an enumeration is judged.
    private static string? Unconverted(string path, string value, Type declared)
    {
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        if (type == typeof(string) || type.IsEnum || (value.Length == 0 && type != declared))
            return null;

        try
        {
            _ = TypeDescriptor.GetConverter(type).ConvertFromInvariantString(value);
            return null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException or OverflowException
                                       || ex.InnerException is FormatException or OverflowException)
        {
            // A boolean or a number cannot be a secret, and is echoed; any other value is not.
            return type == typeof(bool) || IsNumber(type)
                ? $"Failed to convert configuration value '{value}' at '{path}' to type '{type}'."
                : $"Failed to convert the configuration value at '{path}' to type '{type}'.";
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
    /// (internal to its host): a public readable property the binder writes (a public setter) or fills
    /// (a dictionary, a list or an object behind a getter) is a key, a computed value none; a
    /// dictionary takes any key, a list any index, and a value (anything the binder converts from
    /// text) none, carrying the type it is bound to.
    /// </summary>
    private sealed class Shape
    {
        /// <summary>The keys the node names, each with the shape below it, in the order the type declares them.</summary>
        public Dictionary<string, Shape> Named { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The shape of any other key (a dictionary's entries, a list's items); null when the node names its keys.</summary>
        public Shape? Entries { get; private init; }

        /// <summary>Whether anything goes below the node.</summary>
        public bool Open { get; private init; }

        /// <summary>The types the binder converts the node's value to, as declared (a nullable one included); empty when the node is no value.</summary>
        public HashSet<Type> Values { get; } = [];

        /// <summary>Whether the binder reads the node's items as a list, not a dictionary's entries.</summary>
        public bool IsList { get; private init; }

        /// <summary>Whether the list's reader also takes it as one comma-separated value (<c>CommaSeparatedSettingAttribute</c>).</summary>
        public bool TakesCommaSeparated { get; private set; }

        /// <summary>The float or double the binder reads the node's value as, which must then be finite; else null.</summary>
        public Type? Number
        {
            get
            {
                var types = Values.Select(type => Nullable.GetUnderlyingType(type) ?? type).ToList();
                return types.Contains(typeof(float)) ? typeof(float) : types.Contains(typeof(double)) ? typeof(double) : null;
            }
        }

        public bool TakesKeys => Open || Entries is not null || Named.Count > 0;

        public Shape? Child(string key) => Open ? this : Named.GetValueOrDefault(key) ?? Entries;

        public static Shape Of(Type type) => Of(type, []);

        private static Shape Of(Type type, HashSet<Type> visiting)
        {
            var target = Nullable.GetUnderlyingType(type) ?? type;
            if (Leaf(type, target, visiting) is { } leaf)
                return leaf;

            // A type that contains itself takes anything below its second appearance.
            if (!visiting.Add(target))
                return new Shape { Open = true };

            var shape = new Shape();
            foreach (var property in target.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetGetMethod() is null || !IsBound(property))
                    continue;

                var below = Of(property.PropertyType, visiting);
                if (below.IsList && property.IsDefined(typeof(Orkeon.Application.Configuration.CommaSeparatedSettingAttribute), inherit: true))
                    below.TakesCommaSeparated = true;
                shape.Named[property.GetCustomAttribute<ConfigurationKeyNameAttribute>()?.Name ?? property.Name] = below;
            }

            visiting.Remove(target);
            return shape;
        }

        // Whether the binder sets or fills the property: a public setter writes any; behind a getter
        // alone or a private setter, it fills a dictionary, a list or an object, and cannot write a value.
        private static bool IsBound(PropertyInfo property)
        {
            if (property.GetSetMethod() is not null)
                return true;

            var target = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            return !IsValue(target) || target == typeof(object) || IsJsonValue(target);
        }

        private static bool IsJsonValue(Type target) => target.Namespace is "System.Text.Json" or "System.Text.Json.Nodes";

        private static bool IsValue(Type target) =>
            target.IsPrimitive || target.IsEnum || target == typeof(string) || target == typeof(decimal)
            || TypeDescriptor.GetConverter(target).CanConvertFrom(typeof(string));

        private static Shape? Leaf(Type declared, Type target, HashSet<Type> visiting)
        {
            if (target == typeof(object) || typeof(IConfiguration).IsAssignableFrom(target) || IsJsonValue(target))
                return new Shape { Open = true };

            if (IsValue(target))
            {
                var value = new Shape();
                value.Values.Add(declared);
                return value;
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
            return items is null ? null : new Shape { Entries = Of(items, visiting), IsList = true };
        }
    }

    // The binder's sentence names the key: Failed to convert configuration value '…' at 'Section:Key' to type '…'.
    [GeneratedRegex(@" at '([^']+)' to type '[^']+'\.$")]
    private static partial Regex ConvertedPath();
}
