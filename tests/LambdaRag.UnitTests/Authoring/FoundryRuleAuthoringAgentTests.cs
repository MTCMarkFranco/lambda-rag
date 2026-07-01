using System.Runtime.CompilerServices;
using FluentAssertions;
using LambdaRag.Authoring;
using LambdaRag.Core.Domain;
using Microsoft.Extensions.AI;
using Xunit;

namespace LambdaRag.UnitTests.Authoring;

public class FoundryRuleAuthoringAgentTests
{
    private static SourceSpan AnySpan(string doc = "chunk.txt", string? heading = "Identity and Access Management")
        => new(doc, 0, 500, 1, heading);

    private static RuleAuthoringRequest RequestWith(string content, string prefix = "EA-")
        => new(content, "architecture", prefix, AnySpan());

    // ============ predicate DSL validator ============

    [Theory]
    [InlineData("input1.text.Contains(\"WAF\")", true)]
    [InlineData("input1.text.ToLower().Contains(\"waf\")", true)]
    [InlineData("input1.text.Contains(\"a\") || input1.text.Contains(\"b\")", true)]
    [InlineData("(input1.text.ToLower().Contains(\"tls\") && (input1.text.Contains(\"1.2\") || input1.text.Contains(\"1.3\")))", true)]
    // Rejects: unknown method
    [InlineData("input1.text.StartsWith(\"WAF\")", false)]
    // Rejects: property access
    [InlineData("input1.text.Length > 0", false)]
    // Rejects: other identifiers
    [InlineData("input2.text.Contains(\"x\")", false)]
    // Rejects: category equality (the old mock's style)
    [InlineData("input1.category == \"iam\"", false)]
    // Rejects: empty
    [InlineData("", false)]
    // Rejects: naked literal
    [InlineData("true", false)]
    public void IsValidPredicate_matches_DSL_grammar(string predicate, bool expected)
    {
        FoundryRuleAuthoringAgent.IsValidPredicate(predicate).Should().Be(expected);
    }

    // ============ happy path — 3 SHALL clauses -> 3 rules ============

    [Fact]
    public async Task HappyPath_emits_one_rule_per_normative_clause()
    {
        var json = """
        {
          "rules": [
            {
              "topicSlug": "IAM",
              "naturalLanguage": "Privileged access SHALL require MFA.",
              "predicate": "input1.text.ToLower().Contains(\"mfa\")",
              "remediation": "Add a clause requiring MFA for privileged access.",
              "sourceQuote": "Privileged access SHALL require MFA."
            },
            {
              "topicSlug": "NET",
              "naturalLanguage": "Public ingress SHALL traverse a WAF in prevention mode.",
              "predicate": "input1.text.ToLower().Contains(\"waf\") && (input1.text.ToLower().Contains(\"prevention\") || input1.text.ToLower().Contains(\"blocking\"))",
              "remediation": "State that ingress traverses a WAF configured in prevention (blocking) mode.",
              "sourceQuote": "All public ingress SHALL traverse a WAF in prevention mode."
            },
            {
              "topicSlug": "LOG",
              "naturalLanguage": "Audit logs SHALL be forwarded to a tamper-resistant SIEM.",
              "predicate": "input1.text.ToLower().Contains(\"siem\") || input1.text.ToLower().Contains(\"sentinel\")",
              "remediation": "Forward control-plane and data-plane audit logs to a SIEM (e.g. Sentinel).",
              "sourceQuote": "Audit logs SHALL be forwarded to a tamper-resistant SIEM."
            }
          ]
        }
        """;

        var agent = new FoundryRuleAuthoringAgent(new FakeChatClient(json), new DeterministicHashEmbedder());
        var result = await agent.AuthorAsync(RequestWith("Privileged access SHALL require MFA. Public ingress SHALL traverse a WAF. Audit logs SHALL be forwarded to a SIEM."));

        result.Should().HaveCount(3);
        result.Select(r => r.Rule.Metadata["topicSlug"]).Should().Equal("IAM", "LOG", "NET"); // ordered by topic slug
        result.All(r => r.Rule.Lambda.StartsWith("input1.text")).Should().BeTrue();
        result.All(r => r.Rule.Metadata["authoringAgent"] == "FoundryRuleAuthoringAgent").Should().BeTrue();
        result.All(r => !string.IsNullOrEmpty(r.Rule.EvidenceQuote)).Should().BeTrue();
    }

    // ============ predicate-shape rejection ============

    [Fact]
    public async Task BadPredicate_is_dropped_but_valid_siblings_survive()
    {
        var json = """
        {
          "rules": [
            {
              "topicSlug": "IAM",
              "naturalLanguage": "Good rule.",
              "predicate": "input1.text.Contains(\"mfa\")",
              "remediation": "keep",
              "sourceQuote": "Good."
            },
            {
              "topicSlug": "IAM",
              "naturalLanguage": "Bad rule with LINQ.",
              "predicate": "input1.text.Split(' ').Any(w => w == \"mfa\")",
              "remediation": "drop",
              "sourceQuote": "Bad."
            },
            {
              "topicSlug": "iam",
              "naturalLanguage": "Bad slug (lowercase).",
              "predicate": "input1.text.Contains(\"x\")",
              "remediation": "drop",
              "sourceQuote": "Also bad."
            }
          ]
        }
        """;

        var agent = new FoundryRuleAuthoringAgent(new FakeChatClient(json), new DeterministicHashEmbedder());
        var result = await agent.AuthorAsync(RequestWith("something"));

        result.Should().HaveCount(1);
        result[0].Rule.NaturalLanguage.Should().Be("Good rule.");
    }

    // ============ empty content short-circuit ============

    [Fact]
    public async Task EmptyContent_returns_empty_without_calling_LLM()
    {
        var chat = new FakeChatClient("SHOULD NOT BE READ");
        var agent = new FoundryRuleAuthoringAgent(chat, new DeterministicHashEmbedder());
        var result = await agent.AuthorAsync(RequestWith(""));

        result.Should().BeEmpty();
        chat.Calls.Should().Be(0);
    }

    // ============ malformed JSON handling ============

    [Fact]
    public async Task MalformedJson_returns_empty_and_does_not_throw()
    {
        var agent = new FoundryRuleAuthoringAgent(new FakeChatClient("not json at all {"), new DeterministicHashEmbedder());
        var result = await agent.AuthorAsync(RequestWith("Something SHALL happen."));
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyRulesArray_returns_empty()
    {
        var agent = new FoundryRuleAuthoringAgent(new FakeChatClient("""{"rules": []}"""), new DeterministicHashEmbedder());
        var result = await agent.AuthorAsync(RequestWith("Prose with no normative content."));
        result.Should().BeEmpty();
    }

    // ============ retry on transient 429 ============

    [Fact]
    public async Task Transient429_is_retried_and_eventually_succeeds()
    {
        var good = """
        {"rules":[{"topicSlug":"IAM","naturalLanguage":"n","predicate":"input1.text.Contains(\"x\")","remediation":"r","sourceQuote":"q"}]}
        """;
        var chat = new FakeChatClient(new Queue<Func<string>>(new Func<string>[]
        {
            () => throw new FakeThrottleException(429, "Too Many Requests"),
            () => throw new FakeThrottleException(503, "Service Unavailable"),
            () => good,
        }));
        var agent = new FoundryRuleAuthoringAgent(chat, new DeterministicHashEmbedder(), log: null, maxRetries: 3);
        var result = await agent.AuthorAsync(RequestWith("Something SHALL be done."));

        result.Should().HaveCount(1);
        chat.Calls.Should().Be(3);
    }

    [Fact]
    public async Task PermanentFailure_swallowed_returns_empty()
    {
        var chat = new FakeChatClient(new Queue<Func<string>>(new Func<string>[]
        {
            () => throw new InvalidOperationException("permanent 400 bad request"),
        }));
        var agent = new FoundryRuleAuthoringAgent(chat, new DeterministicHashEmbedder(), log: null, maxRetries: 3);
        var result = await agent.AuthorAsync(RequestWith("Something SHALL be done."));

        result.Should().BeEmpty();
        chat.Calls.Should().Be(1); // non-transient -> no retry
    }

    // ============ meta-governance skipping is prompt-driven ============
    // The agent trusts the model to skip. We verify the prompt contains the
    // right instruction so a regression would be visible in code review.

    [Fact]
    public void SystemPrompt_instructs_model_to_skip_meta_governance()
    {
        FoundryRuleAuthoringAgent.SystemPrompt.Should().Contain("SKIP meta-governance");
        FoundryRuleAuthoringAgent.SystemPrompt.Should().Contain("reviewed annually");
        FoundryRuleAuthoringAgent.SystemPrompt.Should().Contain("time-boxed");
    }

    [Fact]
    public void SystemPrompt_states_constrained_DSL()
    {
        FoundryRuleAuthoringAgent.SystemPrompt.Should().Contain("CONSTRAINED PREDICATE DSL");
        FoundryRuleAuthoringAgent.SystemPrompt.Should().Contain("input1.text.Contains");
        FoundryRuleAuthoringAgent.SystemPrompt.Should().Contain("input1.text.ToLower().Contains");
    }

    // ============ helpers ============

    /// <summary>
    /// Minimal IChatClient stub. Either returns a fixed response every time,
    /// or dequeues a scripted response (which may throw) per call.
    /// </summary>
    private sealed class FakeChatClient : IChatClient
    {
        private readonly string? _fixed;
        private readonly Queue<Func<string>>? _script;

        public int Calls { get; private set; }

        public FakeChatClient(string @fixed) { _fixed = @fixed; }
        public FakeChatClient(Queue<Func<string>> script) { _script = script; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var text = _fixed ?? _script!.Dequeue().Invoke();
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => EmptyStream();

        private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStream(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Fake Azure-style transient exception with a Status property.</summary>
    private sealed class FakeThrottleException : Exception
    {
        public int Status { get; }
        public FakeThrottleException(int status, string message) : base(message) { Status = status; }
    }
}
