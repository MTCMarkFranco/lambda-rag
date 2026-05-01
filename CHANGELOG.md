# Changelog

All notable changes to lambda-rag are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html) once
it reaches `1.0.0`.

## [Unreleased]

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
- `docs/findings/contoso-gap-analysis.md` — Contoso PAY-001 / DPA-001 gap
  investigation closing Phase 0 issue #6. Documents PAY-001 as an
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
