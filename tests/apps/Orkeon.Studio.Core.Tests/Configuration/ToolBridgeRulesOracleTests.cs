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
        // What the binder makes of the JSON, not a second reading of it: keys in another letter case,
        // a null, a list written as an object, a null entry, a number in hexadecimal.
        """{ "address": "http://bridge.example:7410/", "allow": ["*"] }""",
        """{ "TokenEnvVar": null }""",
        """{ "Address": null, "HostHeader": null }""",
        """{ "Address": "https://x/", "Allow": { "0": "a*" } }""",
        """{ "Allow": [null] }""",
        """{ "Allow": "*" }""",
        """{ "MaxFileBytes": "0x10" }""",
        """{ "ConnectTimeout": "30" }""",
        // An empty list of names is no name; a null and an empty object are keys without a value.
        """{ "Allow": [], "Deny": [] }""",
        """{ "ConnectTimeout": null, "MaxFileBytes": {} }""",
        """{ "Allow": [null, "a*"], "Deny": { "1": "b*", "0": "c*" } }""",
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
        """{ "tokenEnvVar": null }""",
        """{ "Prefix": null }""",
        """{ "MaxSessions": "0x10" }""",
        """{ "maxSessions": 0, "deny": { "a": "b*" } }""",
        """{ "Allow": [null] }""",
        """{ "Allow": [], "Deny": [] }""",
        """{ "StopTimeout": null, "MaxSessions": {} }""",
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

    /// <summary>
    /// A value the binder cannot convert stops the engine's start before its rules, on the first such
    /// value: Studio says the same sentence first. A boolean reads <c>True</c> or <c>False</c> and an
    /// empty list an empty text, as the JSON provider hands them to the binder.
    /// </summary>
    [Theory]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeClientOptions", """{ "Orkeon": { "Tools": { "Bridge": { "ConnectTimeout": "soon" } } } }""")]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeClientOptions", """{ "Orkeon": { "Tools": { "Bridge": { "MaxFileBytes": "big" } } } }""")]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeClientOptions", """{ "Orkeon": { "Tools": { "Bridge": { "ConnectTimeout": " " } } } }""")]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeServerOptions", """{ "Orkeon": { "Bridge": { "MaxSessions": "many" } } }""")]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeServerOptions", """{ "Orkeon": { "Bridge": { "MaxSessions": "" } } }""")]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeServerOptions", """{ "Orkeon": { "Bridge": { "HeartbeatWindow": "a while" } } }""")]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeServerOptions", """{ "Orkeon": { "Bridge": { "MaxRequestBytes": "big", "MaxSessions": "many" } } }""")]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeServerOptions", """{ "Orkeon": { "Bridge": { "MaxSessions": 1e3 } } }""")]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeClientOptions", """{ "Orkeon": { "Tools": { "Bridge": { "MaxFileBytes": true } } } }""")]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeClientOptions", """{ "Orkeon": { "Tools": { "Bridge": { "ConnectTimeout": [] } } } }""")]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeServerOptions", """{ "Orkeon": { "Bridge": { "MaxSessions": false } } }""")]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeServerOptions", """{ "Orkeon": { "Bridge": { "StopTimeout": [] } } }""")]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeServerOptions", """{ "Orkeon": { "Bridge": { "MaxRequestBytes": [], "Prefix": "not a url" } } }""")]
    public void A_value_the_binder_cannot_convert_is_refused_first_with_the_engines_sentence(string optionsType, string json)
    {
        var engine = Assert.Throws<InvalidOperationException>(() => EngineProblems(optionsType, json));

        Assert.Equal(EngineBinderRefusal.WithoutValue(engine), StudioProblems(optionsType, json)[0]);
    }

    /// <summary>
    /// The binder's refusal hides nothing of what follows it: the values that did convert are still
    /// judged by the rules, the other values that do not convert are said, and so are the keys that are
    /// no setting. A form that writes an address beside a timeout the file already had wrong is told.
    /// </summary>
    [Fact]
    public void After_a_value_the_binder_cannot_convert_the_rules_and_the_keys_are_still_judged()
    {
        var caller = AppSettingsDocument.Parse(
            """{ "Orkeon": { "Tools": { "Bridge": { "ConnectTimeout": "soon", "Address": "not a url", "HostHeader": "a/b", "Allow": ["a*"], "Adress": "x" } } } }""");

        Assert.Equal(
            [
                "Orkeon:Tools:Bridge:ConnectTimeout", "Orkeon:Tools:Bridge:Address", "Orkeon:Tools:Bridge:HostHeader",
                "Orkeon:Tools:Bridge:Allow", "Orkeon:Tools:Bridge:Adress",
            ],
            caller.ToolBridge.Problems().Select(problem => problem.Path));

        var server = AppSettingsDocument.Parse(
            """{ "Orkeon": { "Bridge": { "MaxSessions": "many", "MaxRequestBytes": "big", "Prefix": "not a url", "Deny": ["b*"], "Port": 1 } } }""");

        Assert.Equal(
            ["Orkeon:Bridge:MaxRequestBytes", "Orkeon:Bridge:MaxSessions", "Orkeon:Bridge:Prefix", "Orkeon:Bridge:Deny", "Orkeon:Bridge:Port"],
            server.BridgeServer.Problems().Select(problem => problem.Path));
    }

    public static TheoryData<string, string> KeysThatAreNoSetting => new()
    {
        { "Orkeon.Hosting.ToolBridge.ToolBridgeClientOptions", """{ "Adress": "https://desk.example:7410/" }""" },
        { "Orkeon.Hosting.ToolBridge.ToolBridgeClientOptions", """{ "IsEnabled": true, "isDockerHostOverHttp": true, "Enabled": true }""" },
        { "Orkeon.Hosting.ToolBridge.ToolBridgeClientOptions", """{ "Address": { "Host": "x" }, "ConnectTimeout": { "Seconds": 3 } }""" },
        { "Orkeon.Hosting.ToolBridge.ToolBridgeClientOptions", """{ "Allow": [["a*"], "b*", { "a": 1 }], "Deny": { "x": { "y": 1 }, "z": "c*" } }""" },
        { "Orkeon.Hosting.ToolBridge.ToolBridgeServerOptions", """{ "Port": 7410, "allow": ["*"] }""" },
        { "Orkeon.Hosting.ToolBridge.ToolBridgeServerOptions", """{ "MaxSessions": { "a": 1 }, "Prefix": ["http://127.0.0.1:7410/"] }""" },
        { "Orkeon.Hosting.ToolBridge.ToolBridgeServerOptions", """{ "Allow": [["a*"]], "Deny": [{ "a": 1 }] }""" },
        { "Orkeon.Hosting.ToolBridge.ToolBridgeServerOptions", """{ "Deny": { "x": { "y": 1 } }, "IsEnabled": true }""" },
    };

    /// <summary>
    /// The keys the engine's start refuses under a section, at any depth, are those its own walk of the
    /// section's shape refuses (<c>SettingsValidation.Walk</c> over <c>SettingsShape</c>): Studio says
    /// each one at the same place, in a sentence the engine's starts with (the engine may add a closest
    /// name and where the keys are listed).
    /// </summary>
    [Theory]
    [MemberData(nameof(KeysThatAreNoSetting))]
    public void The_keys_that_are_no_setting_are_those_the_engines_walk_refuses(string optionsType, string section)
    {
        var json = optionsType.EndsWith("ClientOptions", StringComparison.Ordinal)
            ? $$"""{ "Orkeon": { "Tools": { "Bridge": {{section}} } } }"""
            : $$"""{ "Orkeon": { "Bridge": {{section}} } }""";

        var engine = EngineUnknownKeys(optionsType, json);
        var studio = StudioProblems(optionsType, json).Where(problem => problem.Contains(" is not a setting: ", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(engine);
        Assert.Equal(engine.Count, studio.Count);
        Assert.All(engine.Zip(studio), pair => Assert.StartsWith(pair.Second, pair.First, StringComparison.Ordinal));
    }

    /// <summary>
    /// Studio binds each section onto a class of its own, the engine's being internal: the mirror has
    /// the engine's public properties, in its order, with its types, its setters and its defaults, so
    /// the binder reads a file the same way on both sides and the keys a section may carry are the same.
    /// </summary>
    [Theory]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeClientOptions", typeof(ToolBridgeSection))]
    [InlineData("Orkeon.Hosting.ToolBridge.ToolBridgeServerOptions", typeof(BridgeServerSection))]
    public void The_mirror_of_a_section_has_the_shape_and_the_defaults_of_the_engines_options(string optionsType, Type section)
    {
        const BindingFlags Properties = BindingFlags.Public | BindingFlags.Instance;
        var engine = Hosting.GetType(optionsType, throwOnError: true)!;
        var mirror = section.GetNestedType("Values", BindingFlags.NonPublic)!;

        Assert.Equal(
            engine.GetProperties(Properties).Select(property => (property.Name, property.PropertyType, property.CanWrite)),
            mirror.GetProperties(Properties).Select(property => (property.Name, property.PropertyType, property.CanWrite)));

        var engineDefaults = Activator.CreateInstance(engine, nonPublic: true)!;
        var mirrorDefaults = Activator.CreateInstance(mirror, nonPublic: true)!;
        Assert.All(engine.GetProperties(Properties), property => Assert.Equal(
            Comparable(property.GetValue(engineDefaults)),
            Comparable(mirror.GetProperty(property.Name, Properties)!.GetValue(mirrorDefaults))));
    }

    // A collection by its sequence, anything else as it is.
    private static object? Comparable(object? value) =>
        value is IEnumerable<string> items ? string.Join("\u0001", items) : value;

    private static List<string> StudioProblems(string optionsType, string json)
    {
        var document = AppSettingsDocument.Parse(json);
        var problems = optionsType.EndsWith("ClientOptions", StringComparison.Ordinal)
            ? document.ToolBridge.Problems()
            : document.BridgeServer.Problems();
        return [.. problems.Select(problem => problem.Problem)];
    }

    /// <summary>The engine's own walk of the section against the shape of its options class.</summary>
    private static List<string> EngineUnknownKeys(string optionsType, string json)
    {
        var type = Hosting.GetType(optionsType, throwOnError: true)!;
        var section = (string)type.GetField("SectionName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var shape = Hosting.GetType("Orkeon.Hosting.SettingsShape", throwOnError: true)!
            .GetMethod("Of", BindingFlags.Public | BindingFlags.Static, [typeof(Type)])!
            .Invoke(null, [type])!;
        var walk = Hosting.GetType("Orkeon.Hosting.SettingsValidation", throwOnError: true)!
            .GetMethod("Walk", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        // The walk yields each refusal with its path and its kind; the refusal alone is compared.
        var found = (System.Collections.IEnumerable)walk.Invoke(null, [configuration.GetSection(section), shape, false])!;
        return [.. found.Cast<object>().Select(item => (string)item.GetType().GetField("Item2")!.GetValue(item)!)];
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

    /// <summary>
    /// The engine's own verdict, as its start gives it: both sections registered as its hosts register them
    /// (<c>ToolBridgeStartup.Register</c>), then <c>SettingsValidation.Refusals</c>, which binds them, runs
    /// their <c>Problems()</c> and merges the value refusals of its walk (Orkeon #450, #454), each said once.
    /// What it says of the section of <paramref name="optionsType"/> is kept, but the keys that are no
    /// setting, compared apart (<see cref="The_keys_that_are_no_setting_are_those_the_engines_walk_refuses"/>).
    /// A value the binder cannot convert throws, as the options class bound alone throws it.
    /// </summary>
    private static List<string> EngineProblems(string optionsType, string json)
    {
        // The binder's refusal, which the start rewords without the value: thrown here as the binder throws it.
        _ = EngineRules(optionsType, json);
        var type = Hosting.GetType(optionsType, throwOnError: true)!;
        var section = (string)type.GetField("SectionName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IConfiguration>(services, configuration);
        Hosting.GetType("Orkeon.Hosting.ToolBridge.ToolBridgeStartup", throwOnError: true)!
            .GetMethod("Register", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [services, configuration]);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var refusals = (IEnumerable<string>)Hosting.GetType("Orkeon.Hosting.SettingsValidation", throwOnError: true)!
            .GetMethod("Refusals", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(IServiceProvider)])!
            .Invoke(null, [provider])!;
        var unknownKeys = EngineWalk(optionsType, json).Where(found => found.Kind == "Key").Select(found => found.Refusal).ToHashSet(StringComparer.Ordinal);
        return [.. refusals.Where(refusal => refusal.StartsWith(section + ":", StringComparison.OrdinalIgnoreCase) && !unknownKeys.Contains(refusal))];
    }

    /// <summary>The engine's walk of the section: each finding's path, refusal and kind.</summary>
    private static List<(string Path, string Refusal, string Kind)> EngineWalk(string optionsType, string json)
    {
        var type = Hosting.GetType(optionsType, throwOnError: true)!;
        var section = (string)type.GetField("SectionName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var shape = Hosting.GetType("Orkeon.Hosting.SettingsShape", throwOnError: true)!
            .GetMethod("Of", BindingFlags.Public | BindingFlags.Static, [typeof(Type)])!
            .Invoke(null, [type])!;
        var walk = Hosting.GetType("Orkeon.Hosting.SettingsValidation", throwOnError: true)!
            .GetMethod("Walk", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var found = (System.Collections.IEnumerable)walk.Invoke(null, [configuration.GetSection(section), shape, false])!;
        return [.. found.Cast<object>().Select(item => (
            (string)item.GetType().GetField("Item1")!.GetValue(item)!,
            (string)item.GetType().GetField("Item2")!.GetValue(item)!,
            item.GetType().GetField("Item3")!.GetValue(item)!.ToString()!))];
    }

    /// <summary>The engine's rules alone: its options class bound as its host binds it, then its <c>Problems()</c>.</summary>
    private static List<string> EngineRules(string optionsType, string json)
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
