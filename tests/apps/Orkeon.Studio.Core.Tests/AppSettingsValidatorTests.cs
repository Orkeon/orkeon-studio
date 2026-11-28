using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Orkeon.Constants.Configuration;
using Orkeon.Infrastructure.LLMs.Profiles;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Tests.Doubles;
using Orkeon.Studio.Core.Validation;

namespace Orkeon.Studio.Core.Tests;

/// <summary>
/// Pre-save validation, including the WIN-01 onboarding trap: no <c>Llm</c> section
/// means the runtime degrades silently to the echo provider.
/// </summary>
public sealed class AppSettingsValidatorTests
{
    /// <summary>The integer fields among those the validator checks as numbers — each one a screen writes with SetInt32.</summary>
    private static readonly string[] IntegerFields =
    [
        "Llm:MaxTokens",
        "Llm:TimeoutSeconds",
        "Llm:StreamIdleSeconds",
        "RateLimiting:MaxConcurrentRequests",
        "RateLimiting:GlobalRequestsPerMinute",
        "RateLimiting:ProviderRequestsPerMinute",
        "RateLimiting:AgentRequestsPerMinute",
        "RateLimiting:QueueLimit",
        "LlmLogging:MaxBodyLengthChars",
        "Orkeon:Rag:Corrective:MaxIterations",
    ];

    private static AppSettingsValidator Validator(params string[] existingDirectories) =>
        new(new FakeDirectoryProbe(existingDirectories));

    private static ValidationMessage? Find(IEnumerable<ValidationMessage> messages, string code) =>
        messages.FirstOrDefault(m => m.Code == code);

    /// <summary>The list's paths, <c>*</c> replaced by a concrete key or index; the daemon's included.</summary>
    public static IEnumerable<string> PlaceholderPaths()
    {
        foreach (var setting in SettingPlaceholders.Settings)
        {
            var segments = setting.Path.Split(':');
            for (var i = 0; i < segments.Length; i++)
            {
                if (segments[i] != "*")
                    continue;
                segments[i] = i < segments.Length - 1
                    ? (segments[i - 1] == "Servers" ? "s" : "p")
                    : segments[i - 1] switch
                    {
                        "Args" => "0",
                        "Env" => "GITHUB_TOKEN",
                        "Secrets" => "PEER_KEY",
                        _ => "ITEM",
                    };
            }

            yield return string.Join(':', segments);
        }
    }

    /// <summary>One row per entry of the list other than the two API keys, plus a row in another case.</summary>
    public static TheoryData<string> PlaceholderRows()
    {
        var data = new TheoryData<string>();
        foreach (var path in PlaceholderPaths())
        {
            if (path == "Llm:ApiKey" || path == "Llm:Profiles:p:ApiKey")
                continue;
            data.Add(path);
        }

        data.Add("llm:baseurl");
        return data;
    }

    /// <summary>A settings document holding <paramref name="value"/> at <paramref name="path"/> (a number segment is an index).</summary>
    public static string NestedJson(string path, string value)
    {
        var root = new JsonObject { ["RaggableTree"] = new JsonObject { ["Enabled"] = false } };
        JsonNode node = root;
        var segments = path.Split(':');
        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;
            JsonNode child = last
                ? JsonValue.Create(value)!
                : int.TryParse(segments[i + 1], out _) ? new JsonArray() : new JsonObject();
            if (node is JsonArray array)
                array.Add(child);
            else
                ((JsonObject)node)[segments[i]] = child;
            node = child;
        }

        return root.ToJsonString();
    }

    [Theory]
    [Trait("BR", "CH-15")]
    [MemberData(nameof(PlaceholderRows))]
    public void ShouldReportAnErrorWithTheRunLine_ForEachSettingOfTheListARunReads(string path)
    {
        var document = AppSettingsDocument.Parse(NestedJson(path, "${X}"));

        var errors = Validator().Validate(document)
            .Where(m => m.Severity == ValidationSeverity.Error)
            .Where(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var error = Assert.Single(errors);
        Assert.Contains(
            OperatorMessages.SettingPlaceholderRefused(path, SettingPlaceholders.CorrectionFor(path)!),
            error.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_without_an_llm_section_reproduces_the_win01_warning()
    {
        var document = AppSettingsDocument.Parse("""{ "RateLimiting": { "QueueLimit": 32 } }""");

        var message = Find(Validator().Validate(document), ValidationCodes.LlmSectionMissing);

        Assert.NotNull(message);
        Assert.Equal(ValidationSeverity.Warning, message.Severity);
        Assert.Equal(AppSettingsValidator.LlmNotConfiguredWarning, message.Text);
        Assert.StartsWith(AppSettingsValidator.LlmNotConfiguredMessage, message.Text, StringComparison.Ordinal);
        Assert.Contains("<undefined-llm>", message.Text, StringComparison.Ordinal);

        // Studio only sees the file: it must say the environment can already have answered
        // this, or it sends users hunting for a problem ORKEON_Llm__* already solved.
        Assert.Contains("ORKEON_Llm__BaseUrl", message.Text, StringComparison.Ordinal);
        Assert.Contains("emits no warning", message.Text, StringComparison.Ordinal);
        Assert.Equal("Llm", message.Path);
    }

    [Fact]
    public void An_empty_llm_section_also_triggers_win01()
    {
        var document = AppSettingsDocument.Parse("""{ "Llm": {} }""");

        Assert.NotNull(Find(Validator().Validate(document), ValidationCodes.LlmSectionMissing));
    }

    [Fact]
    public void A_configured_llm_section_does_not_trigger_win01()
    {
        var document = AppSettingsDocument.Parse(
            """{ "Llm": { "Model": "llama3.2", "BaseUrl": "http://localhost:11434" } }""");

        Assert.Null(Find(Validator().Validate(document), ValidationCodes.LlmSectionMissing));
    }

    [Fact]
    public void A_malformed_document_yields_a_single_json_error()
    {
        var messages = Validator().ValidateJson("{ oops");

        Assert.Equal(ValidationCodes.MalformedJson, Assert.Single(messages).Code);
    }

    [Fact]
    public void A_known_numeric_field_holding_text_is_an_error()
    {
        var document = AppSettingsDocument.Parse(
            """{ "Llm": { "Model": "m" }, "RateLimiting": { "QueueLimit": "many" } }""");

        var message = Find(Validator().Validate(document), ValidationCodes.InvalidFieldType);

        Assert.NotNull(message);
        Assert.Equal("RateLimiting:QueueLimit", message.Path);
    }

    [Fact]
    public void The_thinking_switch_must_be_a_boolean_and_its_effort_a_string()
    {
        // LLM-11: Llm:Thinking:{Enabled,Effort} are the keys the runner reads.
        var wrongSwitch = AppSettingsDocument.Parse(
            """{ "Llm": { "Model": "kimi-k2.6", "Thinking": { "Enabled": "yes" } } }""");
        var switchMessage = Find(Validator().Validate(wrongSwitch), ValidationCodes.InvalidFieldType);
        Assert.NotNull(switchMessage);
        Assert.Equal("Llm:Thinking:Enabled", switchMessage.Path);

        var wrongEffort = AppSettingsDocument.Parse(
            """{ "Llm": { "Model": "kimi-k2.6", "Thinking": { "Effort": { "level": "high" } } } }""");
        var effortMessage = Find(Validator().Validate(wrongEffort), ValidationCodes.InvalidFieldType);
        Assert.NotNull(effortMessage);
        Assert.Equal("Llm:Thinking:Effort", effortMessage.Path);

        var right = AppSettingsDocument.Parse(
            """{ "Llm": { "Model": "kimi-k2.6", "Thinking": { "Enabled": false, "Effort": "high" } } }""");
        Assert.Null(Find(Validator().Validate(right), ValidationCodes.InvalidFieldType));
        Assert.False(right.Llm.ThinkingEnabled);
        Assert.Equal("high", right.Llm.ThinkingEffort);
    }

    [Fact]
    public void A_numeric_field_spelled_as_a_numeric_string_is_accepted()
    {
        var document = AppSettingsDocument.Parse(
            """{ "Llm": { "Model": "m", "MaxTokens": "4096" } }""");

        Assert.Null(Find(Validator().Validate(document), ValidationCodes.InvalidFieldType));
    }

    [Fact]
    public void Every_integer_field_a_screen_writes_is_a_number_before_the_file_is_reloaded()
    {
        // STUDIO-53: electing a setting that pins a timeout wrote Llm:TimeoutSeconds through
        // SetInt32, and the validator refused the node as « not a number » — the save with it.
        var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": "m" } }""");
        foreach (var path in IntegerFields)
            document.SetInt32(path, 600);

        var refused = Validator().Validate(document)
            .Where(m => m.Code == ValidationCodes.InvalidFieldType)
            .Select(m => m.Path);

        Assert.Empty(refused);
    }

    [Fact]
    public void A_temperature_json_cannot_write_is_not_a_number()
    {
        // NaN has no JSON text: the file could not be written, so the save is refused by name.
        var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": "m" } }""");
        document.Llm.Temperature = double.NaN;

        var message = Find(Validator().Validate(document), ValidationCodes.InvalidFieldType);

        Assert.NotNull(message);
        Assert.Equal("Llm:Temperature", message.Path);
    }

    [Fact]
    public void A_known_string_field_holding_an_object_is_an_error()
    {
        var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": { "name": "m" } } }""");

        var message = Find(Validator().Validate(document), ValidationCodes.InvalidFieldType);

        Assert.NotNull(message);
        Assert.Equal("Llm:Model", message.Path);
    }

    [Fact]
    public void A_non_absolute_base_url_is_an_error()
    {
        var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": "m", "BaseUrl": "localhost:11434" } }""");

        var message = Find(Validator().Validate(document), ValidationCodes.InvalidFieldType);

        Assert.NotNull(message);
        Assert.Equal("Llm:BaseUrl", message.Path);
    }

    [Theory]
    [Trait("BR", "CH-09")]
    [InlineData("Llm", "Llm:BaseUrl", """{ "Llm": { "Model": "m", "BaseUrl": "https://api.deepseek.com/anthropic" } }""")]
    [InlineData("Llm:Profiles:ds", "Llm:Profiles:ds:BaseUrl", """{ "Llm": { "Model": "m", "BaseUrl": "http://localhost:11434", "Profiles": { "ds": { "Model": "m", "BaseUrl": "https://api.deepseek.com/anthropic" } } } }""")]
    public void ShouldReportAnError_WhenTheAddressIsAVendorAnthropicPath(string sectionPath, string path, string json)
    {
        const string baseUrl = "https://api.deepseek.com/anthropic";
        var reading = Orkeon.Domain.SharedKernel.ValueObjects.LlmEndpointReading.Of(baseUrl, "m", null);
        var expected = Orkeon.Constants.Configuration.OperatorMessages.LlmVendorAnthropicPathRefused(
            $"{sectionPath}:BaseUrl", baseUrl, reading.ProviderKey);

        var messages = Validator().Validate(AppSettingsDocument.Parse(json));

        var message = Assert.Single(messages, m => m.Path == path);
        Assert.Equal(ValidationSeverity.Error, message.Severity);
        Assert.Equal(expected, message.Text);

        // A non-http(s) address keeps its single STUDIO-TYPE message: the new rule adds none.
        var notHttp = AppSettingsDocument.Parse(
            """{ "Llm": { "Model": "m", "BaseUrl": "localhost:11434" } }""");
        var single = Assert.Single(Validator().Validate(notHttp), m => m.Path == "Llm:BaseUrl");
        Assert.Equal(ValidationCodes.InvalidFieldType, single.Code);
    }

    [Theory]
    [Trait("BR", "CH-09")]
    [InlineData("Llm", """{ "Llm": { "Model": "m", "BaseUrl": "http://localhost:8080" } }""")]
    [InlineData("Llm:Profiles:local", """{ "Llm": { "Model": "m", "Profiles": { "local": { "Model": "m", "BaseUrl": "http://localhost:8080" } } } }""")]
    public void ShouldReportAnInformation_WhenACustomAddressHasNoPath(string sectionPath, string json)
    {
        const string baseUrl = "http://localhost:8080";

        var messages = Validator().Validate(AppSettingsDocument.Parse(json), ValidationScope.Saving);

        var message = Assert.Single(messages, m => m.Code == "STUDIO-LLM-ENDPOINT");
        Assert.Equal(ValidationSeverity.Information, message.Severity);
        Assert.Equal($"{sectionPath}:BaseUrl", message.Path);
        Assert.Equal(Orkeon.Constants.Configuration.OperatorMessages.LlmBaseUrlSilent($"{sectionPath}:BaseUrl", baseUrl), message.Text);
        Assert.DoesNotContain(messages, m => m.Severity == ValidationSeverity.Error);
    }

    /// <summary>
    /// A section that names its <c>Provider</c> is built by that name, its address unread: the run
    /// says nothing of an address without a path (RunnerHost.WarnSilentAddresses), and neither does Studio.
    /// </summary>
    [Theory]
    [InlineData("""{ "Llm": { "Provider": "mock", "Responses": "/r.jsonl", "Model": "m", "BaseUrl": "http://localhost:8080" } }""")]
    [InlineData("""{ "Llm": { "Model": "m", "Profiles": { "p": { "Provider": "MOCK", "Responses": "/r.jsonl", "Model": "m", "BaseUrl": "http://localhost:8080" } } } }""")]
    public void ShouldNotReportASilentAddress_WhenTheSectionNamesItsProvider(string json)
    {
        var messages = Validator().Validate(AppSettingsDocument.Parse(json), ValidationScope.Saving);

        Assert.DoesNotContain(messages, m => m.Code == ValidationCodes.LlmEndpoint);
    }

    [Fact]
    public void An_unknown_rag_profile_is_an_error_listing_the_known_ones()
    {
        var document = AppSettingsDocument.Parse(
            """{ "Llm": { "Model": "m" }, "Orkeon": { "Rag": { "Profile": "turbo" } } }""");

        var message = Find(Validator().Validate(document), ValidationCodes.UnknownRagProfile);

        Assert.NotNull(message);
        foreach (var known in RagSection.KnownProfiles)
            Assert.Contains(known, message.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("balanced")]
    [InlineData("quality")]
    [InlineData("adaptive")]
    [InlineData("corrective")]
    public void Every_known_rag_profile_is_accepted(string profile)
    {
        var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": "m" } }""");
        document.Rag.Profile = profile;

        Assert.Null(Find(Validator().Validate(document), ValidationCodes.UnknownRagProfile));
    }

    /// <summary>VFS-90 D-03: two settings entries of one root, each with an id, are a valid file.</summary>
    /// <summary>
    /// STUDIO-48 (GAP-19): <c>Orkeon:Rag:LlmProfile</c> naming a profile the file does not define
    /// refuses the start of every run — said before the file is written. A warning: the profile can
    /// come from the launch environment, which is how Studio passes its own model settings.
    /// </summary>
    [Fact]
    public void A_rag_llm_profile_the_file_does_not_define_is_a_warning_listing_the_defined_ones()
    {
        var document = AppSettingsDocument.Parse("""
            {
              "Llm": { "Model": "qwen3", "Profiles": { "claude": { "Model": "claude-sonnet-5" } } },
              "Orkeon": { "Rag": { "LlmProfile": "claud" } }
            }
            """);

        var message = Find(Validator().Validate(document), ValidationCodes.UnknownRagLlmProfile);

        Assert.NotNull(message);
        Assert.Equal(ValidationSeverity.Warning, message.Severity);
        Assert.Contains("'claud'", message.Text, StringComparison.Ordinal);
        Assert.Contains("default, claude.", message.Text, StringComparison.Ordinal);
        Assert.Equal("Orkeon:Rag:LlmProfile", message.Path);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("CLAUDE")]
    [InlineData("default")]
    [InlineData("")]
    public void A_rag_llm_profile_the_file_defines_or_the_default_is_accepted(string profile)
    {
        var document = AppSettingsDocument.Parse($$"""
            {
              "Llm": { "Model": "qwen3", "Profiles": { "claude": { "Model": "claude-sonnet-5" } } },
              "Orkeon": { "Rag": { "LlmProfile": "{{profile}}" } }
            }
            """);

        Assert.Null(Find(Validator().Validate(document), ValidationCodes.UnknownRagLlmProfile));
    }

    [Fact]
    public void Two_entries_of_one_root_with_ids_validate_without_an_error()
    {
        var a = Orkeon.Domain.Common.MountId.Create();
        var b = Orkeon.Domain.Common.MountId.Create();
        var document = AppSettingsDocument.Parse(
            $$"""{ "Llm": { "Model": "m" }, "Orkeon": { "FileSystem": { "Mounts": [ "{{a}}|/srv/a:/output:rw", "{{b}}|/srv/b:/output:rw" ] } } }""");

        var messages = Validator("/srv/a", "/srv/b").Validate(document, ValidationScope.Saving);

        Assert.DoesNotContain(messages, m => m.Severity == ValidationSeverity.Error);
        Assert.NotNull(Find(messages, ValidationCodes.MountSharedRoot));
    }

    [Fact]
    public void A_document_without_mounts_warns_rather_than_fails()
    {
        var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": "m" } }""");

        var message = Find(Validator().Validate(document), ValidationCodes.MountsEmpty);

        Assert.NotNull(message);
        Assert.Equal(ValidationSeverity.Warning, message.Severity);
    }

    [Fact]
    public void Saving_a_document_without_mounts_is_flagged_and_never_blocked()
    {
        // STUDIO-57: the runner mounts nothing and runs the crew all the same — a team that reads
        // a mail and answers a mail needs no folder, and a team's own folders travel with it.
        // Saving used to block here, on the claim that the runtime refuses to start.
        var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": "m" } }""");

        var messages = Validator().Validate(document, ValidationScope.Saving);
        var message = Find(messages, ValidationCodes.MountsEmpty);

        Assert.NotNull(message);
        Assert.Equal(ValidationSeverity.Warning, message.Severity);
        Assert.Equal(MountsSection.SectionPath, message.Path);
        Assert.DoesNotContain(messages, m => m.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void The_save_scope_reaches_the_json_entry_point_too()
    {
        var messages = Validator().ValidateJson("""{ "Llm": { "Model": "m" } }""", ValidationScope.Saving);

        Assert.Equal(ValidationSeverity.Warning, Assert.Single(messages, m => m.Code == ValidationCodes.MountsEmpty).Severity);
        Assert.DoesNotContain(messages, m => m.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void A_document_with_mounts_is_saveable()
    {
        var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": "m" } }""");
        document.Mounts.SetRaw(["/srv/data:/workspace:ro"]);

        var messages = Validator("/srv/data").Validate(document, ValidationScope.Saving);

        Assert.Null(Find(messages, ValidationCodes.MountsEmpty));
        Assert.DoesNotContain(messages, m => m.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Declared_mounts_are_validated_against_the_disk()
    {
        var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": "m" } }""");
        document.Mounts.SetRaw(["/srv/data:/workspace:ro", "/gone:/output:rw"]);

        var messages = Validator("/srv/data").Validate(document);

        var message = Find(messages, ValidationCodes.MountPathMissing);
        Assert.NotNull(message);
        Assert.Contains("/gone", message.Text, StringComparison.Ordinal);
        Assert.Null(Find(messages, ValidationCodes.MountsEmpty));
    }

    [Fact]
    public void An_inline_api_key_is_reported_as_advice_not_as_a_failure()
    {
        var document = AppSettingsDocument.Parse(
            """{ "Llm": { "Model": "m", "ApiKey": "sk-live-123" } }""");

        var message = Find(Validator().Validate(document), ValidationCodes.InlineApiKey);

        Assert.NotNull(message);
        Assert.Equal(ValidationSeverity.Information, message.Severity);
        Assert.Contains("ORKEON_Llm__ApiKey", message.Text, StringComparison.Ordinal);
        // STUDIO-49: a key the configuration holds wins — it masks the variable the reference names.
        Assert.Contains("masks Llm:ApiKeyEnvVar", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_docker_model_runner_placeholder_key_is_not_reported()
    {
        var document = AppSettingsDocument.Parse(
            """{ "Llm": { "Model": "m", "ApiKey": "not-needed" } }""");

        Assert.Null(Find(Validator().Validate(document), ValidationCodes.InlineApiKey));
    }

    /// <summary>
    /// STUDIO-54, decision 4: beside a reference — a file edited by hand —, the placeholder masks it
    /// like any key the file holds: the run would send <c>not-needed</c> to the endpoint whose key the
    /// variable holds.
    /// </summary>
    [Fact]
    public void The_placeholder_key_beside_a_reference_is_reported_as_it_masks_it()
    {
        var document = AppSettingsDocument.Parse(
            """{ "Llm": { "Model": "deepseek-v4-flash", "ApiKey": "not-needed", "ApiKeyEnvVar": "DEEPSEEK_API_KEY" } }""");

        var message = Find(Validator().Validate(document), ValidationCodes.InlineApiKey);

        Assert.NotNull(message);
        Assert.Equal(ValidationSeverity.Information, message.Severity);
        Assert.Equal("Llm:ApiKey", message.Path);
        Assert.Contains("not-needed", message.Text, StringComparison.Ordinal);
        Assert.Contains("masks Llm:ApiKeyEnvVar", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_sample_validates_with_only_the_mounts_warning()
    {
        const string sample = """
            {
              "Llm": {
                "Model": "ai/granite-4.0-h-tiny",
                "BaseUrl": "http://localhost:12434/engines/llama.cpp/v1",
                "ApiKey": "not-needed",
                "Temperature": 0.7,
                "MaxTokens": 4096,
                "TimeoutSeconds": 120
              },
              "RateLimiting": {
                "MaxConcurrentRequests": 1,
                "GlobalRequestsPerMinute": 60,
                "ProviderRequestsPerMinute": 30,
                "AgentRequestsPerMinute": 20,
                "QueueLimit": 32
              }
            }
            """;

        var messages = Validator().ValidateJson(sample);

        Assert.Equal(ValidationCodes.MountsEmpty, Assert.Single(messages).Code);
    }

    [Fact]
    [Trait("BR", "R-58")]
    public void ShouldRejectSettings_WhenLlmCacheTtlIsInvalid()
    {
        var document = AppSettingsDocument.Parse(
            """{ "Llm": { "Model": "m", "Cache": { "Ttl": "2h" } } }""");

        var messages = Validator().Validate(document);

        Assert.Contains(messages, m => m.Severity == ValidationSeverity.Error && m.Path == "Llm:Cache:Ttl");
    }

    /// <summary>
    /// A lifetime needs a breakpoint, and <c>Cache:Conversation</c> marks one (Orkeon #490): left out it
    /// follows the other two switches, set it decides alone. A switch that is not true or false stops the
    /// start at that switch, a blank one is unset. Judged as the run reads the section (<c>LlmSettings</c>),
    /// under the default and under a profile; <paramref name="refusedAt"/> is the key refused, if any.
    /// </summary>
    [Theory]
    [InlineData("""{ "Conversation": true, "Ttl": "1h" }""", null)]
    [InlineData("""{ "Conversation": "true", "Ttl": "5m" }""", null)]
    [InlineData("""{ "Conversation": false, "Ttl": "1h" }""", "Cache:Ttl")]
    [InlineData("""{ "Ttl": "1h" }""", "Cache:Ttl")]
    [InlineData("""{ "SystemPrompt": true, "Conversation": false, "Ttl": "1h" }""", null)]
    [InlineData("""{ "Tools": true, "Ttl": "1h" }""", null)]
    [InlineData("""{ "Conversation": true, "Ttl": "2h" }""", "Cache:Ttl")]
    [InlineData("""{ "Conversation": "yes" }""", "Cache:Conversation")]
    [InlineData("""{ "Conversation": "yes", "Ttl": "1h" }""", "Cache:Conversation")]
    [InlineData("""{ "SystemPrompt": 1 }""", "Cache:SystemPrompt")]
    [InlineData("""{ "Tools": "on", "Ttl": "1h" }""", "Cache:Tools")]
    [InlineData("""{ "Conversation": " " }""", null)]
    [InlineData("""{ "Conversation": "", "Ttl": "1h" }""", "Cache:Ttl")]
    [InlineData("""{ "SystemPrompt": "TRUE", "Ttl": " 1h " }""", null)]
    public void A_cache_is_judged_with_the_conversation_as_the_run_reads_it(string cache, string? refusedAt)
    {
        AssertCacheAsTheRun($$"""{ "Llm": { "Model": "m", "Cache": {{cache}}, "Profiles": { "p": { "Model": "m", "Cache": {{cache}} } } } }""", refusedAt);
    }

    /// <summary>A key written as one flattened name reads as its segments, to the run as here.</summary>
    [Theory]
    [InlineData("""{ "Cache:Ttl": "2h" }""", "Cache:Ttl")]
    [InlineData("""{ "Cache:Conversation": true, "Cache:Ttl": "1h" }""", null)]
    [InlineData("""{ "Cache": { "Conversation": "yes" }, "Cache:Ttl": "1h" }""", "Cache:Conversation")]
    public void A_flattened_cache_key_is_judged_as_the_run_reads_it(string members, string? refusedAt)
    {
        var inner = members.Trim()[1..^1];
        AssertCacheAsTheRun($$"""{ "Llm": { "Model": "m", {{inner}}, "Profiles": { "p": { "Model": "m", {{inner}} } } } }""", refusedAt);
    }

    private static void AssertCacheAsTheRun(string json, string? refusedAt)
    {
        var runtime = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

        var errors = Validator().Validate(AppSettingsDocument.Parse(json))
            .Where(m => m.Severity == ValidationSeverity.Error && m.Path?.Contains(":Cache:", StringComparison.Ordinal) == true)
            .Select(m => m.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        string[] expected = refusedAt is null ? [] : [$"Llm:{refusedAt}", $"Llm:Profiles:p:{refusedAt}"];
        Assert.Equal(expected.Order(StringComparer.Ordinal), errors);
        Assert.Equal(refusedAt is null ? null : $"Llm:{refusedAt}", Refusal(() => LlmSettings.ReadDefault(runtime)));
        Assert.Equal(refusedAt is null ? null : $"Llm:Profiles:p:{refusedAt}", Refusal(() => LlmSettings.ReadProfiles(runtime)));

        // The key the run names in its refusal: every refusal of a cache key starts with its path.
        static string? Refusal(Action read)
        {
            try
            {
                read();
                return null;
            }
            catch (InvalidOperationException ex)
            {
                var cache = ex.Message.IndexOf(":Cache:", StringComparison.Ordinal);
                Assert.True(cache > 0, ex.Message);
                var end = cache + ":Cache:".Length;
                while (end < ex.Message.Length && char.IsAsciiLetter(ex.Message[end]))
                    end++;
                return ex.Message[..end];
            }
        }
    }

    [Theory]
    [Trait("BR", "R-41")]
    [InlineData("extreme")]
    [InlineData("High")]
    public void ShouldRejectSettings_WhenLlmThinkingEffortIsUnknown(string effort)
    {
        var document = AppSettingsDocument.Parse(
            "{ \"Llm\": { \"Model\": \"m\", \"Thinking\": { \"Effort\": \"" + effort + "\" } } }");

        var messages = Validator().Validate(document);

        Assert.Contains(messages, m => m.Severity == ValidationSeverity.Error && m.Path == "Llm:Thinking:Effort");
        var valid = AppSettingsDocument.Parse(
            """{ "Llm": { "Model": "m", "Thinking": { "Effort": "high" } } }""");
        Assert.DoesNotContain(Validator().Validate(valid), m => m.Path == "Llm:Thinking:Effort");
    }
    [Theory]
    // #356: the run refuses at start a Thinking that names no support - a comma, a number, a typo.
    [InlineData("EffortOnly,Toggle")]
    [InlineData("1")]
    [InlineData("Always")]
    public void ShouldRejectSettings_WhenModelCapabilityThinkingIsNoSupport(string thinking)
    {
        var document = AppSettingsDocument.Parse(
            "{ \"Llm\": { \"Model\": \"m\", \"ModelCapabilities\": { \"openrouter/google/gemini-3.7\": { \"Thinking\": \"" + thinking + "\" } } } }");

        var messages = Validator().Validate(document);

        var refusal = Assert.Single(messages, m => m.Severity == ValidationSeverity.Error
            && m.Path == "Llm:ModelCapabilities:openrouter/google/gemini-3.7:Thinking");
        // The engine's sentence (Orkeon #465): the key and the names, never the value.
        Assert.Equal(
            "Llm:ModelCapabilities:openrouter/google/gemini-3.7:Thinking is not a thinking support: write one of None, EffortOnly, Toggle, Budget.",
            refusal.Text);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("effortonly")]
    [InlineData("Toggle")]
    [InlineData("Budget")]
    [InlineData("  ")]
    public void ShouldAcceptSettings_WhenModelCapabilityThinkingNamesASupportOrIsBlank(string thinking)
    {
        var document = AppSettingsDocument.Parse(
            "{ \"Llm\": { \"Model\": \"m\", \"ModelCapabilities\": { \"glm-5.3\": { \"Thinking\": \"" + thinking + "\", \"ReasoningEfforts\": [\"low\", \"none\"] } } } }");

        var messages = Validator().Validate(document);

        Assert.DoesNotContain(messages, m => m.Path?.StartsWith("Llm:ModelCapabilities", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ShouldRejectSettings_WhenAModelCapabilitySwitchIsNotABoolean()
    {
        var document = AppSettingsDocument.Parse(
            """{ "Llm": { "Model": "m", "ModelCapabilities": { "gpt-4o": { "Vision": "yes", "AudioInput": 1, "AudioOutput": "true" } } } }""");

        var messages = Validator().Validate(document);

        Assert.Contains(messages, m => m.Severity == ValidationSeverity.Error && m.Path == "Llm:ModelCapabilities:gpt-4o:Vision");
        Assert.Contains(messages, m => m.Severity == ValidationSeverity.Error && m.Path == "Llm:ModelCapabilities:gpt-4o:AudioInput");
        Assert.DoesNotContain(messages, m => m.Path == "Llm:ModelCapabilities:gpt-4o:AudioOutput");
    }

    private static IReadOnlyList<ValidationMessage> ModelCapabilityMessages(string modelCapabilitiesJson) =>
        [.. Validator()
            .Validate(AppSettingsDocument.Parse("{ \"Llm\": { \"Model\": \"m\", \"ModelCapabilities\": " + modelCapabilitiesJson + " } }"))
            .Where(m => m.Path?.StartsWith("Llm:ModelCapabilities", StringComparison.Ordinal) == true)];

    [Theory]
    // The run reads a switch and Thinking as single values: a list or an object that holds anything stops it.
    [InlineData("""{ "gpt-4o": { "Thinking": ["Toggle"] } }""", "Llm:ModelCapabilities:gpt-4o:Thinking")]
    [InlineData("""{ "gpt-4o": { "Thinking": { "Level": "Toggle" } } }""", "Llm:ModelCapabilities:gpt-4o:Thinking")]
    [InlineData("""{ "gpt-4o": { "Vision": [true] } }""", "Llm:ModelCapabilities:gpt-4o:Vision")]
    [InlineData("""{ "gpt-4o": { "AudioInput": { "a": true } } }""", "Llm:ModelCapabilities:gpt-4o:AudioInput")]
    [InlineData("""{ "gpt-4o": { "AudioOutput": [null] } }""", "Llm:ModelCapabilities:gpt-4o:AudioOutput")]
    // An item of ReasoningEfforts is a single value too.
    [InlineData("""{ "gpt-4o": { "ReasoningEfforts": [["low"]] } }""", "Llm:ModelCapabilities:gpt-4o:ReasoningEfforts")]
    [InlineData("""{ "gpt-4o": { "ReasoningEfforts": ["low", { "a": "high" }] } }""", "Llm:ModelCapabilities:gpt-4o:ReasoningEfforts")]
    // An entry carries its five keys and no other.
    [InlineData("""{ "gpt-4o": { "Thinkng": "Toggle" } }""", "Llm:ModelCapabilities:gpt-4o:Thinkng")]
    [InlineData("""{ "gpt-4o": ["x"] }""", "Llm:ModelCapabilities:gpt-4o:0")]
    [InlineData("""{ "gpt-4o": { "Thinking:Level": "Toggle" } }""", "Llm:ModelCapabilities:gpt-4o:Thinking")]
    // The section written as a list: its items are the entries 0, 1...
    [InlineData("""[ { "Thinking": "Always" } ]""", "Llm:ModelCapabilities:0:Thinking")]
    [InlineData("""[ ["x"] ]""", "Llm:ModelCapabilities:0:0")]
    public void ShouldRejectSettings_WhenAModelCapabilityEntryIsNotShapedAsTheRunReadsIt(string json, string path)
    {
        var message = Assert.Single(ModelCapabilityMessages(json));

        Assert.Equal(ValidationSeverity.Error, message.Severity);
        Assert.Equal(ValidationCodes.InvalidFieldType, message.Code);
        Assert.Equal(path, message.Path);
    }

    [Theory]
    [InlineData("""{ "llama3.2:latest": { "Thinking": "None" } }""")]
    [InlineData("""{ "llama3.2:latest": {} }""")]
    public void ShouldRejectSettings_WhenAModelIdHoldsAColon_NamingThePrefixToKey(string json)
    {
        var message = Assert.Single(ModelCapabilityMessages(json));

        Assert.Equal(ValidationSeverity.Error, message.Severity);
        Assert.Equal("Llm:ModelCapabilities:llama3.2:latest", message.Path);
        Assert.Contains("'llama3.2:latest'", message.Text, StringComparison.Ordinal);
        Assert.Contains("key the entry 'llama3.2' instead", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldNameTheColonOnlyOnTheKeyItSplits_WhenATypoSitsBesideAnIdWithAColon()
    {
        var messages = ModelCapabilityMessages(
            """{ "llama3.2": { "Thinkng": "None" }, "llama3.2:latest": { "Thinking": "None" } }""");

        Assert.Equal(2, messages.Count);
        var typo = Assert.Single(messages, m => m.Path == "Llm:ModelCapabilities:llama3.2:Thinkng");
        Assert.DoesNotContain("instead", typo.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("llama3.2:latest", typo.Text, StringComparison.Ordinal);
        var colon = Assert.Single(messages, m => m.Path == "Llm:ModelCapabilities:llama3.2:latest");
        Assert.Contains("key the entry 'llama3.2' instead", colon.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldNotNameAColon_WhenTheUnknownKeyIsATypo()
    {
        var message = Assert.Single(ModelCapabilityMessages("""{ "gpt-4o": { "Thinkng": "Toggle" } }"""));

        Assert.Contains("AudioInput, AudioOutput, Vision, ReasoningEfforts and Thinking", message.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("instead", message.Text, StringComparison.Ordinal);
    }

    [Theory]
    // Equal but for the case, a value on both sides.
    [InlineData("""{ "gpt-4o": { "Vision": true, "vision": false } }""", "Llm:ModelCapabilities:gpt-4o:Vision")]
    [InlineData("""{ "gpt-4o": { "Thinking": "Toggle" }, "GPT-4O": { "thinking": "None" } }""", "Llm:ModelCapabilities:gpt-4o:Thinking")]
    // The same path once a key is split at its ':'.
    [InlineData("""{ "llama3.2:Vision": true, "llama3.2": { "Vision": false } }""", "Llm:ModelCapabilities:llama3.2:Vision")]
    // An item of a list is a value too, and so is a null.
    [InlineData("""{ "x": { "ReasoningEfforts": ["a"], "reasoningefforts": ["b"] } }""", "Llm:ModelCapabilities:x:ReasoningEfforts:0")]
    [InlineData("""{ "x": { "ReasoningEfforts": ["a"], "reasoningefforts": { "0": "b" } } }""", "Llm:ModelCapabilities:x:ReasoningEfforts:0")]
    [InlineData("""{ "gpt-4o": { "Vision": null, "vision": true } }""", "Llm:ModelCapabilities:gpt-4o:Vision")]
    [InlineData("""{ "gpt-4o": { "Vision": true, "vision": null } }""", "Llm:ModelCapabilities:gpt-4o:Vision")]
    // An empty list or object holds its key: the value written after it is refused.
    [InlineData("""{ "gpt-4o": { "Vision": {}, "vision": true } }""", "Llm:ModelCapabilities:gpt-4o:Vision")]
    [InlineData("""{ "gpt-4o": { "Vision": [], "vision": true } }""", "Llm:ModelCapabilities:gpt-4o:Vision")]
    // Three spellings: one finding at the path.
    [InlineData("""{ "gpt-4o": { "Vision": true, "vision": false, "VISION": true } }""", "Llm:ModelCapabilities:gpt-4o:Vision")]
    public void ShouldRejectSettings_WhenTwoModelCapabilityKeysSetOneSameValue(string json, string path)
    {
        var message = Assert.Single(ModelCapabilityMessages(json));

        Assert.Equal(ValidationSeverity.Error, message.Severity);
        Assert.Equal(ValidationCodes.InvalidFieldType, message.Code);
        Assert.Equal(path, message.Path);
        Assert.Contains("the configuration refuses a key written twice", message.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two single values under one id in two letter cases: the key written twice, and the single value
    /// where the entry's keys are expected, which the start refuses since Orkeon #454.
    /// </summary>
    [Fact]
    public void ShouldRejectSettings_WhenTwoModelCapabilityEntriesInTwoCasesAreSingleValues()
    {
        var messages = ModelCapabilityMessages("""{ "gpt-4o": "x", "GPT-4O": "y" }""");

        Assert.All(messages, message => Assert.Equal("Llm:ModelCapabilities:gpt-4o", message.Path));
        Assert.Single(messages, message => message.Text.Contains("the configuration refuses a key written twice", StringComparison.Ordinal));
        Assert.Single(messages, message => message.Text.Contains("is a single value where a section of keys is expected", StringComparison.Ordinal));
    }

    /// <summary>
    /// An entry written as a single value, and a switch of an entry written as blanks, are refused by the
    /// start since Orkeon #454: the run's binder no longer drops them without a word.
    /// </summary>
    [Theory]
    [InlineData("""{ "gpt-4o": "x" }""", "Llm:ModelCapabilities:gpt-4o", "is a single value where a section of keys is expected")]
    [InlineData("""[ "x" ]""", "Llm:ModelCapabilities:0", "is a single value where a section of keys is expected")]
    [InlineData("""{ "gpt-4o": { "AudioOutput": " " } }""", "Llm:ModelCapabilities:gpt-4o:AudioOutput", "must be true or false")]
    public void ShouldRejectSettings_WhenAModelCapabilityEntryIsASingleValueOrASwitchIsBlank(string json, string path, string words)
    {
        var message = Assert.Single(ModelCapabilityMessages(json));

        Assert.Equal(ValidationSeverity.Error, message.Severity);
        Assert.Equal(ValidationCodes.InvalidFieldType, message.Code);
        Assert.Equal(path, message.Path);
        Assert.Contains(words, message.Text, StringComparison.Ordinal);
    }

    [Theory]
    // Two objects that set no same key are read as one.
    [InlineData("""{ "gpt-4o": { "Vision": true }, "GPT-4O": { "AudioInput": true } }""")]
    [InlineData("""{ "llama3.2:Vision": true, "llama3.2": { "AudioInput": false } }""")]
    [InlineData("""{ "x": { "ReasoningEfforts": ["a"], "reasoningefforts": { "1": "b" } } }""")]
    // An empty list or object after a value replaces it with none: even a value the run would refuse.
    [InlineData("""{ "gpt-4o": { "Vision": true, "vision": {} } }""")]
    [InlineData("""{ "gpt-4o": { "Vision": "yes", "vision": [] } }""")]
    [InlineData("""{ "gpt-4o": { "Thinking": "Always", "thinking": {} } }""")]
    [InlineData("""{ "gpt-4o": { "Vision": {}, "vision": [] } }""")]
    [InlineData("""{ "gpt-4o": { "Vision": true }, "gpt-4o:Vision": {} }""")]
    public void ShouldAcceptSettings_WhenTwoModelCapabilityKeysSetNoSameValue(string json)
    {
        Assert.Empty(ModelCapabilityMessages(json));
    }

    [Fact]
    public void ShouldReportTheSecondValueAndTheValueItself_WhenAKeyWrittenTwiceAlsoHoldsAValueTheRunRefuses()
    {
        var messages = ModelCapabilityMessages("""{ "gpt-4o": { "Vision": true, "vision": "yes" } }""");

        Assert.Equal(2, messages.Count);
        Assert.All(messages, message => Assert.Equal("Llm:ModelCapabilities:gpt-4o:Vision", message.Path));
        Assert.Contains(messages, message => message.Text.Contains("written twice", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Text.Contains("true or false", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "": { "Vision": true } }""", "Llm:ModelCapabilities:")]
    [InlineData("""{ " ": { "Vision": true } }""", "Llm:ModelCapabilities: ")]
    [InlineData("""{ "": {} }""", "Llm:ModelCapabilities:")]
    [InlineData("""{ " ": "x" }""", "Llm:ModelCapabilities: ")]
    [InlineData("""{ "": null }""", "Llm:ModelCapabilities:")]
    // A key that starts with ':' is split into a blank id.
    [InlineData("""{ ":Vision": true }""", "Llm:ModelCapabilities:")]
    public void ShouldRejectSettings_WhenAModelCapabilityEntryIsKeyedByABlankId(string json, string path)
    {
        var message = Assert.Single(ModelCapabilityMessages(json));

        Assert.Equal(ValidationSeverity.Error, message.Severity);
        Assert.Equal(ValidationCodes.InvalidFieldType, message.Code);
        Assert.Equal(path, message.Path);
        Assert.Contains("blank model id", message.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Model")]
    [InlineData("BaseUrl")]
    [InlineData("ApiKey")]
    [InlineData("ApiKeyEnvVar")]
    [InlineData("Thinking:Effort")]
    public void ShouldAcceptAnEmptyListOrObject_AndRejectOneThatHoldsAnything_UnderAStringOfTheLlmShape(string key)
    {
        foreach (var section in new[] { "Llm", "Llm:Profiles:local-gpu" })
        {
            var path = $"{section}:{key}";
            foreach (var empty in new[] { "[]", "{}" })
            {
                var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": "m", "Profiles": { "local-gpu": { "Model": "m" } } } }""");
                document.SetNode(path, System.Text.Json.Nodes.JsonNode.Parse(empty));

                Assert.DoesNotContain(Validator().Validate(document), m => m.Severity == ValidationSeverity.Error);
            }

            foreach (var holding in new[] { "[\"x\"]", "{ \"a\": \"x\" }", "[[]]", "{ \"a\": {} }", "[null]" })
            {
                var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": "m", "Profiles": { "local-gpu": { "Model": "m" } } } }""");
                document.SetNode(path, System.Text.Json.Nodes.JsonNode.Parse(holding));

                var message = Assert.Single(Validator().Validate(document), m => m.Path == path);
                Assert.Equal(ValidationSeverity.Error, message.Severity);
                Assert.Equal(ValidationCodes.InvalidFieldType, message.Code);
            }
        }
    }

    [Theory]
    [InlineData("Temperature")]
    [InlineData("MaxTokens")]
    [InlineData("TimeoutSeconds")]
    [InlineData("StreamIdleSeconds")]
    [InlineData("MaxRetries")]
    [InlineData("Thinking:Enabled")]
    [InlineData("Grammar")]
    public void ShouldAcceptAnEmptyListOrObject_AndRejectOneThatHoldsAnything_UnderANumberOrASwitchOfTheLlmShape(string key)
    {
        foreach (var empty in new[] { "[]", "{}" })
        {
            var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": "m" } }""");
            document.SetNode($"Llm:{key}", System.Text.Json.Nodes.JsonNode.Parse(empty));

            Assert.DoesNotContain(Validator().Validate(document), m => m.Severity == ValidationSeverity.Error);
        }

        foreach (var holding in new[] { "[1]", "{ \"a\": true }", "[[]]" })
        {
            var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": "m" } }""");
            document.SetNode($"Llm:{key}", System.Text.Json.Nodes.JsonNode.Parse(holding));

            var message = Assert.Single(Validator().Validate(document), m => m.Path == $"Llm:{key}");
            Assert.Equal(ValidationSeverity.Error, message.Severity);
        }
    }

    [Theory]
    // Left out, each in its own way.
    [InlineData("""{ "gpt-4o": { "Thinking": [], "Vision": {}, "AudioInput": null, "AudioOutput": "", "ReasoningEfforts": [] } }""")]
    [InlineData("""{ "gpt-4o": { "Thinking": {}, "Vision": [] } }""")]
    // An entry that holds nothing to judge.
    [InlineData("""{ "gpt-4o": [] }""")]
    [InlineData("""{ "gpt-4o": {} }""")]
    [InlineData("""[ { "Thinking": "Toggle" } ]""")]
    // Scalar vendor efforts stay the run's to judge.
    [InlineData("""{ "gpt-4o": { "ReasoningEfforts": ["none", "minimal", 3] } }""")]
    [InlineData("""{ "gpt-4o": { "ReasoningEfforts": "low, high" } }""")]
    [InlineData("""{ "gpt-4o": { "ReasoningEfforts": { "a": "low" } } }""")]
    [InlineData("""{ "gpt-4o": { "ReasoningEfforts": [[], {}, null] } }""")]
    [InlineData("""{ "gpt-4o": { "ReasoningEfforts:0": "low" } }""")]
    // Keys without case; a ':' that lands on a key of the entry is that key.
    [InlineData("""{ "gpt-4o": { "thinking": "toggle", "VISION": true } }""")]
    [InlineData("""{ "llama3.2:Vision": true }""")]
    [InlineData("""{ "mammouth/gemini-3.7": { "Thinking": "EffortOnly" }, "llama3.2": { "Thinking": "None" } }""")]
    public void ShouldAcceptSettings_WhenAModelCapabilityEntryIsOneTheRunStartsOn(string json)
    {
        Assert.Empty(ModelCapabilityMessages(json));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\\t")]
    public void ShouldAcceptSettings_WhenLlmThinkingEffortIsWhitespaceAlone(string effort)
    {
        var document = AppSettingsDocument.Parse(
            "{ \"Llm\": { \"Model\": \"m\", \"Thinking\": { \"Effort\": \"" + effort + "\" } } }");

        Assert.DoesNotContain(Validator().Validate(document), m => m.Path == "Llm:Thinking:Effort");
    }

    [Theory]
    [InlineData(" high")]
    [InlineData("high ")]
    public void ShouldRejectSettings_WhenLlmThinkingEffortIsPadded(string effort)
    {
        var document = AppSettingsDocument.Parse(
            "{ \"Llm\": { \"Model\": \"m\", \"Thinking\": { \"Effort\": \"" + effort + "\" } } }");

        Assert.Contains(
            Validator().Validate(document),
            m => m.Severity == ValidationSeverity.Error && m.Path == "Llm:Thinking:Effort");
    }
}
