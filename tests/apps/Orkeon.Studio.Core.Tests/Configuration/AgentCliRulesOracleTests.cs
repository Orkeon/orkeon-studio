using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Orkeon.Interop.AgentCli;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Validation;

namespace Orkeon.Studio.Core.Tests.Configuration;

/// <summary>
/// <see cref="AgentCliSection"/> restates what the agent-cli provider module refuses at start in
/// <c>Llm:AgentCli</c> (<c>AgentCliOptionsValidator.Refusals</c>, internal to Orkeon.Interop.AgentCli) and
/// binds the section onto a copy of <c>AgentCliOptions</c>. These tests run the engine's rules and
/// Studio's over the same settings: a sentence, an order or a type that moves fails here first.
/// </summary>
public sealed class AgentCliRulesOracleTests
{
    public static TheoryData<string> Sections() =>
    [
        """{ "Llm": { "AgentCli": {} } }""",
        """{ "Llm": { "AgentCli": { "Engine": "codex" } } }""",
        """{ "Llm": { "AgentCli": { "Engine": " Claude " } } }""",
        """{ "Llm": { "AgentCli": { "Engine": "" } } }""",
        """{ "Llm": { "AgentCli": { "Command": " " } } }""",
        """{ "Llm": { "AgentCli": { "Command": "C:\\tools\\claude.cmd" } } }""",
        """{ "Llm": { "AgentCli": { "Command": "claude.BAT" } } }""",
        """{ "Llm": { "AgentCli": { "Command": "/usr/local/bin/claude" } } }""",
        """{ "Llm": { "AgentCli": { "MaxProcesses": 0, "MaxToolResultCharacters": 999, "AutocompactTokens": -1 } } }""",
        """{ "Llm": { "AgentCli": { "MaxProcesses": 1, "MaxToolResultCharacters": 1000, "AutocompactTokens": 1 } } }""",
        """{ "Llm": { "AgentCli": { "Engine": "x", "Command": "a.cmd", "MaxProcesses": -3, "RequireLogin": false } } }""",
    ];

    [Theory]
    [MemberData(nameof(Sections))]
    public void The_refusals_are_the_modules_in_its_order(string json)
    {
        var engine = (IReadOnlyList<string>)EngineRefusals.Invoke(null, [Bind<AgentCliOptions>(json)])!;

        var studio = AppSettingsDocument.Parse(json).AgentCli.Problems();

        Assert.Equal(engine, studio.Select(problem => problem.Problem));
        Assert.All(studio, problem => Assert.StartsWith(problem.Path, problem.Problem, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "Llm": { "AgentCli": { "MaxProcesses": "many" } } }""")]
    [InlineData("""{ "Llm": { "AgentCli": { "AutocompactTokens": "sk-ant-api03-secret" } } }""")]
    [InlineData("""{ "Llm": { "AgentCli": { "RequireLogin": "maybe" } } }""")]
    [InlineData("""{ "Llm": { "AgentCli": { "MaxToolResultCharacters": 1e3 } } }""")]
    public void A_value_the_binder_cannot_convert_is_refused_first_with_the_engines_sentence(string json)
    {
        var engine = Assert.Throws<InvalidOperationException>(() => Bind<AgentCliOptions>(json));

        var studio = AppSettingsDocument.Parse(json).AgentCli.Problems();

        Assert.Equal(EngineBinderRefusal.WithoutValue(engine), studio[0].Problem);
        Assert.DoesNotContain("sk-ant-api03-secret", studio[0].Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shape_is_the_engines_own()
    {
        var engine = typeof(AgentCliOptions).GetProperties().ToDictionary(property => property.Name, property => property.PropertyType);
        var studio = typeof(AgentCliSettings).GetProperties().ToDictionary(property => property.Name, property => property.PropertyType);

        Assert.Equal(engine.OrderBy(pair => pair.Key), studio.OrderBy(pair => pair.Key));
        Assert.Equal(AgentCliOptions.SectionName, AgentCliSection.SectionPath);
        Assert.Equal(AgentCliEngines.Claude, AgentCliSection.ClaudeEngine);

        // The defaults, which the rules judge when a key is left out.
        var defaults = new AgentCliOptions();
        var shape = new AgentCliSettings();
        foreach (var property in typeof(AgentCliOptions).GetProperties())
            Assert.Equal(property.GetValue(defaults), typeof(AgentCliSettings).GetProperty(property.Name)!.GetValue(shape));
    }

    [Fact]
    public void A_key_that_is_no_setting_of_the_section_is_refused_and_a_refusal_is_an_error_of_the_file()
    {
        var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": "m", "AgentCli": { "Comand": "claude", "MaxProcesses": 0 } } }""");

        Assert.Contains(document.AgentCli.Problems(), problem => problem.Path == "Llm:AgentCli:Comand");
        var errors = new AppSettingsValidator().Validate(document).Where(message => message.Severity == ValidationSeverity.Error).ToList();
        Assert.Contains(errors, message => message.Path == "Llm:AgentCli:MaxProcesses");
        Assert.Contains(errors, message => message.Path == "Llm:AgentCli:Comand");
    }

    [Fact]
    public void A_file_without_the_section_has_no_finding()
    {
        Assert.Empty(AppSettingsDocument.Parse("""{ "Llm": { "Model": "m" } }""").AgentCli.Problems());
    }

    private static readonly MethodInfo EngineRefusals = typeof(AgentCliOptions).Assembly
        .GetType("Orkeon.Interop.AgentCli.AgentCliOptionsValidator", throwOnError: true)!
        .GetMethod("Refusals", BindingFlags.NonPublic | BindingFlags.Static, [typeof(AgentCliOptions)])!;

    private static T Bind<T>(string json)
        where T : new()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var options = new T();
        configuration.GetSection(AgentCliOptions.SectionName).Bind(options);
        return options;
    }
}
