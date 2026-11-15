using System.Globalization;
using System.Text.Json.Nodes;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// A JSON subtree as the run's configuration sees it: every key split at <c>:</c> into nesting
/// levels, keys compared without case, an array read as an object keyed by index, and a scalar as its
/// text. A key holding an empty object, an empty array or a JSON null is a node with no child: the
/// run reads no value worth judging there.
/// <para>
/// Keys that come to one same path - equal but for the case, or once split at <c>:</c> - are read as
/// the JSON configuration reads them. Two objects or lists that set no same key below are one node,
/// the keys of both together. A value - a JSON null included - written at a path that already holds
/// one is refused by the configuration, which throws on a key written twice and reads nothing of the
/// file: the node is listed in <see cref="WrittenTwice"/>, and keeps the last value for a reader that
/// goes on. An empty object or list holds its path too, but is refused nowhere: it replaces the
/// value written before it with none.
/// </para>
/// </summary>
internal sealed class SettingNode
{
    private readonly Dictionary<string, SettingNode> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SettingNode> _writtenTwice;
    private bool _held;

    private SettingNode(string key, string path, List<SettingNode> writtenTwice)
    {
        Key = key;
        Path = path;
        _writtenTwice = writtenTwice;
    }

    /// <summary>The key of the node under its parent, as first written.</summary>
    public string Key { get; }

    /// <summary>The colon-separated path of the node.</summary>
    public string Path { get; }

    /// <summary>The text of the scalar written at the node; null when none is.</summary>
    public string? Value { get; private set; }

    /// <summary>The nodes below, in the order they were first written.</summary>
    public IReadOnlyCollection<SettingNode> Children => _children.Values;

    /// <summary>
    /// The nodes of the whole tree a value is written at twice, each once, in the order the second
    /// value was met: where the JSON configuration throws on a duplicate key, so that no run reads
    /// the file. Empty for a tree the configuration loads.
    /// </summary>
    public IReadOnlyList<SettingNode> WrittenTwice => _writtenTwice;

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
        var root = new SettingNode(separator < 0 ? path : path[(separator + 1)..], path, []);
        root.Read(json);
        return root;
    }

    private void Read(JsonNode? json)
    {
        switch (json)
        {
            case JsonObject { Count: > 0 } properties:
                foreach (var (key, value) in properties)
                    Descend(key).Read(value);
                break;
            case JsonArray { Count: > 0 } items:
                for (var index = 0; index < items.Count; index++)
                    Descend(index.ToString(CultureInfo.InvariantCulture)).Read(items[index]);
                break;
            case JsonObject or JsonArray:
                // Empty: it holds the path and replaces what was written there, without a refusal.
                _held = true;
                Value = null;
                break;
            default:
                // A scalar or a JSON null: refused at a path that already holds a value.
                if (_held && !_writtenTwice.Contains(this))
                    _writtenTwice.Add(this);

                _held = true;
                Value = json is JsonValue scalar
                    ? scalar.TryGetValue<string>(out var text) ? text : scalar.ToJsonString()
                    : null;
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
                next = new SettingNode(segment, $"{node.Path}:{segment}", _writtenTwice);
                node._children[segment] = next;
            }

            node = next;
        }

        return node;
    }
}
