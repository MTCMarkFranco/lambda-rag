# Changelog

All notable changes to lambda-rag are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html) once
it reaches `1.0.0`.

## [Unreleased]

### Added

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
