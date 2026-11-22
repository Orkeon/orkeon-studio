using System.Reflection;
using Microsoft.Extensions.Configuration;
using Orkeon.Infrastructure.LLMs.Decision;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Core.Tests.Configuration;

/// <summary>
/// The binder's refusal of a value it cannot convert is said without that value (Orkeon #465), in the
/// engine's own words in every case (#469): the sentence read from its end, a sentence that cannot be
/// read back or a converter's message withheld behind a fixed sentence, an account's section named.
/// </summary>
public sealed class BinderRefusalWithoutValueTests
{
    private const string Secret = "sk-ant-api03-studio-oracle-secret";

    [Theory]
    [InlineData("Failed to convert configuration value 'x' at 'Orkeon:Compaction:TriggerShare' to type 'System.Double'.")]
    [InlineData("Failed to convert configuration value '' at 'Orkeon:Compaction:TriggerShare' to type 'System.Double'.")]
    [InlineData("Failed to convert configuration value 'it's' at 'Orkeon:Bridge:MaxSessions' to type 'System.Int32'.")]
    [InlineData("Failed to convert configuration value 'a' at 'b' at 'Orkeon:Bridge:MaxSessions' to type 'System.Int32'.")]
    [InlineData("Failed to convert configuration value 'two\nlines' at 'Orkeon:Bridge:MaxSessions' to type 'System.Int32'.")]
    [InlineData("Failed to convert configuration value 'x' at '' to type 'System.Int32'.")]
    [InlineData("Failed to convert configuration value 'x' at 'Orkeon:Bridge:MaxSessions' to type ''.")]
    [InlineData("Failed to convert configuration value 'x' at 'Orkeon:Bridge:MaxSessions'")]
    [InlineData("Error_FailedBinding, x, Orkeon:Bridge:MaxSessions, System.Int32")]
    [InlineData("x is not a valid value for TimeSpan.")]
    [InlineData("Requested value 'x' was not found.")]
    [InlineData("The input string 'x' was not in a correct format.")]
    [InlineData("String 'x' was not recognized as a valid Boolean.")]
    [InlineData("Some other refusal, said as it is.")]
    public void A_message_is_said_in_the_engines_words(string message)
    {
        Assert.Equal(EngineBinderRefusal.WithoutValue(message), Studio.WithoutValue(message));
        Assert.Equal(
            EngineBinderRefusal.WithoutValue(message, "Orkeon:Tools:Email:Accounts:work"),
            Studio.WithoutValue(message, "Orkeon:Tools:Email:Accounts:work"));
    }

    /// <summary>The exception's chain, read as the engine reads it: the sentence anywhere in it, a conversion exception withheld.</summary>
    public static TheoryData<Exception> Chains() =>
    [
        new InvalidOperationException("Some other refusal, said as it is."),
        new InvalidOperationException("Outer.", new FormatException("whatever")),
        new InvalidOperationException("Outer.", new OverflowException("whatever")),
        new InvalidOperationException("Outer.", new InvalidCastException("whatever")),
        new InvalidOperationException("Outer.", new UriFormatException("whatever")),
        new InvalidOperationException(
            "Outer.", new InvalidOperationException("Failed to convert configuration value 'x' at 'Orkeon:Bridge:MaxSessions' to type 'System.Int32'.")),
        new InvalidOperationException("Outer.", new InvalidOperationException("Requested value 'x' was not found.")),
    ];

    [Theory]
    [MemberData(nameof(Chains))]
    public void An_exceptions_chain_is_said_in_the_engines_words(Exception exception)
    {
        Assert.Equal(EngineBinderRefusal.QuotesAValue(exception), Studio.QuotesAValue(exception));
        Assert.Equal(EngineBinderRefusal.WithoutValue(exception), Studio.WithoutValue(exception));
        Assert.Equal(
            EngineBinderRefusal.WithoutValue(exception, "Orkeon:Tools:Drive:Accounts:work"),
            Studio.WithoutValue(exception, "Orkeon:Tools:Drive:Accounts:work"));
    }

    [Theory]
    [InlineData("Orkeon:Decision:Profiles:l'equipe:Stages:0:MinConfidence")]
    [InlineData("Orkeon:Decision:Profiles:'quoted':Stages:0:MinConfidence")]
    [InlineData("Orkeon:Decision:Profiles:a' at 'b:Stages:0:MinConfidence")]
    public void A_path_holding_an_apostrophe_never_lets_the_value_through(string path)
    {
        var refusal = BinderRefusal(path, Secret, new Probe());
        Assert.Contains(Secret, refusal.Message, StringComparison.Ordinal);

        var studio = Studio.WithoutValue(refusal);

        Assert.DoesNotContain(Secret, studio, StringComparison.Ordinal);
        Assert.Equal(EngineBinderRefusal.WithoutValue(refusal), studio);
        Assert.EndsWith("' to type 'System.Double'.", studio, StringComparison.Ordinal);
    }

    /// <summary>
    /// The e-mail and drive binders word an account's refusal through the engine's rewording, naming the
    /// account's section (#469): Studio's rewording on that section says the same.
    /// </summary>
    [Theory]
    [InlineData("work", "Port")]
    [InlineData("l'equipe", "Port")]
    [InlineData("a' at 'b", "TimeoutSeconds")]
    public void An_accounts_refusal_is_said_as_the_account_binders_say_it(string account, string key)
    {
        var email = BinderRefusal($"Orkeon:Tools:Email:Accounts:{account}:Incoming:{key}", Secret, new WholeNumberProbe());
        var drive = BinderRefusal($"Orkeon:Tools:Drive:Accounts:{account}:{key}", Secret, new WholeNumberProbe());

        var emailSaid = Orkeon.Tools.Email.Configuration.EmailOptionsBinder.Refusal(account, email);
        var driveSaid = Orkeon.Tools.Drive.Configuration.DriveOptionsBinder.Refusal(account, drive);

        Assert.Equal(emailSaid, Studio.WithoutValue(email, $"Orkeon:Tools:Email:Accounts:{account}"));
        Assert.Equal(driveSaid, Studio.WithoutValue(drive, $"Orkeon:Tools:Drive:Accounts:{account}"));
        Assert.DoesNotContain(Secret, emailSaid, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, driveSaid, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_holding_the_words_of_the_sentence_never_gets_through()
    {
        var refusal = BinderRefusal("Orkeon:Decision:Profiles:l'equipe:Stages:0:MinConfidence", Secret + "' at 'x' to type 'y'.", new Probe());

        Assert.DoesNotContain(Secret, Studio.WithoutValue(refusal), StringComparison.Ordinal);
        Assert.Equal(EngineBinderRefusal.WithoutValue(refusal), Studio.WithoutValue(refusal));
    }

    [Fact]
    public void A_secret_under_a_profile_named_with_an_apostrophe_is_filed_at_its_key_and_never_echoed()
    {
        var document = AppSettingsDocument.Parse($$"""
            { "Orkeon": { "Decision": { "Profiles": {
              "l'equipe": { "Target": "default", "Stages": [ { "Kind": "route", "Routes": { "fast": "x" }, "MinConfidence": "{{Secret}}", "Fallback": "slow" } ] },
              "a' at 'b": { "Target": "default", "Stages": [ { "Kind": "verify", "EscalateTo": "x", "MinProbability": "{{Secret}}" } ] }
            } } } }
            """);

        var problems = document.Decision.ProfileProblems();

        Assert.NotEmpty(problems);
        Assert.All(problems, problem => Assert.DoesNotContain(Secret, problem.Problem, StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Path == "Orkeon:Decision:Profiles:l'equipe:Stages:0:MinConfidence"
            && problem.Problem == "Failed to convert the configuration value at 'Orkeon:Decision:Profiles:l'equipe:Stages:0:MinConfidence' to type 'System.Double'.");
        Assert.Contains(problems, problem => problem.Path == "Orkeon:Decision:Profiles:a' at 'b:Stages:0:MinProbability");
        // Nothing is filed under the section: each refusal is read at its key.
        Assert.DoesNotContain(problems, problem => problem.Path == DecisionProfilesOptions.SectionName);
    }

    /// <summary>Studio's rewording, internal to its Core: reached through reflection, as the engine's is.</summary>
    private static class Studio
    {
        private static readonly Type Binding = typeof(AppSettingsDocument).Assembly
            .GetType("Orkeon.Studio.Core.Configuration.SettingsBinding", throwOnError: true)!;

        public static string WithoutValue(string message, string? section = null) =>
            (string)Binding.GetMethod("WithoutValue", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(string)])!
                .Invoke(null, [message, section])!;

        public static string WithoutValue(Exception exception, string? section = null) =>
            (string)Binding.GetMethod("WithoutValue", BindingFlags.Public | BindingFlags.Static, [typeof(Exception), typeof(string)])!
                .Invoke(null, [exception, section])!;

        public static bool QuotesAValue(Exception exception) =>
            (bool)Binding.GetMethod("QuotesAValue", BindingFlags.Public | BindingFlags.Static, [typeof(Exception)])!
                .Invoke(null, [exception])!;
    }

    /// <summary>The configuration binder's own refusal of <paramref name="value"/> at <paramref name="path"/>, bound onto <paramref name="probe"/>.</summary>
    private static InvalidOperationException BinderRefusal(string path, string value, object probe)
    {
        var last = path.LastIndexOf(':');
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [path] = value })
            .Build();
        return Assert.Throws<InvalidOperationException>(() => configuration.GetSection(path[..last]).Bind(probe));
    }

    private sealed class Probe
    {
        public double MinConfidence { get; set; }

        public double MinProbability { get; set; }
    }

    // The whole-number settings of an account, as the e-mail and drive binders bind them.
    private sealed class WholeNumberProbe
    {
        public int Port { get; set; }

        public int TimeoutSeconds { get; set; }

        public int MaxUploadMegabytes { get; set; }
    }
}
