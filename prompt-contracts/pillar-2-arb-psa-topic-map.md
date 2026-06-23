# Pillar 2 — ARB-PSA topic map (#117)

**Intent.** Author a topic map that classifies PSA sections into the 12 dimensions
the LLM baseline judged on (`out/analysis-llm.md`). Without this, the projector
labels PSA sections with the `contract` vocabulary and predicates like
`input1.category == "data_security"` never fire — every rule emits N/A.

**Inputs.**
- The 12 LLM dimensions: PSA Completeness, Architecture Constraints, Architecture
  Risks, Decision Records, Technology Standards, Design Patterns, Data Security,
  Integrations, Infrastructure Architecture, Security Architecture, Information
  Governance, DR & Resiliency.
- ARB-PSA template heading conventions (from the sample PDF).

**Outputs.**
- `src/LambdaRag.Projection/TopicMaps/arb-psa.v1.json` covering 12 primary topics
  + heading-based axis tags. Each topic has both heading keywords AND body
  keyword fallbacks per the plan.
- Loaded via `TopicMapRegistry.Load("arb-psa.v1")` automatically (registry
  auto-discovers embedded resources).

**Edge cases.**
- Headings like "DR & Resiliency" — keyword must tolerate `&` and `and`.
- "To be completed" placeholder text appears across many sections; topic map
  must not falsely promote those to high-confidence primary topics.
- Projector schema unchanged; only the topic vocabulary differs.

**Acceptance.**
- `TopicMapRegistry.Load("arb-psa.v1")` succeeds.
- A unit test asserts the map has ≥ 12 primary topics matching the 12 LLM dims.
- Projection determinism: same bytes + arb-psa.v1 → byte-identical projection.

Closes #117.
