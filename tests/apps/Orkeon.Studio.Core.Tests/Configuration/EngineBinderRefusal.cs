using System.Reflection;

namespace Orkeon.Studio.Core.Tests.Configuration;

/// <summary>
/// The engine's rewording of the binder's refusal, without the value (<c>BinderRefusal</c>, internal to
/// Orkeon.Constants.Configuration, Orkeon #465 and #469): one wording for the host's start, the walk,
/// <c>orkeon doctor</c> and the e-mail and drive binders. An oracle that binds a section itself meets
/// the binder's raw sentence; the engine says this one.
/// </summary>
internal static class EngineBinderRefusal
{
    private static readonly Type Refusal = typeof(Orkeon.Constants.Configuration.ConfigurationKeys).Assembly
        .GetType("Orkeon.Constants.Configuration.BinderRefusal", throwOnError: true)!;

    private static readonly MethodInfo OfMessage = Refusal
        .GetMethod("WithoutValue", BindingFlags.NonPublic | BindingFlags.Static, [typeof(string), typeof(string)])!;

    private static readonly MethodInfo OfException = Refusal
        .GetMethod("WithoutValue", BindingFlags.NonPublic | BindingFlags.Static, [typeof(Exception), typeof(string)])!;

    private static readonly MethodInfo Quotes = Refusal
        .GetMethod("QuotesAValue", BindingFlags.NonPublic | BindingFlags.Static, [typeof(Exception)])!;

    /// <summary><paramref name="message"/> as the engine says it, naming <paramref name="section"/> when it withholds it.</summary>
    public static string WithoutValue(string message, string? section = null) =>
        (string)OfMessage.Invoke(null, [message, section])!;

    /// <summary><paramref name="exception"/>'s refusal as the engine says it, naming <paramref name="section"/> when it withholds it.</summary>
    public static string WithoutValue(Exception exception, string? section = null) =>
        (string)OfException.Invoke(null, [exception, section])!;

    /// <summary>Whether the engine judges that <paramref name="exception"/>'s chain may quote a value.</summary>
    public static bool QuotesAValue(Exception exception) => (bool)Quotes.Invoke(null, [exception])!;
}
