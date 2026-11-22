using System.Reflection;

namespace Orkeon.Studio.Core.Tests.Configuration;

/// <summary>
/// The host's rewording of the binder's refusal, without the value (<c>BinderRefusal.WithoutValue</c>,
/// internal to Orkeon.Hosting, Orkeon #465). An oracle that binds a section itself meets the binder's
/// raw sentence; the host's start, the walk and <c>orkeon doctor</c> say this one.
/// </summary>
internal static class EngineBinderRefusal
{
    private static readonly MethodInfo Reword = typeof(Orkeon.Hosting.RunnerHost).Assembly
        .GetType("Orkeon.Hosting.BinderRefusal", throwOnError: true)!
        .GetMethod("WithoutValue", BindingFlags.NonPublic | BindingFlags.Static, [typeof(string)])!;

    /// <summary><paramref name="message"/> as the host says it.</summary>
    public static string WithoutValue(string message) => (string)Reword.Invoke(null, [message])!;
}
