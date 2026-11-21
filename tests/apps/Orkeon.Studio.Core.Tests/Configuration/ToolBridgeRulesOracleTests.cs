using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Core.Tests.Configuration;

/// <summary>
/// The tool bridge's sections are validated at start by options classes internal to
/// <c>Orkeon.Hosting</c>, which Studio does not reference: Studio restates their rules
/// (<c>ToolBridgeRules</c>). This oracle runs the engine's own classes, reached by reflection in the
/// <c>Orkeon.Hosting</c> assembly the tests carry, and Studio's sections over the same settings, and
/// asks for the same sentences in the same order. A rule the engine adds, drops or rewords fails here.
/// </summary>
public sealed class ToolBridgeRulesOracleTests
{
    private static readonly Assembly Hosting = typeof(Orkeon.Hosting.RunnerHost).Assembly;

    public static TheoryData<string> CallerSettings => new()
    {
        "{}",
        """{ "Address": "https://bridge.example:7410/", "Allow": ["*"] }""",
        """{ "Address": "http://127.0.0.1:7410/", "Allow": ["browser_open"], "Deny": ["shell_command"] }""",
        """{ "Address": "http://localhost:7410/" }""",
        """{ "Address": "http://host.docker.internal:7410/", "Allow": ["*"] }""",
        """{ "Address": "http://bridge.example:7410/" }""",
        """{ "Address": "https://bridge.example:7410" }""",
        """{ "Address": "https://user:secret@bridge.example:7410/" }""",
        """{ "Address": "ftp://bridge.example/" }""",
        """{ "Address": "bridge.example" }""",
        """{ "HostHeader": "127.0.0.1:7410" }""",
        """{ "HostHeader": "http://127.0.0.1:7410/" }""",
        """{ "HostHeader": "two words" }""",
        """{ "TokenEnvVar": "" }""",
        """{ "TokenEnvVar": "MY_BRIDGE_TOKEN" }""",
        """{ "ConnectTimeout": "00:00:00" }""",
        """{ "ConnectTimeout": "00:01:00" }""",
        """{ "MaxFileBytes": 0 }""",
        """{ "MaxResponseBytes": -1 }""",
        """{ "MaxResponseBytes": 4294967296 }""",
        """{ "Allow": ["browser_*"], "Deny": ["", "*"] }""",
        """{ "Address": "http://bridge.example:7410", "HostHeader": "a/b", "ConnectTimeout": "-00:00:01", "MaxFileBytes": -5, "Allow": ["a*"] }""",
    };

    public static TheoryData<string> ServerSettings => new()
    {
        "{}",
        """{ "Allow": ["*"], "Deny": ["shell_command"] }""",
        """{ "Prefix": "https://0.0.0.0:7410/" }""",
        """{ "Prefix": "http://0.0.0.0:7410/" }""",
        """{ "Prefix": "http://localhost:7410/" }""",
        """{ "Prefix": "http://host.docker.internal:7410/" }""",
        """{ "Prefix": "http://127.0.0.1:7410" }""",
        """{ "Prefix": "" }""",
        """{ "TokenEnvVar": " " }""",
        """{ "MaxSessions": 0, "MaxConcurrentCalls": 1025, "MaxCallsPerSession": 1, "MaxRunsPerSession": 1024 }""",
        """{ "RequestReadTimeout": "00:00:00.5", "StopTimeout": "00:11:00" }""",
        """{ "StopTimeout": "00:00:00", "RequestReadTimeout": "00:10:00" }""",
        """{ "HeartbeatWindow": "00:00:00.999" }""",
        """{ "HeartbeatWindow": "00:10:01" }""",
        """{ "MaxRequestBytes": 0, "MaxFileBytes": 0 }""",
        """{ "Allow": ["*", "a*b", ""] }""",
    };

    [Theory]
    [MemberData(nameof(CallerSettings))]
    public void The_callers_section_is_refused_as_the_engine_refuses_it(string section)
    {
        var json = $$"""{ "Orkeon": { "Tools": { "Bridge": {{section}} } } }""";

        Assert.Equal(
            EngineProblems("Orkeon.Hosting.ToolBridge.ToolBridgeClientOptions", json),
            AppSettingsDocument.Parse(json).ToolBridge.Problems().Select(problem => problem.Problem));
    }

    [Theory]
    [MemberData(nameof(ServerSettings))]
    public void The_serving_section_is_refused_as_the_engine_refuses_it(string section)
    {
        var json = $$"""{ "Orkeon": { "Bridge": {{section}} } }""";

        Assert.Equal(
            EngineProblems("Orkeon.Hosting.ToolBridge.ToolBridgeServerOptions", json),
            AppSettingsDocument.Parse(json).BridgeServer.Problems().Select(problem => problem.Problem));
    }

    /// <summary>A value the binder cannot convert stops the engine's start before its rules: Studio says so too.</summary>
    [Theory]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeClientOptions", """{ "Orkeon": { "Tools": { "Bridge": { "ConnectTimeout": "soon" } } } }""")]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeClientOptions", """{ "Orkeon": { "Tools": { "Bridge": { "MaxFileBytes": "big" } } } }""")]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeServerOptions", """{ "Orkeon": { "Bridge": { "MaxSessions": "many" } } }""")]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeServerOptions", """{ "Orkeon": { "Bridge": { "HeartbeatWindow": "a while" } } }""")]
    public void A_value_the_binder_cannot_convert_is_refused_on_both_sides(string optionsType, string json)
    {
        Assert.Throws<InvalidOperationException>(() => EngineProblems(optionsType, json));

        var document = AppSettingsDocument.Parse(json);
        var problems = optionsType.EndsWith("ClientOptions", StringComparison.Ordinal)
            ? document.ToolBridge.Problems()
            : document.BridgeServer.Problems();
        Assert.StartsWith("Failed to convert configuration value", Assert.Single(problems).Problem, StringComparison.Ordinal);
    }

    /// <summary>The token is read as the engine reads it: trimmed, set, sixteen characters or more.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short-token")]
    [InlineData("  exactly-16-chars  ")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void The_token_is_judged_as_the_engine_judges_it(string? value)
    {
        var protocol = Hosting.GetType("Orkeon.Hosting.ToolBridge.ToolBridgeProtocol", throwOnError: true)!;
        var readToken = protocol.GetMethod("ReadToken", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] arguments = [value, "ORKEON_BRIDGE_TOKEN", null];
        readToken.Invoke(null, arguments);

        Assert.Equal((string?)arguments[2], ToolBridgeRules.TokenProblem(value, "ORKEON_BRIDGE_TOKEN"));
        Assert.Equal(
            ToolBridgeRules.MinTokenLength,
            (int)protocol.GetField("MinTokenLength", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!);
        Assert.Equal(
            ToolBridgeSection.DefaultTokenEnvVar,
            (string)protocol.GetField("DefaultTokenEnvVar", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!);
    }

    /// <summary>The engine's own verdict: its options class bound as its host binds it, then its <c>Problems()</c>.</summary>
    private static List<string> EngineProblems(string optionsType, string json)
    {
        var type = Hosting.GetType(optionsType, throwOnError: true)!;
        var options = Activator.CreateInstance(type, nonPublic: true)!;
        var section = (string)type.GetField("SectionName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        configuration.GetSection(section).Bind(options);
        var problems = type.GetMethod("Problems", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            return [.. (IEnumerable<string>)problems.Invoke(options, null)!];
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
