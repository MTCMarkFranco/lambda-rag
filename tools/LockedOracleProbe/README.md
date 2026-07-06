# Locked Oracle Probe — Phase 0

**Issue:** [#175](https://github.com/MTCMarkFranco/lambda-rag/issues/175) — Locked Oracle Pattern spec.  
**Research basis:** [FID-Lottery paper analysis](../../.copilot/session-state/2fc9c260-90ba-4861-a84a-3b50d2675423/files/fid-lottery-analysis.md) (arXiv 2606.20536).

## Purpose

Phase 0 empirical probe: does an Azure OpenAI deployment return **byte-identical** structured facts across N=100 independent calls with the same prompt at `temperature=0` + pinned seed? This is the gate that decides whether the Locked Oracle Pattern is worth building.

The paper measured diffusion sampling hardware drift at σ=0.047 (below the sampling floor). We do not know if LLM autoregressive inference has the same drift profile. This probe answers that empirically.

## Verdict thresholds (from #175)

| Canonical-JSON identity | Verdict | Meaning |
|---|---|---|
| ≥ 99% | **GREEN** | Locked Oracle justified as-is; proceed to Phase 1 |
| 95–98% | **AMBER** | Viable *only* with N=3 majority-vote fallback for all rules; spec update needed |
| < 95% | **RED** | 99% relaxation insufficient; the LLM inference drift profile is larger than the paper's diffusion analog. Close #175 negative, or try a different backend |

Exit codes: GREEN=0, AMBER=10, RED=20.

## Prerequisites

1. **Azure OpenAI deployment** — any chat model that supports `temperature=0` and JSON response format. `gpt-4o-mini` is fine.
2. **Entra ID auth** — the tool uses `DefaultAzureCredential`. Run `az login` first.
3. **Env vars**:
   ```powershell
   $env:AZURE_OPENAI_ENDPOINT   = "https://<your-foundry>.cognitiveservices.azure.com/"
   $env:AZURE_OPENAI_DEPLOYMENT = "gpt-4o-mini"    # or your deployment name
   ```

## Run

```powershell
cd C:\projects\lambda-rag
dotnet run --project tools\LockedOracleProbe -- --n 100
```

Flags:

| Flag | Default | Meaning |
|---|---|---|
| `--n <int>` | `100` | Number of probe calls |
| `--endpoint <url>` | `$AZURE_OPENAI_ENDPOINT` | Override endpoint |
| `--deployment <name>` | `$AZURE_OPENAI_DEPLOYMENT` or `$AZURE_OPENAI_MINI_DEPLOYMENT` or `gpt-4o-mini` | Override deployment |
| `--out <dir>` | `out\locked-oracle-probe` | Output root; a timestamped subfolder is created per run |

## Output

```
out/locked-oracle-probe/probe-<UTC-stamp>/
├── probe-report.md      # human-readable report + verdict
├── probe-report.json    # machine-readable metrics + per-run manifest
└── runs/
    ├── run-000.json     # raw response bodies (verbatim, for divergence inspection)
    ├── run-001.json
    └── ...
```

## The three metrics (novel piece)

The FID-Lottery paper computes one variance number (σ). For lambda-rag we need to know *where* variance lives:

1. **Raw byte-identity** — SHA-256 of the raw response string. Sensitive to whitespace, property ordering, tokenizer boundaries.
2. **Canonical-JSON identity** — parse to typed record, re-serialize canonically, SHA-256 of that. Isolates *semantic* drift from formatting drift. **This is the number that decides the verdict.**
3. **Per-field modal agreement** — for each of the 5 schema fields, fraction of runs whose value equals the modal value. Tells us which fields are flip-prone → which rules need `idempotencyClass: "strict"` (N=3 majority vote) once the pattern ships.

## Design choices (spike-scope)

- **Sequential calls, not parallel.** Parallel calls change the hardware-drift profile. Sequential is paper-faithful for measuring drift, at the cost of wall time (~50–500 seconds for N=100 depending on latency).
- **Fixed input document** — `ProbeDocument.Text` in `ProbeDocument.cs`. Editing it invalidates any comparison across runs.
- **Pinned seed = 42** — Azure OpenAI's `seed` parameter is best-effort per Microsoft docs; the probe measures whether it is actually honored end-to-end.
- **`temperature=0`, `top_p=1`, `response_format=json_object`** — all three set to the "least random" values the API exposes.
- **No retries on transient errors.** A network flake is a failed run, counted separately from divergent runs.
- **Throwaway.** This project is not referenced by any other assembly and does not ship in any release artifact.

## What to do with the result

- **GREEN** → File a follow-up PR that promotes the probe to a permanent gate (nightly job, or Phase 4 of #175). Start Phase 1.
- **AMBER** → Update #175 to require majority-vote fallback for every LLM-backed projector, not just `strict`. Then start Phase 1.
- **RED** → Comment on #175 with the report attached. Consider re-probing on a different backend (self-hosted vLLM, Anthropic, a locally hosted model with deterministic kernels) before closing.
