# Pillar 5 — Completeness vs Template detector (#120)

**Intent.** Match the LLM's strongest discriminator on the PSA review: detecting
sections that exist but contain placeholder / TBD text ("section exists but is
still TBD"). The rules engine had no equivalent before this pillar.

**Inputs.** Section body text (passed by the projector).

**Outputs.**
- `LambdaPrimitives.IsTemplateBoilerplate(text)` (shipped as part of Pillar 3
  primitives — see `pillar-3-semantic-predicates.md`). Returns true when:
  1. Any verbatim placeholder phrase from the signed list appears, OR
  2. Placeholder phrases cumulatively cover ≥ 30% of the section's characters.
- Signed phrase list `LambdaPrimitives.BoilerplatePhrases` — order-irrelevant,
  case-insensitive, part of the binary contract (changing it bumps the
  primitive's binary version).
- Used in every Pillar-4 ARB-PSA rule that gates on section presence
  (`ARB-PSA-COMPLETENESS-001`, `ARB-PSA-RISKS-001`, `ARB-PSA-DATA-SEC-001`,
  `ARB-PSA-INFRA-001`, etc.).

**Edge cases.**
- Very short sections (< 20 chars) skip the density check — pure noise floor.
- "TBD" inside a long paragraph still triggers (verbatim hit beats density).
- Sections that look like code/JSON are unaffected — placeholder phrases are
  prose-only.

**Acceptance.** Tests in `tests/LambdaRag.UnitTests/Evaluation/LambdaPrimitivesTests.cs`
cover both trigger modes plus negative cases (well-formed sections do not trip).
Phrase list determinism (byte-identical list across two reads) tested too.

Closes #120.
