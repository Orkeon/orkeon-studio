using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Orkeon.Infrastructure.LLMs.Decision;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Core.Tests.Configuration;

/// <summary>
/// The decision profiles of <c>Orkeon:Decision:Profiles</c> are checked while the engine's host is
/// composed, by a method internal to the engine (<c>DecisionProfileServiceCollectionExtensions.Validate</c>):
/// Studio restates its rules (<c>DecisionProfileRules</c>). This oracle runs the engine's own method,
/// reached by reflection, over the section bound as the engine binds it, and
/// <see cref="DecisionSection.ProfileProblems"/> over the same settings: the engine throws exactly
/// when Studio finds a problem, and its sentence is Studio's first. A rule the engine adds, drops or
/// rewords fails here.
/// </summary>
public sealed class DecisionProfileRulesOracleTests
{
    private const string Guard = """{ "Kind": "guard", "Risks": { "x": "y" } }""";

    public static TheoryData<string> Profiles => new()
    {
        "{}",
        $$"""{ "p": { "Target": "default", "Stages": [ {{Guard}} ] } }""",
        $$"""{ "auto": { "Target": "default", "Stages": [ {{Guard}} ] } }""",
        $$"""{ "AUTO": { "Stages": [ {{Guard}} ] } }""",
        $$"""{ "p": { "Stages": [ {{Guard}} ] } }""",
        $$"""{ "p": { "Target": " ", "Stages": [ {{Guard}} ] } }""",
        $$"""{ "p": { "Target": { "a": 1 }, "Stages": [ {{Guard}} ] } }""",
        """{ "p": { "Target": "default" } }""",
        """{ "p": { "Target": "default", "Stages": [] } }""",
        """{ "p": { "Target": "default", "Stages": {} } }""",
        """{ "p": null }""",
        """{ "p": "x" }""",
        """{ "p": { "Target": "default", "Stages": [ null ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "nope" } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": " GUARD ", "Risks": { "x": "y" } } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "guard" } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" }, "Phase": "later" } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" }, "Phase": " RESPONSE " } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" }, "Phase": "2" } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" }, "Phase": "3" } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" }, "Phase": "request,response" } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" }, "BlockThreshold": 1.01 } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" }, "BlockThreshold": 1, "WarnThreshold": 0 } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" }, "WarnThreshold": -0.25 } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" }, "WarnThreshold": "NaN" } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" }, "WarnThreshold": "Infinity" } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" }, "BlockThreshold": "x" } ] } }""",
        $$"""{ "p": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" }, "BlockThreshold": "x" }, {{Guard}} ] } }""",
        $$"""{ "p": { "Target": "default", "FailClosed": "x", "Stages": [ {{Guard}} ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "route" } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "route", "Routes": { "default": "anything", "strong": "the hard ones" }, "Fallback": "strong", "MinConfidence": 0.6 } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "route", "Routes": { "default": "anything" }, "MinConfidence": 3 } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "route", "Routes": { "P": "itself" } } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "route", "Routes": { "default": "anything" }, "Fallback": " p " } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "route", "Routes": { " ": "blank" } } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "verify" } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "verify", "EscalateTo": "default", "Steps": "all", "Criterion": "it answers" } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "verify", "EscalateTo": "default", "MinProbability": 1.5 } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "verify", "EscalateTo": "default", "Steps": "some" } ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "verify", "EscalateTo": "q" } ] }, "q": { "Target": "default", "Stages": [ { "Kind": "verify", "EscalateTo": "default" } ] } }""",
        $$"""{ "p": { "Target": "q", "Stages": [ {{Guard}} ] }, "q": { "Target": "P", "Stages": [ { "Kind": "nope" } ] } }""",
        $$"""{ "b": { "Target": "default" }, "a": { "Stages": [ {{Guard}} ] } }""",
        """{ "p": { "Target": "default", "Stages": [ { "Kind": "guard", "Phase": "later", "WarnThreshold": 4 }, { "Kind": "nope" } ] } }""",
        // Keys in another letter case, a list written as an object.
        $$"""{ "p": { "target": "default", "stages": { "0": {{Guard}} } } }""",
    };

    [Theory]
    [MemberData(nameof(Profiles))]
    public void The_profiles_are_refused_as_the_engine_refuses_them(string profiles)
    {
        var json = $$"""{ "Orkeon": { "Decision": { "Profiles": {{profiles}} } } }""";

        var engine = EngineRefusal(json);
        var studio = AppSettingsDocument.Parse(json).Decision.ProfileProblems();

        Assert.Equal(engine, studio.Select(problem => problem.Problem).FirstOrDefault());
        // Each problem names its key: a rule's sentence starts with it, and so does a single value written
        // where a section is expected; the binder's, which the engine says inside a profile since #454,
        // quotes it ("Failed to convert configuration value 'x' at '<key>'").
        Assert.All(studio, problem => Assert.True(
            (problem.Problem + ":").StartsWith(problem.Path + ":", StringComparison.Ordinal)
                || problem.Problem.StartsWith(problem.Path + " is a single value where ", StringComparison.Ordinal)
                || problem.Problem.Contains($" at '{problem.Path}'", StringComparison.Ordinal),
            $"{problem.Path}: {problem.Problem}"));
    }

    /// <summary>Every problem is said, the engine's first: what it says once the first is mended follows.</summary>
    [Fact]
    public void Every_problem_of_the_profiles_is_said_in_the_engines_order()
    {
        var document = AppSettingsDocument.Parse(
            """{ "Orkeon": { "Decision": { "Profiles": { "p": { "Stages": [ { "Kind": "guard", "Phase": "later", "WarnThreshold": 4 }, { "Kind": "nope" } ] }, "q": { "Target": "p" } } } } }""");

        Assert.Equal(
            [
                "Orkeon:Decision:Profiles:p:Target", "Orkeon:Decision:Profiles:p:Stages:0:Risks", "Orkeon:Decision:Profiles:p:Stages:0:Phase",
                "Orkeon:Decision:Profiles:p:Stages:0:WarnThreshold", "Orkeon:Decision:Profiles:p:Stages:1:Kind",
                "Orkeon:Decision:Profiles:q:Target", "Orkeon:Decision:Profiles:q:Stages",
            ],
            document.Decision.ProfileProblems().Select(problem => problem.Path));
    }

    /// <summary>The engine's own verdict: the section bound as its host binds it, then its <c>Validate</c>; null when it accepts.</summary>
    private static string? EngineRefusal(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var options = new DecisionProfilesOptions();
        configuration.GetSection(DecisionProfilesOptions.SectionName).Bind(options);
        // The section goes with the options, as the host passes it: the engine reads the raw entries
        // again for what the binder drops without a word (#454).
        var validate = typeof(DecisionProfileServiceCollectionExtensions)
            .GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static, [typeof(DecisionProfilesOptions), typeof(IConfiguration)])!;
        try
        {
            validate.Invoke(null, [options, configuration.GetSection(DecisionProfilesOptions.SectionName)]);
            return null;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException refusal)
        {
            return refusal.Message;
        }
    }
}
