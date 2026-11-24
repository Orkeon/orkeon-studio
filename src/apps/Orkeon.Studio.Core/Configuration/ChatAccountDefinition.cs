using System.Collections.Immutable;

namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// One chat account as <c>Orkeon:Tools:Chat:Accounts</c> spells it: every key the engine's
/// binder reads, flattened, on the model of <see cref="DriveAccountDefinition"/>. A field left
/// <see langword="null"/> is a key that is absent — never an empty string, which the engine reads
/// as a value and not as "unset" — so the engine's own default applies to it. The enumerations are
/// kept as the text the file holds, in the spelling of <see cref="ChatSection"/>; this type validates nothing.
/// </summary>
public sealed record ChatAccountDefinition
{
    /// <summary>The dictionary key under <c>Orkeon:Tools:Chat:Accounts</c>; what an agent passes as <c>account</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The service, one of <see cref="ChatSection.Providers"/>; mandatory to the engine.</summary>
    public string? Provider { get; init; }

    /// <summary>What an agent may do. <see cref="ChatRights.None"/> writes no key.</summary>
    public ChatRights Rights { get; init; }

    /// <summary>
    /// The <c>Rights</c> text the file holds when it is not a list of rights the engine reads
    /// (a misspelt name, a number, <c>None</c>), set only while <see cref="Rights"/> is
    /// <see cref="ChatRights.None"/>: it is written back as it was until a right is chosen.
    /// </summary>
    public string? RightsRaw { get; init; }

    /// <summary><c>Auth:Method</c>, one of <see cref="ChatSection.AuthMethods"/>; absent means a user signs in.</summary>
    public string? AuthMethod { get; init; }

    /// <summary><c>Auth:ClientId</c>: the id of the Microsoft Entra application.</summary>
    public string? ClientId { get; init; }

    /// <summary><c>Auth:Tenant</c>: a tenant id, domain or alias; absent means <c>organizations</c>.</summary>
    public string? Tenant { get; init; }

    /// <summary><c>Auth:AdminGranted</c>: what an administrator of the tenant consented to, of <see cref="ChatSection.Grants"/>.</summary>
    public ImmutableList<string> AdminGranted { get; init; } = [];

    /// <summary><c>Auth:CertificatePathEnvVar</c>: the <i>name</i> of the variable that holds the path of the certificate.</summary>
    public string? CertificatePathEnvVar { get; init; }

    /// <summary><c>Auth:FederatedTokenFileEnvVar</c>: the <i>name</i> of the variable that holds the path of the federated token file.</summary>
    public string? FederatedTokenFileEnvVar { get; init; }

    /// <summary><c>Auth:ClientSecretEnvVar</c>: the <i>name</i> of the variable that holds the client secret.</summary>
    public string? ClientSecretEnvVar { get; init; }

    /// <summary><c>Webhook:UrlEnvVar</c>: the <i>name</i> of the variable that holds the webhook's address, which is a secret.</summary>
    public string? WebhookEnvVar { get; init; }

    /// <summary><c>Cloud</c>, one of <see cref="ChatSection.Clouds"/>; absent means the worldwide one.</summary>
    public string? Cloud { get; init; }

    /// <summary><c>Read:AllowedPlaces</c>: the places an agent may read; empty means none.</summary>
    public ImmutableList<string> ReadPlaces { get; init; } = [];

    /// <summary><c>Send:AllowedPlaces</c>: the places an agent may post in; empty means none.</summary>
    public ImmutableList<string> SendPlaces { get; init; } = [];

    /// <summary><c>Send:MaxPerHour</c>: the most writes in an hour; absent sets no cap.</summary>
    public int? MaxPerHour { get; init; }
}
