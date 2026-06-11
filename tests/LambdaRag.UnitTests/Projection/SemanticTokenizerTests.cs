using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Projection;
using Xunit;

namespace LambdaRag.UnitTests.Projection;

/// <summary>
/// Pillar 6 (#124) — determinism + correctness tests for
/// <see cref="SemanticTokenizer"/>. The tokenizer is part of the
/// projection artifact contract; drift here means cached projections
/// silently disagree with new runs.
/// </summary>
public class SemanticTokenizerTests
{
    private static readonly string[] FixtureChunks =
    {
        "DR & Resiliency: We target a 4 hour recovery point objective and 30 minute recovery time objective.",
        "Security Architecture aligns to NIST CSF v2.0 and PCI DSS standards with documented MVS controls.",
        "Architecture risks include: lack of failover for primary database, weak monitoring, unclear runbooks.",
        "The PSA must be completed for ARB-2 review. Sections without owners will be sent back.",
        "Integrations use the corporate API gateway hub, with async messaging for high-volume flows.",
        "Decision records capture the vendor selection rationale, status, and any deferred technical debt.",
        "Data security: data at rest is encrypted via platform-managed keys; in transit via TLS 1.2+.",
        "Infrastructure architecture documents three Terraform modules deployed across two Azure regions.",
        "Information governance: PIA and IG questionnaire are linked; data classification matrix attached.",
        "Design patterns referenced: DP-001, DP-005, DP-006, DP-008, DP-009.",
        "Failover design uses warm-standby in a paired region with continuous geo-replication.",
        "Architecture constraints: ACE must remain operational on Day 1; new platform must coexist.",
        "Risk owner: Jane Smith. Mitigation: deploy additional monitoring before cutover. Severity: high.",
        "Technology standards align with the approved STD-001 catalog status: Approved as of 2024-Q3.",
        "The recovery point objective is 1 hour for the production tier and 4 hours for analytics.",
        "Recovery time objective for tier-1 services is 30 minutes, including database failover.",
        "Zero-trust ingress is enforced via the SaaS federation pattern at the API hub.",
        "Encryption at rest uses customer-managed keys stored in Azure Key Vault.",
        "Authentication is federated via Entra ID with conditional access enforcing MFA for all admins.",
        "Authorization follows the principle of least privilege using role-based access control.",
        "Business impact analysis: a 30-minute outage of tier-1 = $250,000 in lost revenue.",
        "Single point of failure analysis: load balancer is HA; database has automatic failover.",
        "Operational constraints include the Day-1 ACE coexistence and the existing IAM dependency.",
        "Privacy contact: privacy@example.com. PIA reference: PIA-2024-0123.",
        "Data classification: Confidential (Customer PII); retention 7 years per IG policy.",
        "Network architecture: three subnets in a hub-spoke topology across two Azure regions.",
        "IaC modules required: terraform-azure-vnet, terraform-azure-aks, bicep-keyvault.",
        "Standards alignment: NIST CSF v2.0 functions IDENTIFY, PROTECT, DETECT, RESPOND, RECOVER.",
        "PCI DSS scope: cardholder data environment is segmented behind dedicated firewall rules.",
        "MVS minimum viable security baseline applies to all internet-facing endpoints.",
        "Risk: vendor lock-in to a single cloud provider. Severity: medium. Mitigation: abstraction layer.",
        "Risk: insufficient observability of background jobs. Severity: low. Owner: SRE team.",
        "Decision: adopt Kafka over RabbitMQ. Rationale: throughput at p99 = 50k msg/s.",
        "Decision: defer multi-region active-active to Q4. Status: deferred. Owner: VP Engineering.",
        "Pattern DP-009 (SaaS federation) applies to all external identity provider integrations.",
        "Pattern DP-001 (API gateway) is the canonical entry point for partner traffic.",
        "Architecture risks table: 12 entries; each row has severity + mitigation + owner columns populated.",
        "Constraints: budget capped at $2.5M; go-live by end of Q3; no breaking API changes.",
        "Service level agreement: 99.95% uptime per tier-1 service, measured monthly.",
        "Failover testing performed quarterly via the documented runbook RB-DR-001.",
        "Disaster recovery design includes a hot standby in the secondary region with 5-minute RPO.",
        "Backup strategy: hourly incrementals, nightly fulls, 30-day retention; tested monthly.",
        "Section heading: DR & Resiliency. Content discusses RTO, RPO, failover, and business impact.",
        "Section heading: Integrations. Content lists 14 source/target pairs with patterns and schedules.",
        "Section heading: Decision Records. Captures 8 ADRs with full rationale and status fields.",
        "Section heading: Architecture Risks. Documents 12 risks with severity, mitigation, owner.",
        "Section heading: Architecture Constraints. Captures Day-1 ACE coexistence requirement.",
        "Section heading: Technology Standards. References STD-001..STD-014 from approved catalog.",
        "Section heading: Design Patterns. References DP-001, DP-005, DP-006, DP-008, DP-009.",
        "Section heading: Information Governance. PIA + IG questionnaire are attached.",
        "Section heading: Security Architecture. NIST/PCI alignment with MVS gap analysis.",
    };

    [Fact]
    public void Tokenizer_version_is_pinned_and_non_empty()
    {
        SemanticTokenizer.TokenizerVersion.Should().NotBeNullOrWhiteSpace();
        SemanticTokenizer.StopwordHash.Should()
            .NotBeNullOrWhiteSpace()
            .And.HaveLength(64, "SHA-256 hex");
    }

    [Fact]
    public void Stopwords_contain_baseline_english_terms()
    {
        SemanticTokenizer.Stopwords.Should().Contain(new[] { "the", "and", "of", "is", "for" });
        SemanticTokenizer.Stopwords.Count.Should().BeGreaterThan(100);
    }

    [Fact]
    public void Tokenizer_is_byte_identical_across_100_runs_for_50_fixture_chunks()
    {
        // Determinism gate: 50 chunks × 100 runs ⇒ identical canonical
        // token sequences (text, ngram, charStart, charLength).
        var baseline = FixtureChunks.Select(c => Canonical(SemanticTokenizer.Tokenize(c))).ToList();
        baseline.Should().HaveCount(FixtureChunks.Length).And.HaveCountGreaterThanOrEqualTo(50);

        for (var run = 0; run < 100; run++)
        {
            for (var i = 0; i < FixtureChunks.Length; i++)
            {
                var current = Canonical(SemanticTokenizer.Tokenize(FixtureChunks[i]));
                current.Should().Be(baseline[i],
                    $"run {run + 1}/100 chunk #{i} drifted — tokenizer is non-deterministic");
            }
        }
    }

    [Fact]
    public void Tokens_have_non_negative_spans_and_match_lowercased_surface()
    {
        foreach (var chunk in FixtureChunks)
        {
            var tokens = SemanticTokenizer.Tokenize(chunk);
            foreach (var t in tokens)
            {
                t.CharStart.Should().BeGreaterThanOrEqualTo(0);
                t.CharLength.Should().BeGreaterThan(0);
                (t.CharStart + t.CharLength).Should().BeLessThanOrEqualTo(chunk.Length);
                t.Text.Should().Be(t.Text.ToLowerInvariant(), "tokens must be lowercased");
                t.Ngram.Should().BeInRange(1, 3);
            }
        }
    }

    [Fact]
    public void Stopwords_are_dropped()
    {
        var tokens = SemanticTokenizer.Tokenize("The cat sat on the mat under the window of the kitchen.");
        var unigrams = tokens.Where(t => t.Ngram == 1).Select(t => t.Text).ToHashSet();
        unigrams.Should().NotContain("the").And.NotContain("on").And.NotContain("of");
        unigrams.Should().Contain("cat").And.Contain("mat").And.Contain("kitchen");
    }

    [Fact]
    public void Bigrams_do_not_cross_sentence_boundaries()
    {
        var tokens = SemanticTokenizer.Tokenize("Failover design. Recovery point objective is 4 hours.");
        var bigrams = tokens.Where(t => t.Ngram == 2).Select(t => t.Text).ToList();
        bigrams.Should().NotContain("design recovery");
        bigrams.Should().Contain(b => b == "recovery point" || b == "point objective");
    }

    [Fact]
    public void Trigram_opt_in_emits_3grams_only_when_requested()
    {
        var defaultTokens = SemanticTokenizer.Tokenize("recovery point objective standard alignment");
        defaultTokens.Should().NotContain(t => t.Ngram == 3);

        var withTri = SemanticTokenizer.Tokenize(
            "recovery point objective standard alignment",
            new[] { 1, 2, 3 });
        withTri.Should().Contain(t => t.Ngram == 3);
    }

    [Fact]
    public void Token_cap_enforced_at_max_per_section()
    {
        // Build a wide chunk and verify cap.
        var huge = string.Join(" ", Enumerable.Range(1, 800).Select(i => $"keyword{i:D4}"));
        var tokens = SemanticTokenizer.Tokenize(huge);
        tokens.Count.Should().BeLessThanOrEqualTo(SemanticTokenizer.MaxTokensPerSection);
    }

    private static string Canonical(IReadOnlyList<TokenEmbedding> tokens) =>
        string.Join("\n", tokens.Select(t => $"{t.Ngram}|{t.CharStart}|{t.CharLength}|{t.Text}"));
}
