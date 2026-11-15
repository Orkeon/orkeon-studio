using System.Globalization;
using System.Text.Json.Nodes;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// A JSON subtree as the run's configuration sees it: every key split at <c>:</c> into nesting
/// levels, keys merged without case, an array read as an object keyed by index, and a scalar as its
/// text. A key holding an empty object, an empty array or a JSON null is a node with no child: the
/// run reads no value worth judging there.
/// </summary>
internal sealed class SettingNode
{
    private readonly Dictionary<string, SettingNode> _children = new(StringComparer.OrdinalIgnoreCase);

    private SettingNode(string key, string path)
    {
        Key = key;
        Path = path;
    }

    /// <summary>The key of the node under its parent, as first written.</summary>
    public string Key { get; }

    /// <summary>The colon-separated path of the node.</summary>
    public string Path { get; }

    /// <summary>The text of the scalar written at the node; null when none is.</summary>
    public string? Value { get; private set; }

    /// <summary>The nodes below, in the order they were first written.</summary>
    public IReadOnlyCollection<SettingNode> Children => _children.Values;

    /// <summary>Whether anything is written below the node.</summary>
    public bool HasChildren => _children.Count > 0;

    /// <summary>The scalar's text, trimmed; null when blank or absent, which the run reads as left out.</summary>
    public string? Text => string.IsNullOrWhiteSpace(Value) ? null : Value.Trim();

    /// <summary>The node below <paramref name="key"/>, compared without case; null when none.</summary>
    public SettingNode? Child(string key) => _children.GetValueOrDefault(key);

    /// <summary>Reads <paramref name="json"/>, found at <paramref name="path"/>, as the run's configuration does.</summary>
    public static SettingNode Of(JsonNode? json, string path)
    {
        var separator = path.LastIndexOf(':');
        var root = new SettingNode(separator < 0 ? path : path[(separator + 1)..], path);
        root.Read(json);
        return root;
    }

    private void Read(JsonNode? json)
    {
        switch (json)
        {
            case JsonObject properties:
                foreach (var (key, value) in properties)
                    Descend(key).Read(value);
                break;
            case JsonArray items:
                for (var index = 0; index < items.Count; index++)
                    Descend(index.ToString(CultureInfo.InvariantCulture)).Read(items[index]);
                break;
            case JsonValue scalar:
                Value = scalar.TryGetValue<string>(out var text) ? text : scalar.ToJsonString();
                break;
            default:
                break;
        }
    }

    private SettingNode Descend(string key)
    {
        var node = this;
        foreach (var segment in key.Split(':'))
        {
            if (!node._children.TryGetValue(segment, out var next))
            {
                next = new SettingNode(segment, $"{node.Path}:{segment}");
                node._children[segment] = next;
            }

            node = next;
        }

        return node;
    }
}
