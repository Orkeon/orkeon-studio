using System.Net;
using System.Text.Json.Nodes;
using Orkeon.Studio.Core.Llm;
using Orkeon.Studio.Core.Tests.Doubles;

namespace Orkeon.Studio.Core.Tests.Llm;

/// <summary>A message of the user's own sent through the endpoint probe, and the reply it reads.</summary>
public sealed class LlmProbePromptTests
{
    private const string Key = "sk-secret-value";

    private static async Task<LlmProbeResult> ProbeAsync(StubHttpMessageHandler handler, string baseUrl, string? prompt = "Say hello.")
    {
        using var client = new HttpClient(handler, disposeHandler: false);
        using var probe = new HttpLlmEndpointProbe(client);
        return await probe.ProbeAsync(
            new LlmProbeRequest { BaseUrl = baseUrl, ApiKey = Key, Model = "m", CheckCompletion = true, Prompt = prompt },
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("https://api.deepseek.com/v1", """{"choices":[{"message":{"content":"Hello!"}}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""", "max_tokens")]
    [InlineData("https://api.deepseek.com/v1", """{"choices":[{"message":{"content":[{"type":"text","text":"Hel"},{"type":"text","text":"lo!"}]}}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""", "max_tokens")]
    [InlineData("https://api.anthropic.com", """{"content":[{"type":"thinking","thinking":"x"},{"type":"text","text":"Hello!"}],"usage":{"input_tokens":3,"output_tokens":2}}""", "max_tokens")]
    [InlineData("http://localhost:11434", """{"message":{"content":"Hello!"},"prompt_eval_count":3,"eval_count":2}""", null)]
    public async Task The_message_is_sent_alone_and_its_reply_read(string baseUrl, string answer, string? cap)
    {
        using var handler = new StubHttpMessageHandler { Body = answer };

        var result = await ProbeAsync(handler, baseUrl);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("Hello!", result.ReplyText);
        Assert.Equal(new LlmProbeUsage(3, 2), result.Usage);
        var body = JsonNode.Parse(Assert.Single(handler.Requests).Body!)!;
        Assert.Equal("Say hello.", (string?)body["messages"]![0]!["content"]);
        Assert.Equal(LlmProbeRequest.PromptMaxTokens, cap is null ? (int?)body["options"]!["num_predict"] : (int?)body[cap]);
    }

    [Theory]
    [InlineData("""{"choices":[{"message":{"content":""}}]}""", "")]
    [InlineData("""{"choices":[{"message":{"content":null}}]}""", "")]
    [InlineData("""{"choices":[]}""", null)]
    [InlineData("not json", null)]
    public async Task An_empty_reply_is_told_from_an_unreadable_one(string answer, string? expected)
    {
        using var handler = new StubHttpMessageHandler { Body = answer };

        Assert.Equal(expected, (await ProbeAsync(handler, "https://api.deepseek.com/v1")).ReplyText);
    }

    [Fact]
    public async Task The_whole_error_body_is_kept_without_the_key()
    {
        var body = new string('x', 500) + " " + Key + " end-of-body";
        using var handler = new StubHttpMessageHandler { StatusCode = HttpStatusCode.BadRequest, Body = body };

        var result = await ProbeAsync(handler, "https://api.deepseek.com/v1");

        Assert.False(result.Succeeded);
        Assert.EndsWith("end-of-body", result.FullDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, result.FullDetail, StringComparison.Ordinal);
        Assert.True(result.Detail!.Length < result.FullDetail!.Length);
    }

    [Fact]
    public async Task Without_a_message_the_test_stays_the_catalogue_then_the_ping()
    {
        using var handler = new StubHttpMessageHandler { Body = """{"data":[{"id":"m"}],"choices":[{"message":{"content":"pong"}}]}""" };

        var result = await ProbeAsync(handler, "https://api.deepseek.com/v1", prompt: null);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("ping", (string?)JsonNode.Parse(handler.LastRequest.Body!)!["messages"]![0]!["content"]);
        Assert.Equal("pong", result.ReplyText);
    }

    [Fact]
    public void Masking_takes_out_each_key_the_longest_first_and_skips_short_values()
    {
        Assert.Equal("*** and *** but abc", SecretMasker.Apply("secret12 and secret1 but abc", ["secret1", "secret12", "abc", null]));
    }
}
