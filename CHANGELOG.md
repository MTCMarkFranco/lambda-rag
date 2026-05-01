# Changelog

All notable changes to lambda-rag are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html) once
it reaches `1.0.0`.

## [Unreleased]

### Added

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
- `docs/findings/ac-gap-analysis.md` — Air Canada PAY-001 / DPA-001 gap
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
