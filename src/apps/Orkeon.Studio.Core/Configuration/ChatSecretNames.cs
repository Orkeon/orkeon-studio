namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// The environment variables Studio keeps the secrets of a chat account in when the file names
/// none: <c>CHAT_&lt;ACCOUNT&gt;_CLIENT_SECRET</c> for an application's secret and
/// <c>CHAT_&lt;ACCOUNT&gt;_WEBHOOK_URL</c> for a webhook's address, on the convention of
/// <see cref="EmailSecretNames"/> — the account's name in upper case with everything that is no
/// letter or digit turned into <c>_</c>, ranked <c>_2</c>, <c>_3</c> clear of the names the other
/// accounts already hold, whatever their case. Never <c>ORKEON_</c>: a run reads such a variable
/// as configuration. A name the file already holds is not derived again.
/// </summary>
public static class ChatSecretNames
{
    private const string Prefix = "CHAT_";
    private const string ClientSecretSuffix = "_CLIENT_SECRET";
    private const string WebhookUrlSuffix = "_WEBHOOK_URL";

    /// <summary>The variable of the client secret of the account <paramref name="accountName"/>.</summary>
    /// <param name="accountName">The account's name, as the file holds it.</param>
    /// <param name="taken">The variables the other accounts of the file already name.</param>
    public static string ClientSecretFor(string accountName, IEnumerable<string> taken) =>
        EmailSecretNames.Derive(Prefix, accountName, ClientSecretSuffix, taken);

    /// <summary>The variable of the webhook address of the account <paramref name="accountName"/>.</summary>
    /// <param name="accountName">The account's name, as the file holds it.</param>
    /// <param name="taken">The variables the other accounts of the file already name.</param>
    public static string WebhookVariableFor(string accountName, IEnumerable<string> taken) =>
        EmailSecretNames.Derive(Prefix, accountName, WebhookUrlSuffix, taken);
}
