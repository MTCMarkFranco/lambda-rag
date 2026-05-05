# Spike: AI Search authoring pivot (issue #72)

This spike validates the **prompt + schema design** for the Azure AI
Search "GenAI prompt skill" before we commit to the full skillset
deployment. It runs the rule-extraction prompt against real ARB policy
chunks via the existing `IChatClient` Foundry stack and checks the
output against `samples/authoring/rule-extraction.schema.json`.

## Why a prompt-level spike first

The highest-risk piece of #72 is **does the LLM emit usable, schema-
valid rule tuples?** Everything else (Bicep for the index, indexer
configuration, ruleset pull CLI) is conventional plumbing. If the prompt
yields garbage, the rest is wasted work.

Running the prompt against the existing chat client is functionally
equivalent to running it inside an AI Search GenAI prompt skill — the
skill is a thin wrapper around the same model deployments. Once the
prompt design is locked, we'll wire the skillset (subsequent commits in
the same PR).

## What this spike does

1. Loads `policies\arb\*.md` (the per-policy markdown chunks split from
   the ARB `policies.json`).
2. For each chunk, calls the chat client with:
   - System message = `samples/authoring/rule-extraction.system-prompt.md`
   - User message  = `{ domain, documentId, chunkOrdinal, headingPath, chunk }`
3. Parses the response and validates against
   `samples/authoring/rule-extraction.schema.json`.
4. Writes results to `out/spike-72/<chunk-id>.json`.
5. Diffs the synthesised rule against the corresponding hand-authored
   rule in `samples/contracts/arb-ruleset.json` and writes a side-by-side
   markdown report to `out/spike-72/comparison.md`.

## Acceptance bar (for the spike, not for #72 overall)

- ≥ 18 of 20 ARB policy chunks produce a schema-valid rule on the first
  pass.
- Concepts list overlaps the hand-authored concept list by ≥ 2 phrases
  per rule (semantic, not exact-string — measured via Foundry embedding
  cosine ≥ 0.7).
- Generated remediation is at least as specific as the hand-authored
  one (manual review).

If we hit those bars, we proceed to wire the AI Search skillset and
indexer. If not, we iterate on the prompt.

## How to run

```powershell
# Requires Foundry config in user-secrets (already set up — see #67).
cd C:\Projects\lambda-rag
dotnet run --project spikes\72-ai-search-authoring\Spike.csproj
```

Outputs land in `out\spike-72\`.

## Status

- [x] Schema + system prompt drafted
- [ ] Spike harness implementation
- [ ] First end-to-end run against `policies\arb\*.md`
- [ ] Comparison report committed to `docs/findings/`
- [ ] Decision recorded in #72: proceed to skillset deployment, or iterate
