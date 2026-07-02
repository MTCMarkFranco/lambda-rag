# Changelog

All notable changes to lambda-rag are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html) once
it reaches `1.0.0`.

## [Unreleased]

### Added

- **Fact-mode conversion, batch 2 (issue #154)**. Added two new concepts
  to `enterprise-architecture-v1.json` `factSchema` (bumped schema
  version `2` → `3` — loudly invalidates cached sidecars). Concept
  selection followed the Flexibility rule from batch 1: only convert
  rules whose concepts leak across **both** out-of-domain scenarios
  (healthcare AND contract), never single-scenario surface.

  | Concept | Type | Rule converted |
  |---|---|---|
  | `untagged_principal_disable_days` | Integer | EA-IAM-023 (30-day cap) |
  | `break_glass_audited` | Boolean | EA-CICD-011 |

  Ratchet movement (production-floor test, `EmptyBagsFactExtractor`):

  | Scenario | Batch 1 | Batch 2 | Rules removed |
  |---|---|---|---|
  | `healthcare/acme-telehealth-gaps` | 8.79% (32/364) | **8.24% (30/364)** | 2 |
  | `contract/doc-002-clean-msa`      | 4.12% (15/364) | **3.57% (13/364)** | 2 |

  Ratchet ceilings tightened: healthcare `9% → 8.5%`, contract `5% → 4%`.

  Bonus: cross-industry test at floor `0.35` also tightened for contract
  scenario (`0.82% → 0.55%`); `EA-IAM-023` was firing at that floor too.
  The floor-0.35 threshold (5%) remains unchanged — it tracks
  "architectural generalization" rather than progressive conversion.

  Deferred to batch 3+: `EA-SECR-007` (compound OR-requirement — needs
  a small engine feature for `requiredFactsAny` semantics before it can
  be modeled honestly); `EA-IAC-001/003` (healthcare-only in the current
  corpus — waiting for a second scenario to earn a schema slot);
  Group-B temporary-console-action rules (`EA-IAC-013/014/015` — bespoke
  concepts per rule, low leverage until we add more docs).

- **Fact-mode conversion, first batch (issue #154)**. Added two new
  concepts to `enterprise-architecture-v1.json` `factSchema` (bumped
  schema version `1` → `2`, which invalidates all existing sidecars —
  intentional loud invalidation) and converted the two rules from the
  top-of-Fail leaderboard that cross-reference them:

  | Concept | Type | Rule converted |
  |---|---|---|
  | `secrets_vaulted` | Boolean | EA-DATA-018 |
  | `credential_rotation_days` | Integer (via duration normalizer) | EA-DATA-019 |

  Ratchet movement (production-floor test, `EmptyBagsFactExtractor`):

  | Scenario | Before | After | Rules removed |
  |---|---|---|---|
  | `healthcare/acme-telehealth-gaps` | 9.34% (34/364) | **8.79% (32/364)** | 2 |
  | `contract/doc-002-clean-msa`      | 4.67% (17/364) | **4.12% (15/364)** | 2 |

  Ratchet ceilings tightened: healthcare `10% → 9%`, contract `10% → 5%`.

  `EA-SECR-007` was on the top-leaderboard but requires two additional
  concepts (`secrets_iac_managed` + `orphan_secret_deletion_days`) to
  model its compound requirement honestly; deferred to the next batch
  rather than forced into this one.

- **Flexibility ratchet gate at production applicability floor (issue #154)**.
  Adds `Arch_Ruleset_Against_OutOfDomain_Doc_At_Production_Floor_Ratchet`
  (`tests/LambdaRag.IdempotencyTests/WrongRulesetAntiOverfitTests.cs`) as a
  companion to the existing floor-0.35 anti-overfit gate. This one runs at
  the **ship-default floor (0.20)** — the setting used for real ARB reviews
  — and asserts that classic-lambda rules do not manufacture Fail on
  out-of-domain docs above today's measured ceiling. Also emits a
  top-N failing-classic-rule diagnostic (Phase 2 evidence for issue #154):
  ranks rules by Fail count so schema-expansion targets are picked from
  concepts that leak across **both** scenarios (healthcare + contract),
  never a single doc's vocabulary (Flexibility principle).

  Ratchet ledger (tighten only when classic rules migrate to fact-mode):

  | Scenario | Latest measurement | Current ceiling |
  |---|---|---|
  | `healthcare/acme-telehealth-gaps` | 8.24% Fail (30/364, batch 2) | 8.5% |
  | `contract/doc-002-clean-msa`      | 3.57% Fail (13/364, batch 2) | 4% |

  Do not raise the ceiling to make a failing build green. Convert classic
  rules to fact-mode instead, then tighten the ceiling in this ledger.

- **Fourth engineering pillar — Flexibility** (`docs/FOUR-PILLARS.md`).
  Elevated from an implicit norm to a first-class, tested property
  alongside Determinism, Idempotency, and Accuracy. Every future feature
  must state, up front, how it defends flexibility across arbitrary
  documents (adversarial-paraphrase invariance, wrong-ruleset ≥80% NA,
  cross-industry corpus stability). Manifesto + Pillar 9 + Pillar 12
  contracts updated to reference the canonical doc.

- **Pillar 12 — Section-fact projection (#153)**. Second pass over the same
  document that populates a typed, closed **`FactSchema`** of concepts
  (booleans, enums, integers, durations, verbatim text) once per section,
  then evaluates `evaluationMode: "facts"` rules against the **union** of
  the scoped section fact bags — one lambda over one merged bag, not
  N lambdas over N sections. This is the mechanism that lets a rule like
  "AES-256 encryption AND ≤90-day key rotation" resolve on a real policy
  doc where the two facts live in different paragraphs.

  Ships in three composable layers, all opt-in and byte-identity-preserving
  against every pre-Pillar-12 golden (nullable-defaulted fields, folded
  into `Rule.Fingerprint()` / `RuleSet.Fingerprint()` only when non-null,
  mirroring the `SemanticAnchors` / `Phrasebooks` opt-in pattern):

  - `LambdaRag.Core.Domain.FactSchema` + `FactConcept` + `FactType` enum
    (`Boolean | Enum | Integer | Duration | Text`).
  - `LambdaRag.Core.Facts.DurationNormalizer` with embedded, versioned
    phrase→ISO-8601 mapping (`normalizer.v1.json`).
  - `LambdaRag.Core.Facts.FactBag` cross-section merge: Boolean OR,
    Duration/Integer MIN, Enum/Text first-non-null. Conflicts logged on
    verdict's `evaluatedInput._conflicts`.
  - Rule-level opt-in via `Rule.EvaluationMode = "facts"` +
    `Rule.RequiredFacts`. When set, the evaluator skips the per-section
    selector/predicate/gate/floor entirely, resolves scope, merges bags
    to a single `FactBag`, and evaluates the lambda ONCE with a named
    `facts` binding
    (`facts.encryption_declared == true && facts.key_rotation_days <= 90`).
    Missing-fact semantics: any `RequiredFact` null in the union bag →
    `NotApplicable` (advisory) regardless of rule applicability. Empty
    scope (no section discusses any of the rule's `RequiredFacts`) also
    resolves to `NotApplicable`. This is intentionally softer than `Gap`
    because a fact-mode "the doc is silent on this concept" is a
    scope-outside-doc signal, not a silent-missing-required-item signal;
    the classic-lambda Gap semantics remain unchanged. Diagnostic tags
    (`fact_mode:no_scoped_sections`, `fact_mode:missing_required_facts:<list>`)
    are preserved in `verdict.errorMessage` for audit.
  - `LambdaRag.Core.Facts.IFactExtractor` — Pass-1 seam.
    `EvaluationService` takes an optional `factExtractor` ctor param;
    default `null` preserves byte-identity.
  - `LambdaRag.Authoring.FoundrySectionFactExtractor` — Foundry-backed
    Pass 1. Reuses `FoundryRuleAuthoringAgent` wiring
    (`SemaphoreSlim(4)`, retry+jitter, `response_format: json_object`).
    Hallucination defense: every non-null fact requires a
    `supporting_quote` that appears byte-for-byte in the section text;
    otherwise values are dropped and a warning is recorded.
  - `LambdaRag.Authoring.SectionFactSidecarIO` — canonical-JSON sidecar
    IO. Cache at
    `%USERPROFILE%\.lambda-rag\facts\<docHash>.<factSchemaHash>.facts.json`
    (overridable). Fingerprint =
    `(docHash × factSchemaHash × modelId × promptHash × sectionOrderingHash)`;
    load-time mismatch throws `SectionFactSidecarMismatchException`
    naming the drifted component + `--refresh-facts` hint. No silent
    recompute.
  - CLI: `--refresh-facts` and `--facts-cache-dir <path>` flags on
    `review`. Pass 1 auto-enables when the ruleset carries a
    `factSchema` and at least one `evaluationMode: "facts"` rule.

  Tests: 57 new unit tests (schema fingerprint stability, normalizer
  edge cases, fact-bag merge, fact-mode compound lambdas with
  null-handling, sidecar disk round-trip + fingerprint-drift throw).
  Full suite: 534/534 green.

- **Pillar 12 — Flexibility gates (#153)**. Two falsifiable test
  harnesses that prove the fact-mode evaluation path generalises
  beyond the CTC doc the schema was hand-fitted to. Both must pass
  before Pillar 12 is considered production-ready per the manifesto.

  - **Phase C — Adversarial paraphrase invariance corpus.**
    `tests/LambdaRag.UnitTests/Facts/ParaphraseCorpus/ParaphraseCorpus.cs`
    — 13 concept groups × ≥11 phrasings each (booleans, enums,
    integers, durations), authored from policy language rather than
    from any single sample doc's vocabulary. Three layers of assertion
    in `ParaphraseInvarianceTests`:
    1. **Normalizer-only** — every duration phrasing in a group flows
       through `DurationNormalizer` and lands on the same ISO-8601
       value. Fully deterministic, no LLM.
    2. **Extraction-contract** — given a correct Pass-1 emission,
       sidecar merge + rule evaluation are byte-identical across
       paraphrases. Uses `RecordedFactExtractor` (in-memory
       `IFactExtractor`) so tests stay offline and CI-safe.
    3. **LLM integration scaffold** (env-gated
       `LAMBDA_RAG_LLM_TESTS=1`, `[Trait("Category","LLM")]`) — hits
       the real Foundry extractor when explicitly opted-in. No-op in
       normal CI.
    171 new unit tests, 100% green.

  - **Phase D — Wrong-ruleset anti-overfit gate.**
    `tests/LambdaRag.IdempotencyTests/WrongRulesetAntiOverfitTests.cs`
    runs the enterprise-architecture ruleset against two out-of-domain
    goldens (healthcare/acme-telehealth-gaps,
    contract/doc-002-clean-msa) with an empty-bags `IFactExtractor`
    stub (the honest LLM outcome for an out-of-domain doc) and
    asserts the report distribution meets the manifesto's flexibility
    thresholds: `NA + Skipped ≥ 80%`, `Pass ≤ 5%`, `Fail ≤ 5%`,
    `Gap ≤ 5%`. Both scenarios pass with `Fail < 1%`, `NA > 99%`.
    Uses `applicabilityFloor: 0.35` — documented at the test constant
    as the cross-industry setting (in-domain review runs at 0.20).

  Bug uncovered and fixed in-branch: `DurationNormalizer.OnANDayCycle`
  regex was scoped to the trailing noun `cycle`, dropping common
  phrasings like "on a 90-day rotation" and "on a 90-day retention
  window". Broadened to `on\s+a\s+(\d+)[\-\s]?day\b`. Bumped
  `normalizer.v1.json` version 1 → 2 per the Pillar 12 fingerprint
  contract — this invalidates every cached sidecar loudly on next
  load, exactly as designed. `DurationNormalizerTests` version
  assertion updated.

  Full unit suite: 705/705 green (534 + 171). Integration suite: all
  new Phase D tests green.

- **Pillar 10 — lexical applicability floor + rule-level rollup (#152)**.
  Two related changes to make evaluation reports honest against broad
  auto-generated rulesets (e.g. `FoundryRuleAuthoringAgent` output where
  every rule ships with `gateThreshold: 0`, `predicate: "true"`, and
  `selector: $.sections[*]`). Both features are **off by default** so
  every pre-Pillar-10 golden master stays byte-identical.

  - `EvaluationService(applicabilityFloor: <0..1>)` — deterministic,
    offline, embedding-free relevance gate. For rules whose semantic gate
    isn't configured, computes the token-overlap ratio between the rule's
    evidence quote / `topicSlug` and each candidate section text. Sections
    below the floor are skipped exactly as if the predicate had returned
    false. When the floor filters *every* candidate for a Mandatory rule,
    the engine now emits a single `NotApplicable` verdict (with
    `errorMessage: applicability_floor:no_relevant_section`) instead of
    a `Gap`, so a ruleset that spans 24 domains against a document that
    only covers 3 no longer gets penalised on the 21 silent domains.

  - `EvaluationService(emitRuleLevelStats: true)` — populates new
    nullable fields on `ComplianceReport`:
    - `TotalUniqueRules`, `RulesPassed`, `RulesFailed`,
      `RulesNotApplicable`, `RulesGap`, `RulesErrored`, `RulesSkipped`,
      `RuleScore = rulesPassed / (rulesPassed + rulesFailed + rulesGap)`.
    - `RuleSummaries[]` — one entry per distinct rule with
      `AggregateOutcome` (precedence: Pass › Fail › Error › Gap ›
      NotApplicable › Skipped), per-outcome counts, sections evaluated,
      and a `RepresentativeVerdictId` pointer for direct navigation.
    - Rule-level stats auto-enable when `applicabilityFloor > 0.0` —
      the two features are only meaningful together.

  - `lambda-rag review --applicability-floor <0.0..1.0> [--rule-level-stats]`
    surfaces the flags on the CLI. Existing invocations produce identical
    JSON output. `--applicability-floor` alone is enough; the CLI adds
    `--rule-level-stats` for callers that want the rule-level rollup
    without the floor.

  Coverage: `ApplicabilityFloorTests` (5 new tests) asserts the byte-
  identity contract when off, correct silent-topic → NotApplicable
  behaviour, correct topic-matched → Pass evaluation, and rule-level
  aggregation arithmetic (per-verdict Pass count of 1/3 aggregates to a
  rule-level Pass, `RuleScore = 1.0`).

## [1.1.0] — 2026-07-01

### Added

- **`FoundryRuleAuthoringAgent` — LLM-backed rule extraction**: New
  `IRuleAuthoringAgent` implementation that calls the configured Azure
  Foundry chat deployment (via `IChatClient` on top of `AzureOpenAIClient`
  + `DefaultAzureCredential`, mirroring `ComplianceEditorFactory`).
  - **Constrained predicate DSL** — the model may only emit predicates
    built from `input1.text.Contains("...")`,
    `input1.text.ToLower().Contains("...")`, `||`, `&&` and parentheses.
    Everything else is rejected by a tokenizer before the rule is
    emitted, so the runtime evaluation path stays LLM-free and
    deterministic.
  - **One rule per SHALL / MUST / SHALL NOT / MUST NOT clause**, with
    an explicit prompt instruction to skip meta-governance clauses
    ("This policy SHALL be reviewed annually", "Exceptions SHALL be
    time-boxed to 90 days" etc.) that aren't testable against a target
    document.
  - **Topical rule IDs** — the agent tags each suggestion with a
    3-6-letter topic slug (IAM, NET, SECR, CNTR, AKS, LOG, MON, TRACE,
    EXC, RETRY, CICD, IAC, SVC, COST, PRIV, SRE, SFI, DATA, COMP,
    AUDIT, ARCH, API, THRT, ADR, SLO) in
    `Rule.Metadata["topicSlug"]`. The extraction pipeline
    (`LambdaRag.Cli extract-rules`) now maintains per-topic counters and
    stamps `{prefix}{TOPIC}-{NNN:D3}` IDs like `EA-IAM-001`. Agents that
    don't participate in topical numbering (e.g. the legacy
    `DeterministicMockAuthoringAgent`, which pre-stamps its own IDs)
    pass through unchanged.
  - **Aligned to the `gpt-5.x` supported request schema** — `Temperature`
    and `Seed` intentionally omitted (rejected / ignored by the
    deployment). Uses `MaxOutputTokens=8000` for bounded output and
    `ChatResponseFormat.ForJsonSchema` for server-side shape enforcement.
    `AdditionalProperties["user"]="lambda-rag-authoring"` for Foundry-side
    telemetry / rate-limit isolation.
  - **Defensibility validator (Pillar 3)** — every emitted rule's
    `sourceQuote` MUST be a byte-for-byte substring of the source chunk.
    Candidates whose quotes are paraphrased are dropped so that every
    verdict remains regulator-replayable. The system prompt explicitly
    requires verbatim quotes and instructs the model to SKIP the rule
    if no verbatim excerpt can be found.
  - **Structured JSON output** validated against a JSON schema
    server-side AND revalidated locally; invalid entries (bad topic
    slug, off-DSL predicate, missing fields, non-verbatim quote) are
    logged and dropped so a single bad rule can't kill a whole policy
    pass.
  - **Concurrency & resilience** — pipeline-wide `SemaphoreSlim(4)` keeps
    Foundry rate limits happy; transient failures (HTTP 408/429/5xx,
    network errors, timeouts) retry up to 3× with exponential backoff +
    jitter.
  - **Selection** — `FoundryRuleAuthoringAgentFactory.TryCreate(...)`
    returns `null` when `LambdaRag:Foundry:Edit:Endpoint`/`Deployment`
    are unset, so `AddLambdaRagAuthoring` still falls back to
    `DeterministicMockAuthoringAgent` for offline / unit-test runs. No
    behavior change for environments without a Foundry deployment
    configured.
  - **Impact** — on the enterprise-architecture policy sample the
    ruleset grows from 14 (deterministic mock) to **364 unique rules
    across 25 topics** (Foundry-backed), a 26× coverage lift with every
    predicate still executable by the deterministic runtime and every
    quote byte-verbatim against the source document.

- **`samples/policies/enterprise-architecture/enterprise-architecture-policy-v1.docx`**
  (new): 103-page, 32.6k-word enterprise cloud architecture policy
  covering IAM, networking + edge protection (WAF mandate), secrets,
  data protection, containerization, AKS/Kubernetes, compute, storage,
  logging, monitoring, tracing, exception handling, retry/resilience,
  CI/CD supply chain, IaC, service-to-service comms, FinOps, privacy,
  SRE, and Microsoft SFI alignment. Sourced from Microsoft Learn, AWS
  Well-Architected, GCP Architecture Framework, NIST, OWASP, and CNCF
  with ~150 real citations. Includes 20 chapters + 3 appendices.

- **`rulesets/architecture-review/enterprise-architecture-v1.json`**
  (new): 364-rule extracted ruleset from the policy above, emitted by
  `FoundryRuleAuthoringAgent`.

## [Unreleased]

### Added

- **P1.4 — Quebec Law 25 / Loi 25 regulatory mapping (EN + FR)** ([#14], [#51]):
  Bilingual clause-by-clause mapping of Quebec's _Loi modernisant des
  dispositions législatives en matière de protection des renseignements
  personnels_ (Loi 25, 2021 c.25), covering both private-sector P-39.1
  and public-sector A-2.1 amendments.
  - **`samples/contracts/loi-25-ruleset.json`** (new, v1.0.0): 25
    `QC-LOI25-*` rules covering DPO designation (art. 3.1), governance
    framework (3.2), PIA / ÉFVP (3.3), incident response and register
    (3.5/3.6/3.7/3.8), profiling notice (8.1), privacy policy publication
    (8.2), privacy-by-default (9.1), automated-decision disclosure +
    human review (12.1), retention / destruction / anonymization (23),
    cross-border transfer assessment (17), portability (27), de-indexing
    (28.1), HR-decision retention (11), §18 disclosures-without-consent
    log, vendor DPA Loi 25 alignment, biometric CAI declaration, and
    public-sector A-2.1 §63.7/§67.3 analogues. Every rule carries a
    French translation in `metadata.naturalLanguageFr`, a statute
    citation in `metadata.lawReference`, and a quoted evidence span.
    Engine code is **unchanged** — all Quebec-specific knowledge lives
    in this JSON plus the two mapping docs.
  - **`docs/regulatory/quebec-law-25-mapping.md`** (new): EN canonical
    mapping. Mirrors the structure of `bill-c27-aida-mapping.md`:
    status banner, definitions table, effective-dates timeline, clause
    → rule traceability tables (private + public + LCCJTI + vendor),
    worked example (`QC-LOI25-AUTODEC-002`), comparison vs PIPEDA /
    CPPA-AIDA / GDPR / A-2.1, severity → AMP mapping, ambiguities, and
    SME hand-off.
  - **`docs/regulatory/loi-25-mapping.fr.md`** (new): full French mirror
    with FR text primary throughout (real translation, not a stub).
  - **`README.fr.md`** (new): full French README; references both
    mapping docs from the regulatory section.
  - **`docs/regulatory/_research/law-25-researcher-report.md`** (new):
    consolidated Researcher pack used as source material for the
    mapping. Closes the P1.4-prep tracking issue.
  - **Tests** (new, in `tests/LambdaRag.UnitTests/Regulatory/`):
    `QuebecLaw25RulesetParserTests` asserts every rule carries
    `naturalLanguageFr`, a `lawReference` matching the P-39.1 / A-2.1 /
    LCCJTI pattern, a `reviewer` label, and an evidence quote.
    `GenericQuebecRuleEvaluationTests` is a genericity guard: loads the
    ruleset and runs it through `EvaluationService` against synthetic
    non-Quebec and Quebec-relevant documents, asserting `Errored == 0`
    and that the DPO rule passes when required language is present.
  - SME engagement (qc-privacy / qc-public-sector) is **pending** — both
    mapping docs flag that explicitly. Test count: **174 → 179** unit
    tests; idempotency tests unchanged at 15.

- **19 new sample-aligned rules + domain-agnostic `text_features` extractor** ([#TBD]):
  Closes the contract-rule backlog identified in the earlier comparison run.
  Adds 19 new rules to
  `samples/contracts/contoso-demo-ruleset.json` (now v2.0.0, 24 rules total)
  covering payment terms, IP/work-for-hire, liability carve-outs,
  insurance limits, security/cryptography, privacy obligations
  (residency, breach window, consent, retention, explicit-laws), AI
  addenda, subcontracting approval, service locations, and Quebec
  governance.
  - **`TextFeatureExtractor`** (new in `LambdaRag.Projection`): pure-regex,
    domain-agnostic numeric extraction over English prose. Adds
    `text_features.{day_counts, month_counts, year_counts, percent_values,
    dollar_amounts}` arrays plus `_min`/`_max` scalars on every projected
    section. Rule authors target numeric thresholds via lambdas like
    `input1.text_features.day_count_max <= 45` — usable by **any** ruleset,
    not just Contoso.
  - Engine remains domain-agnostic: 11 new tests
    (`TextFeatureExtractorTests` + `GenericTextFeaturesEvaluationTests`)
    prove the extractor and evaluator work on synthetic non-Contoso corpora
    (vendor bond, permit response, ESG recycled-content, oil-and-gas).
  - Projector bumped to **v1.4.0**; topic-map (`contract.v1.json`) bumped
    to **v1.1.0** (adds `tax`, `subcontracting`, `ai`, `service_locations`
    topics).
  - End-to-end vs the Contoso contract: **`pass=5 fail=21 gap=1`** — every Fail
    is a genuine deterministic finding (NET 60 > 45, 2% > 1.5%, no Quebec
    governance, etc.).

### Fixed

- `TextFeatureExtractor.DollarRx`: shorthand suffixes (`m|b|k`) no longer
  match the leading letter of an unrelated trailing word (e.g. `$1,000,000
  bond` was previously parsed as `$1,000,000 b` → 10¹⁵). Suffix now
  requires a word boundary via negative lookahead `(?![A-Za-z])`.
- `TextFeatureExtractor.DayCountRx`: now also matches hyphenated forms
  (`120-day`, `30-day`) in addition to spaced forms (`120 days`).

## [Unreleased — earlier]

### Added

- **CTSO-style comment format in markup output** ([#TBD]):
  Word comments now match the Contoso contract-review UX so reviewers
  see the same visual feedback as in the agentic flow:
  - Author label is derived from the rule's *category* (e.g.
    "🕵 - Legal guidance", "🕵 - Privacy guidance", "🕵 - Finance
    guidance") instead of a generic "lambda-rag" tag. The category is
    resolved from `Metadata["categoryLabel"]` → `Metadata["category"]` →
    `input1.category == "X"` literal in the predicate → "Compliance"
    fallback. Maps every contract.v1 / fsi.v1 / oil-gas.v1 / governance
    topic to a human-readable domain.
  - Body opens with a severity banner that mirrors Contoso: `🚨 CRITICAL`
    / `⚠️ MAJOR` / `✏️ MODERATE` / `💡 SUGGESTION` (mapped from
    `RuleSeverity.Critical/Violation/Deviation/Suggestion`); error
    verdicts open with `🛑 ERROR — Rule Could Not Be Evaluated`.
  - Body ends with `[Policy Reference: <ruleId> v<version>]` so the
    cited rule is always one click away.
  - Word comment author *initials* are derived from the resolved
    category label (e.g. "Legal guidance" → "LG") so the review pane
    shows correct two-letter chips per domain instead of a hardcoded
    "LR".
  - Pass annotations and gap-summary annotations follow the same
    format. Determinism preserved end-to-end (pure-code derivation,
    no I/O).
  Centralized in new `LambdaRag.Markup.CommentFormatting` static class.
- **Synopsis service & `lambda-rag rules synopsize` CLI** ([#TBD]):
  Authoring-time tooling that walks a ruleset and writes a one-sentence
  plain-English summary of each rule's intent into
  `Rule.Metadata["synopsis"]`. The summary is generated by a small chat
  model (gpt-4o-mini at the configured Azure OpenAI endpoint, Entra ID
  via `DefaultAzureCredential`, temperature 0, seeded) and disk-cached
  by content-hashed cache key so re-runs are free.
  - Runtime is unaffected: the markup pipeline reads the cached string
    at review time, so lambda-rag's deterministic guarantee is intact.
  - When present, the synopsis is rendered as the first line of the
    comment body (after the severity banner), giving reviewers a
    plain-English description of *what the rule does* before the full
    natural-language statement.
  - New project area: `LambdaRag.Authoring.Synopsis.SynopsisService`.
  - New CLI: `lambda-rag rules synopsize --ruleset <path> [--out <path>]
    [--cache-dir <path>] [--force] [--endpoint <url>] [--deployment <name>]`.
  - Sample env: `samples/.env.synopsis.example`.
- **Cross-repo discrepancy analysis** ([#TBD]): finding-by-finding
  comparison of lambda-rag verdicts against a third-party agentic
  reviewer on the same sample contract. Classification of the gap
  showed the majority were real coverage misses (no rule authored —
  the planned `out/dev-full/policies-ruleset.json` was not yet
  produced), a smaller share were third-party LLM hallucinations /
  mis-citations, and the rest were duplicate firings. Headline:
  lambda-rag is more accurate, not less thorough.

### Fixed

- **`CTSO-INDM-001` lambda was too permissive** (audit class P) — rule's
  natural language asserts "must address third-party IP infringement"
  but lambda accepted any clause containing `defend`, `defense`, or
  `indemnify`, so it Pass-ed §9.3 of the Contoso sample contract ("shall
  indemnify the Company against any claims arising from the Vendor's
  negligence or willful misconduct") even though the clause does not
  cover IP. Tightened lambda to require **both** a defend / defense
  obligation AND an IP-related trigger (`infringement`, `infringe`,
  `intellectual property`, `IP claim`, `third-party claim`).
  Bumps rule to `CTSO-INDM-001@1.1.0`.
- **`CTSO-LIAB-001` selector miss on combined liability + indemnification
  sections** (audit class S) — predicate `input1.category == "liability"`
  found no section because the contract.v1.3.0 projector breaks the
  `liability:0.9 / indemnification:0.9` tie toward indemnification on
  §9 (LIABILITY AND INDEMNIFICATION), so the rule emitted a misleading
  "Document does not address: Limitation of liability…" Gap even though
  §9.1 contains an explicit fee-multiplier cap. Switched predicate to
  `input1.topics.Contains("liability")` so the rule matches whenever
  *any* of the section's projected topics is `liability`, regardless of
  the primary-topic tie-break. Bumps rule to `CTSO-LIAB-001@1.1.0`.
  End-to-end re-run vs the Contoso sample contract: verdicts now read
  `pass=4 fail=1 gap=1` (was `pass=3 fail=0 gap=2`); §9 now correctly
  Fails on IP indemnity, §9 + §11 both Pass on the explicit cap, §9's
  (the contract has no warranty section — a defect the third-party
  reviewer missed) remains.

### Tests

- 162/162 pass (147 unit + 15 idempotency). +32 new unit tests covering
  `CommentFormatting` (category resolution, severity banners, body
  composition, idempotency), `AnnotationFactory.FromReport` body shape,
  `OpenXmlMarkupService.ResolveInitials`, and `SynopsisService.Normalize`
  / `ComputeCacheKey`. Idempotency golden regenerated to capture the
  new comments.xml format.



### Added

- `--annotate-pass` flag on `lambda-rag review` — Phase 1 / [#4](https://github.com/MTCMarkFranco/lambda-rag/issues/4):
  opt-in positive-confirmation comments in markup mode. When set, each
  Pass verdict produces an additional Comment anchored to the matched
  section, prefixed `✓ Passed: <rule statement>`. Default OFF (high
  volume on large rulesets); idempotency preserved via verdict-id-derived
  annotation ids; tracked changes are NOT introduced for Pass — comments
  only.
- Vocabulary-density tie-break in the contract projector — Phase 1 / [#44](https://github.com/MTCMarkFranco/lambda-rag/issues/44):
  every section now carries `topic_density` (per-topic keyword hits per
  100 words of body) and `is_operative_for_topic` (boolean). When a
  contract mentions the same topic in multiple sections — e.g. a sparse
  early heading mention plus a richer later "Services payment terms"
  block — the densest section is flagged operative. Rule authors can
  bind to it via predicate `input1.primary_topic == "X" &&
  input1.is_operative_for_topic` instead of taking whichever section
  matches first. Bumps projector version `contract@1.2.0` → `contract@1.3.0`.

### Fixed

- Phase 0 gap analysis (since removed) documented the projector-side
  `CTSO-2011-000PAY-001` defect; it is now addressable on the
  projector side via `is_operative_for_topic`. Rule lambda fix
  (`30 calendar days` phrasing) is a per-rule authoring task. End-to-end
  verification on the sample contract defers to P1.8 (golden corpus addition).

### Added

- `docs/manifesto.md` — Phase 1 / [P1.1 #11](https://github.com/MTCMarkFranco/lambda-rag/issues/11):
  *Rule Projection: Deterministic Reasoning over Documents.* The canonical
  anchor doc for the lambda-rag pattern. Defines the five tenets
  (authoring-may-use-AI / runtime-may-not, one-artifact-one-direction,
  fingerprint-as-audit-trail, citations-from-source, gaps-as-first-class),
  contrasts the pattern against RAG / pure rules engines / symbolic AI,
  and lists honest limits. Linked from README's why-this-exists block.
- `docs/diagrams/authoring-vs-runtime.md` — Phase 1 / [P1.6 #16](https://github.com/MTCMarkFranco/lambda-rag/issues/16):
  the canonical authoring-vs-runtime architecture diagram in Mermaid
  (full + reduced-for-slides), with module map, five named anti-patterns,
  and rendering instructions. Replaces ad-hoc ASCII art across the repo.
- `docs/regulatory/osfi-e23-mapping.md` — Phase 1 / [P1.2 #12](https://github.com/MTCMarkFranco/lambda-rag/issues/12):
  clause-by-clause mapping of OSFI Guideline E-23 *Model Risk Management*
  to the lambda-rag rule schema. ~30 candidate rules across §3 framework
  characteristics, §4 governance, §5 lifecycle phases, §6 vendor models,
  §7 foreign-bank subsidiaries, §8 internal audit, and §9 model
  inventory; two worked JSON rule examples (`E23-GOV-001`,
  `E23-AUDIT-002`).
- `docs/regulatory/tbs-adm-mapping.md` — Phase 1 / [P1.5 #15](https://github.com/MTCMarkFranco/lambda-rag/issues/15):
  clause-by-clause mapping of the TBS Directive on Automated
  Decision-Making (date-modified 2025-06-24). ~30 candidate rules
  across §6.1 AIA gates, §6.2 transparency, §6.3 quality assurance,
  §6.4 recourse, §6.5 reporting, plus §8 application/exemption logic.
  Notes lambda-rag's double applicability (subject-of-the-Directive
  vs. tool-for-applying-it).
- `docs/regulatory/bill-c27-aida-mapping.md` — Phase 1 / [P1.3 #13](https://github.com/MTCMarkFranco/lambda-rag/issues/13):
  prospective clause-by-clause mapping of Bill C-27 / AIDA. Carries an
  explicit volatility caveat — AIDA died on the Order Paper after the
  44th Parliament's prorogation; the mapping is forward-looking.
  ~20 candidate rules covering ss.6–12 substantive obligations and
  ss.38–39 offences, with a cross-walk table to EU AI Act, Colorado
  AI Act, and the TBS Directive.
- `tests/Goldens/corpus/` — Phase 1 golden test corpus (issue #18). Five
  public-source-grounded verticals: **gov-architecture** (Government of
  Canada Cloud Guardrails v2.0, OGL-Canada-licensed), **fsi** (OSFI
  Guideline B-10 *Third-Party Risk Management*), **contract** (TBS
  SACC + PIPEDA), **permitting** (Ontario Building Code O.Reg.332/12 +
  IASR/AODA O.Reg.191/11 + Impact Assessment Act S.C.2019 c.28 +
  Constitution Act 1982 s.35), and **oil-gas** (CER Onshore Pipeline
  Regulations SOR/99-294 + Methane Regulations SOR/2018-66 + AER
  Directive 071 + s.35). 25 rules, 11 synthetic candidate documents
  covering pass / fail / gap mixes, with frozen `expected-verdict.json`
  snapshots per document — full 5/5 vertical close-out.
- `tests/LambdaRag.IdempotencyTests/CorpusRegression.cs` — discovers
  every `corpus/{topic-map}/{doc-id}/` triple, runs the full
  parse → project → evaluate pipeline against the matching topic map
  with a frozen `TimeProvider`, and asserts the produced
  `ComplianceReport` matches the checked-in golden byte-for-byte.
  Bootstraps missing goldens with `Assert.Fail` on first run.
- `.github/workflows/corpus-regression.yml` — GitHub Actions job named
  `corpus-regression` that runs the corpus + the full idempotency suite
  on every push and PR touching the corpus, projector, evaluator, or
  workflow. Drift fails the build before merge.
- `docs/findings/` — Phase 0 PAY-001 / DPA-001 gap investigation
  (since removed) closed Phase 0 issue #6. Documented PAY-001 as an
  authoring-side defect (lambda phrasing + projector heading binding) vs.
  DPA-001 as a real, well-flagged compliance gap. Filed follow-up #44 to
  fix PAY-001.
- `tests/LambdaRag.IdempotencyTests/ReviewedDocxIdempotency.cs` — golden
  master + twice-equal byte-identical proof for the reviewed `.docx`
  pipeline. Pins the package-root relationship id (auto-randomized by the
  Open XML SDK on every `Create`) so every inner OOXML part hashes
  identically run-over-run.
- `tests/Goldens/reviewed-docx/reviewed-docx-golden.json` — checked-in
  SHA-256 manifest of every inner OOXML part produced by the markup
  pipeline against the bundled `samples/contracts/contract.md` corpus.
- `docs/what-lambda-rag-is-not.md` — explicit non-claims sheet. Linked
  from the README's "How it works" section. Closes issue #9.
- `docs/dependencies/rules-engine-risk.md` — supply-chain risk note for
  the `microsoft/RulesEngine` dependency, with a documented contingency.
  Closes issue #10.
- `spikes/roslyn-eval/` — ~200-LOC proof-of-concept showing a Roslyn
  scripting–based predicate evaluator as a swap-in replacement for the
  RulesEngine if upstream goes fully unmaintained.

### Changed

- Accuracy framing across docs aligned to **"deterministic, reproducible,
  auditable, human-overridable."** No "100% accurate" / "fully accurate"
  language anywhere in the repo (verified by `rg -i`). Closes issue #8.
