using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Orkeon.Studio.Core.Profiles;

/// <summary>
/// A connection to a provider, as the settings screen records it: a card, an endpoint, the variable
/// holding its key and the workspace the key acts in, typed once, then one or more models reached
/// through them (<see cref="Models"/>). Each (connection, model) pair is the setting teams, the
/// elections and the crews name: a <see cref="ModelProfile"/>, computed by <see cref="Flatten"/>,
/// named by <see cref="PairName"/> (« mydeepseek.flash ») and mirrored as the host profile of its
/// slug (<c>Llm:Profiles:mydeepseek-flash</c>) — the engine takes one model per profile, so the
/// connection itself is never written to the settings file.
/// <para>
/// A connection of one model without alias nor setting of its own is written in the form a Studio
/// before connections wrote, the model in <see cref="Model"/> and no <see cref="Models"/>: a file that
/// declares no second model stays readable by such a Studio, and its pair keeps the connection's
/// name — the name teams already reference.
/// </para>
/// </summary>
public sealed record ModelConnection
{
    /// <summary>User-chosen name; the name of its pairs (<see cref="PairName"/>).</summary>
    public required string Name { get; init; }

    /// <summary>The connection's card (<see cref="ModelProfile.Provider"/>).</summary>
    public string? Provider { get; init; }

    /// <summary>The model of a connection that declares one in the historical form; ignored once <see cref="Models"/> holds one.</summary>
    public string? Model { get; init; }

    /// <summary>Name of the variable holding the API key (<see cref="ModelProfile.KeyEnvName"/>), never the key.</summary>
    public string? KeyEnvName { get; init; }

    /// <summary>The Anthropic workspace the key acts in (<see cref="ModelProfile.WorkspaceId"/>).</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>The temperature every model inherits unless it pins its own.</summary>
    public double? Temperature { get; init; }

    /// <summary>The HTTP timeout, in seconds, every model inherits unless it pins its own.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>The longest silence of a stream, in seconds, every model inherits unless it pins its own.</summary>
    public int? StreamIdleSeconds { get; init; }

    /// <summary>The call mode every model inherits unless it pins its own.</summary>
    public string? CallMode { get; init; }

    /// <summary>The response cap, in tokens, every model inherits unless it pins its own.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>The thinking switch every model inherits unless it pins its own.</summary>
    public bool? ThinkingEnabled { get; init; }

    /// <summary>The reasoning effort every model inherits unless it pins its own.</summary>
    public string? ThinkingEffort { get; init; }

    /// <summary>Endpoint base URL.</summary>
    [SuppressMessage("Design", "CA1056",
        Justification = "User-typed form value round-tripped verbatim into a JSON string field; " +
                        "a half-typed URL must be carried and re-shown, which System.Uri cannot do.")]
    public string? BaseUrl { get; init; }

    /// <summary>
    /// The models the connection declares, in display order; null or empty for a connection written
    /// in the historical form, whose one model is <see cref="Model"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ConnectionModel>? Models { get; init; }

    /// <summary>
    /// The models, the historical form read as one model without alias: never empty, so that a
    /// connection always gives at least one pair; a <c>null</c> a hand edit left in the list is none.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ConnectionModel> DeclaredModels =>
        Models?.OfType<ConnectionModel>().ToList() is { Count: > 0 } models ? models : [new ConnectionModel { Model = Model }];

    /// <summary>
    /// The name of the pair of <paramref name="connection"/> and the model of alias <paramref name="alias"/>:
    /// <c>connection.alias</c>, or the connection's own name for a model without alias — the one model of
    /// a connection in the historical form.
    /// </summary>
    public static string PairName(string connection, string? alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        return string.IsNullOrWhiteSpace(alias) ? connection : $"{connection}.{alias.Trim()}";
    }

    /// <summary>The name of each pair, in the order of the models.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> PairNames => [.. DeclaredModels.Select(model => PairName(Name, model.Alias))];

    /// <summary>
    /// Each pair as the setting the rest of Studio names: the connection's card, endpoint, key and
    /// workspace, the model's identifier, and each setting the model pins, else the connection's.
    /// </summary>
    public IReadOnlyList<ModelProfile> Flatten() => [.. DeclaredModels.Select(Pair)];

    /// <summary>The pair of this connection and <paramref name="model"/>.</summary>
    public ModelProfile Pair(ConnectionModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var alias = string.IsNullOrWhiteSpace(model.Alias) ? null : model.Alias.Trim();
        return new ModelProfile
        {
            Name = PairName(Name, alias),
            Connection = Name,
            Alias = alias,
            Provider = Provider,
            Model = model.Model,
            BaseUrl = BaseUrl,
            KeyEnvName = KeyEnvName,
            WorkspaceId = WorkspaceId,
            Temperature = model.Temperature ?? Temperature,
            TimeoutSeconds = model.TimeoutSeconds ?? TimeoutSeconds,
            StreamIdleSeconds = model.StreamIdleSeconds ?? StreamIdleSeconds,
            CallMode = model.CallMode ?? CallMode,
            MaxTokens = model.MaxTokens ?? MaxTokens,
            ThinkingEnabled = model.ThinkingEnabled ?? ThinkingEnabled,
            ThinkingEffort = model.ThinkingEffort ?? ThinkingEffort,
        };
    }

    /// <summary>
    /// The connection of one model a setting describes, in the historical form: its fields, its name,
    /// its model — what a setting made before connections, or by a caller that knows one model, is.
    /// </summary>
    public static ModelConnection Of(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ModelConnection
        {
            Name = profile.Name,
            Provider = profile.Provider,
            Model = profile.Model,
            BaseUrl = profile.BaseUrl,
            KeyEnvName = profile.KeyEnvName,
            WorkspaceId = profile.WorkspaceId,
            Temperature = profile.Temperature,
            TimeoutSeconds = profile.TimeoutSeconds,
            StreamIdleSeconds = profile.StreamIdleSeconds,
            CallMode = profile.CallMode,
            MaxTokens = profile.MaxTokens,
            ThinkingEnabled = profile.ThinkingEnabled,
            ThinkingEffort = profile.ThinkingEffort,
        };
    }

    /// <summary>
    /// The connection as the file writes it: one model without alias nor setting of its own in the
    /// historical form (<see cref="Model"/>, no <see cref="Models"/>); otherwise the list, and no
    /// <see cref="Model"/> beside it, which the list would make a second, ignored answer.
    /// </summary>
    public ModelConnection Normalized()
    {
        if (Models?.OfType<ConnectionModel>().ToList() is not { Count: > 0 } models)
            return this with { Models = null };

        return models is [var only] && string.IsNullOrWhiteSpace(only.Alias) && !only.PinsASetting
            ? this with { Model = only.Model, Models = null }
            : this with { Model = null, Models = models };
    }
}

/// <summary>
/// One model of a <see cref="ModelConnection"/>: the alias its pair is named by, the identifier the
/// endpoint expects, and each setting it pins over the connection's — null inherits it.
/// </summary>
public sealed record ConnectionModel
{
    /// <summary>The model's short name in its pair (« flash » in « mydeepseek.flash »); optional for the one model of a connection.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Alias { get; init; }

    /// <summary>Model identifier, as the endpoint expects it.</summary>
    public string? Model { get; init; }

    /// <summary>The temperature this model pins, or null for the connection's.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; init; }

    /// <summary>The HTTP timeout, in seconds, this model pins, or null for the connection's.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeoutSeconds { get; init; }

    /// <summary>The longest silence of a stream this model allows, or null for the connection's.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StreamIdleSeconds { get; init; }

    /// <summary>The call mode this model pins, or null for the connection's.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallMode { get; init; }

    /// <summary>The response cap this model pins, or null for the connection's.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; init; }

    /// <summary>The thinking switch this model pins, or null for the connection's.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ThinkingEnabled { get; init; }

    /// <summary>The reasoning effort this model pins, or null for the connection's.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThinkingEffort { get; init; }

    /// <summary>Whether the model pins any setting over its connection's.</summary>
    [JsonIgnore]
    public bool PinsASetting =>
        Temperature is not null || TimeoutSeconds is not null || StreamIdleSeconds is not null || CallMode is not null
        || MaxTokens is not null || ThinkingEnabled is not null || ThinkingEffort is not null;
}
