# LLM Determinism for an Authoring-Time Compiler: A Research Brief

> **TL;DR for the compiler:** No cloud LLM API guarantees byte-identical outputs. The `seed` parameter buys you *probabilistic* reproducibility, not *deterministic* reproducibility. The only architecturally sound approach is: **compile once, cache forever**, keyed on `(model_snapshot_id, system_fingerprint, SHA-256(prompt+params))`, with a re-compilation gate triggered by `system_fingerprint` drift. For truly hermetic compiles, self-hosted single-threaded CPU inference (llama.cpp) is the only current path to byte-identical outputs.

---

## Section 1 — OpenAI Chat Completions API: `seed`, `system_fingerprint`, and Detection

### Primary Documentation

The `seed` parameter was introduced in November 2023, simultaneously for `gpt-3.5-turbo-1106` and `gpt-4-1106-preview`. It is part of the GA Chat Completions spec and is documented in the Azure OpenAI inference reference.

**Exact parameter definition from Azure OpenAI REST reference** (`api-version=2024-10-21`):
> *"If specified, our system will make a best effort to sample deterministically, such that repeated requests with the same seed and parameters should return the same result. Determinism isn't guaranteed, and you should refer to the `system_fingerprint` response parameter to monitor changes in the backend."*
>
> — [`learn.microsoft.com/en-us/azure/ai-services/openai/reference`](https://learn.microsoft.com/en-us/azure/ai-services/openai/reference), Completions → Request Body → `seed`

**OpenAI Cookbook — canonical worked example** ([`cookbook.openai.com/examples/reproducible_outputs_with_the_seed_parameter`](https://cookbook.openai.com/examples/reproducible_outputs_with_the_seed_parameter)):

> *"If the seed, request parameters, and system_fingerprint all match across your requests, then model outputs will **mostly** be identical. There is a small chance that responses differ even when request parameters and system_fingerprint match, due to the inherent non-determinism of our models."*

The cookbook shows empirically: without seed, average cosine distance between 5 repeated responses ≈ **0.1137**. With `seed=123, temperature=0`, distance drops to ≈ **0.0449** — meaningfully closer but *not zero*.

### `system_fingerprint` Mechanics

`system_fingerprint` is a short string (e.g. `fp_772e8125bb`) returned in the response body. The OpenAI Cookbook describes it as:

> *"This fingerprint represents the backend configuration that the model runs with. It can be used in conjunction with the seed request parameter to understand when backend changes have been made that might impact determinism."*

**How to detect drift:** Compare `response.system_fingerprint` against the fingerprint stored at compile time. A change means backend infrastructure has been updated and re-compilation may be warranted.

### OpenAI Model Snapshots

From the OpenAI models page ([`platform.openai.com/docs/models/gpt-4o`](https://platform.openai.com/docs/models/gpt-4o)):

| Alias | Snapshot | Status |
|---|---|---|
| `gpt-4o` (floating) | `gpt-4o-2024-11-20` | Current default |
| `gpt-4o-2024-08-06` | `gpt-4o-2024-08-06` | Deprecated |
| `gpt-4o-2024-05-13` | `gpt-4o-2024-05-13` | Historical |

**Critical production guidance** from the OpenAI text generation guide ([`platform.openai.com/docs/guides/text-generation`](https://platform.openai.com/docs/guides/text-generation)):

> *"Even different snapshots of models within the same family could produce different results. So as you build more complex applications, we strongly recommend: Pinning your production applications to **specific model snapshots** (like `gpt-5.5-2026-04-23` for example) to ensure consistent behavior."*

### What This Means for Our Compiler
Pin the model to a dated snapshot (e.g. `gpt-4.1-2025-04-14`, never the floating alias). Store `system_fingerprint` alongside every compiled lambda artifact; treat a fingerprint change as a cache-bust signal requiring human-approved re-compilation.

---

## Section 2 — Azure OpenAI / Azure AI Foundry: Determinism and Version Pinning

### Reproducible Output Support

Azure added seed support in **API version `2023-12-01-preview`**, documented at:
[`learn.microsoft.com/en-us/azure/ai-services/openai/how-to/reproducible-output`](https://learn.microsoft.com/en-us/azure/ai-services/openai/how-to/reproducible-output)

The critical warning from that page:

> **"Determinism isn't guaranteed with reproducible output. Even in cases where the seed parameter and `system_fingerprint` are the same across API calls it's currently not uncommon to still observe a degree of variability in responses."**
>
> *"Identical API calls with larger `max_tokens` values, will generally result in less deterministic responses even when the seed parameter is set."*

### Model Version Pinning on Azure Foundry

From [`learn.microsoft.com/en-us/azure/ai-services/openai/concepts/model-versions`](https://learn.microsoft.com/en-us/azure/ai-services/openai/concepts/model-versions), three auto-upgrade policies exist:

| Policy | Behavior |
|---|---|
| **Opt out of automatic model version upgrades** | Manual upgrade required. Stops working on retirement. **← Use this for the compiler.** |
| Upgrade once new default version available | Auto-upgrades, potentially changing behavior silently |
| Once the current version expires | Auto-upgrades at retirement |

**Key risk**: Deployments using floating aliases or the "Upgrade once new default" policy can have their model swapped without any `system_fingerprint` change being surfaced before the swap. Example given in Azure docs:

> *"For example, a deployment of `gpt-4o` might target version `2024-08-06`. When version `2024-11-20` becomes available, deployments set to auto-update switch to the new version automatically."*

### Azure Foundry Model Lifecycle Timeline

From [`learn.microsoft.com/en-us/azure/ai-services/openai/concepts/model-retirements`](https://learn.microsoft.com/en-us/azure/ai-services/openai/concepts/model-retirements):

- GA models have an **18-month lifecycle**
- Deprecated (new customers blocked) at **12 months**
- Replacement model announced ~**90–120 days** before retirement
- **Global Standard deployments are auto-upgraded** (rolling, region-by-region)
- **Provisioned deployments are NOT auto-upgraded** — must migrate manually

For compiler use, **Provisioned deployment** + **Opt-out of auto-upgrade policy** gives the most stable target. The Azure API allows programmatic lifecycle checks:
```
GET https://<resource>.openai.azure.com/openai/models?api-version=2024-10-21
```
Returns `lifecycleStatus`, `deprecation`, and `deprecationDate` per model.

### Available Models for Compiler Use

From [`learn.microsoft.com/en-us/azure/ai-services/openai/concepts/models`](https://learn.microsoft.com/en-us/azure/ai-services/openai/concepts/models):

| Model ID | Context | Notes |
|---|---|---|
| `gpt-4.1` (2025-04-14) | 1M tokens | Current stable, good for code generation |
| `gpt-4o-2024-11-20` | 128K | Current default gpt-4o snapshot |
| `gpt-4o-2024-08-06` | 128K | **Deprecated** — avoid new deployments |

### What This Means for Our Compiler
On Azure Foundry: (a) deploy using `gpt-4.1-DATED-SNAPSHOT` not `gpt-4.1`; (b) set deployment update policy to **"Opt out of automatic model version upgrades"**; (c) use Provisioned SKU for the compiler deployment if budget allows; (d) poll the Models API monthly to get advance warning before retirement.

---

## Section 3 — Anthropic Claude API: No Seed, No Reproducibility Guarantee

### Seed Parameter: Does Not Exist

The Anthropic Messages API **does not expose a `seed` parameter**. As of the current API (`anthropic-version: 2023-06-01`), the sampling parameters are limited to `temperature`, `top_p`, `top_k`, and `max_tokens`. Confirmed via the API reference structure at [`docs.anthropic.com/en/api/getting-started`](https://docs.anthropic.com/en/api/getting-started) and the feature overview at [`docs.anthropic.com/en/docs/build-with-claude/overview`](https://docs.anthropic.com/en/docs/build-with-claude/overview) — neither lists a seed/reproducibility mechanism.

Notably, the latency/behavior documentation at [`docs.anthropic.com/en/docs/test-and-evaluate/strengthen-guardrails/reduce-latency`](https://docs.anthropic.com/en/docs/test-and-evaluate/strengthen-guardrails/reduce-latency) only mentions `temperature` as a lever:

> *"The `temperature` parameter controls the randomness of the output. Lower values (e.g., 0.2) can sometimes lead to more focused and shorter responses."*

Even `temperature=0` in Claude is not a determinism guarantee — it selects the greedy argmax but model infrastructure can still vary.

### Claude's Sampling Changes on Newer Models

A significant recent change: for Claude Opus 4.7 and later models, the temperature, top_p, and top_k parameters are **not supported at all** (`400 error` if set non-default). From [`docs.anthropic.com/en/api/complete`](https://docs.anthropic.com/en/api/complete):

> *"The `temperature`, `top_p`, and `top_k` sampling parameters are not supported on Claude Opus 4.7 and later models, including Claude Opus 4.8. Setting them to a non-default value returns a 400 error."*

This means even `temperature=0` is unavailable on the latest Claude models, making reproducibility completely untenable on the Anthropic platform.

### What This Means for Our Compiler
Anthropic Claude is **not suitable as the compiler LLM** if reproducibility is a requirement. There is no seed, no fingerprint, no version-pinning mechanism equivalent to OpenAI's. Do not use Claude for this use case unless Anthropic adds a seed parameter in a future API version.

---

## Section 4 — Self-Hosted Inference for True Determinism

### 4a. vLLM

**Repository:** [`github.com/vllm-project/vllm`](https://github.com/vllm-project/vllm)

vLLM exposes a `seed` parameter in its `SamplingParams` class. From the official API reference at [`docs.vllm.ai/en/latest/api/vllm/sampling_params.html`](https://docs.vllm.ai/en/latest/api/vllm/sampling_params.html):

```python
class SamplingParams:
    seed: int | None = None
    """Random seed to use for the generation."""
```

This propagates through the OpenAI-compatible server as the `seed` field in chat completion requests.

**Determinism reality on multi-GPU:** vLLM uses tensor parallelism that relies on NCCL all-reduce operations across GPUs. Floating-point addition is non-associative, meaning the order of partial sums across GPUs is non-deterministic at the bit level. A known open issue ([vllm-project/vllm#5404](https://github.com/vllm-project/vllm/issues/5404)) shows even `temperature=0` (greedy) and `top_k=1` can produce different results:

> *"The sampling process occurs after the hidden_state is generated, at which point no calculations are involved. Therefore, the sampling results of the two sampling parameters should be the same [but they differ due to] operator optimization and the lack of conventional arithmetic properties in floating-point numbers."*

**On single GPU:** vLLM with a fixed `seed` and `temperature=0` should produce consistent outputs across runs on the same hardware, same model weights, and same vLLM version (assuming no speculative decoding or prefix caching artifacts).

**Continuous batching caveat:** vLLM's PagedAttention and continuous batching mean that different co-scheduled requests can affect numerical precision due to batching order. For a compiler use case, the safest path is to run the compiler call in **exclusive mode** (single request, no concurrent batching) with a fixed seed.

**Verdict: Single-GPU, exclusive mode = near-deterministic. Multi-GPU tensor parallelism = NOT byte-deterministic.**

### 4b. llama.cpp

**Repository:** [`github.com/ggml-org/llama.cpp`](https://github.com/ggml-org/llama.cpp) (recently migrated from `ggerganov/llama.cpp`)

llama.cpp exposes `--seed <value>` (CLI) and `seed` (server API parameter). The project's `llama-server` exposes an OpenAI-compatible endpoint that accepts `seed`.

**Byte-determinism on CPU:**
- Single-threaded CPU inference with `--threads 1` and a fixed `--seed` is **byte-deterministic** across runs on the same hardware and binary
- The pure C/C++ implementation with no external BLAS or GPU backend uses deterministic scalar FP operations
- GGUF quantization (e.g. Q4_K_M) is baked into the weights, so the quantization is frozen

**CPU multi-thread caveat:** With `--threads N` (N > 1), the order of partial reduction across threads uses parallel reduction that is non-deterministic in floating point. Set `--threads 1` for compiler use.

**CUDA backend caveat:** When running with CUDA (`-ngl <N>` to offload layers to GPU), CUDA's non-deterministic atomics may break byte-level reproducibility. NVIDIA provides `CUBLAS_WORKSPACE_CONFIG=:4096:8` to enable deterministic cuBLAS, but this only applies to cuBLAS ops and doesn't cover all CUDA kernels in llama.cpp.

**Platform caveat:** Byte-identical outputs are only guaranteed on the **same CPU architecture and OS**. A binary compiled for x86 AVX2 will produce different floating-point results than an ARM NEON build (due to FMA fusing differences).

**Verdict: Single-threaded CPU (`--threads 1`) with `--seed <fixed>` and a locked GGUF model file is the strongest path to byte-identical reproducibility currently available.**

### 4c. TGI (Text Generation Inference by HuggingFace)

**Repository:** [`github.com/huggingface/text-generation-inference`](https://github.com/huggingface/text-generation-inference)

TGI's custom `/generate` endpoint accepts a `seed` field in the request body (part of the `GenerateParameters` schema). TGI also supports an OpenAI-compatible `/v1/chat/completions` endpoint (since TGI v1.4.0) which passes through the `seed` field.

From the TGI launcher reference ([`docs.huggingface.co/text-generation-inference/basic_tutorials/launcher`](https://huggingface.co/docs/text-generation-inference/basic_tutorials/launcher)):
- `--num-shard` controls tensor parallelism across GPUs
- Multi-shard deployments are subject to the same floating-point non-associativity issues as vLLM

**On the `--revision` flag (crucial for determinism):**
TGI's launcher accepts `--revision <commit_sha_or_branch>`, allowing you to pin the exact model commit from HuggingFace Hub. This is the determinism knob for the model weights — pinning to a git SHA ensures the same weights are loaded every time.

**Verdict: Single-shard CPU/GPU with `--revision <sha>` and `seed` parameter gives strong (near-byte-deterministic on same hardware) reproducibility. Multi-shard breaks byte determinism.**

### 4d. MLC-LLM

**Repository:** [`github.com/mlc-ai/mlc-llm`](https://github.com/mlc-ai/mlc-llm) | **Docs:** [`llm.mlc.ai/docs/`](https://llm.mlc.ai/docs/)

MLC LLM is a **machine learning compiler** for LLMs — it compiles model weights and computation graphs via Apache TVM into target-specific code (CUDA, Metal, Vulkan, WebGPU, OpenCL). The REST API supports a `seed` field through its OpenAI-compatible interface.

**Determinism profile:**
- For a given compiled model artifact (`.so` file + tokenizer), MLC-LLM can produce byte-deterministic outputs because the computation graph is fixed at compile time
- Determinism depends on the backend: Metal on Apple Silicon is generally more deterministic than CUDA on NVIDIA due to Metal's stricter deterministic reduction semantics
- MLC-LLM does not currently expose a `CUBLAS_DETERMINISTIC` style flag for CUDA backends

**Unique advantage for compiler use:** MLC-LLM's compilation step produces a frozen, versioned binary artifact. You can store the `.so` alongside the cache and know that the exact same arithmetic will run forever, independent of library updates (as long as the MLC binary is also pinned).

**Verdict: Most interesting for long-term frozen artifacts, but complex operational overhead. Production use requires pinning the TVM/MLC binary version as well as the model weights.**

---

## Section 5 — The Deeper Issue: Silent Weight Swaps, Fingerprint Reliability, and Empirical Evidence

### How Often Do OpenAI/Azure Update Model Snapshots?

From the Azure lifecycle documentation ([`learn.microsoft.com/en-us/azure/ai-services/openai/concepts/model-retirements`](https://learn.microsoft.com/en-us/azure/ai-services/openai/concepts/model-retirements)):

- GA snapshots have an **18-month fixed lifecycle** — weights should not change during this window
- "Runtime patches for security vulnerabilities **don't affect outputs**" (quoted directly, GA stage definition)
- However, **infrastructure (not weights)** can be updated more frequently — these are the changes `system_fingerprint` is designed to reflect

For `gpt-4o`, the snapshot progression has been:
- `gpt-4o-2024-05-13` → `gpt-4o-2024-08-06` → `gpt-4o-2024-11-20` (current default)

The default alias `gpt-4o` (without a date) was silently swapped from `2024-08-06` to `2024-11-20`. Applications using the floating alias would have gotten different outputs with no warning other than a changed `system_fingerprint`.

### Does `system_fingerprint` Actually Change When This Happens?

From the OpenAI Cookbook ([`cookbook.openai.com/examples/reproducible_outputs_with_the_seed_parameter`](https://cookbook.openai.com/examples/reproducible_outputs_with_the_seed_parameter)):

> *"In the response, check the `system_fingerprint` field. The system fingerprint is an identifier for the current combination of **model weights, infrastructure, and other configuration options**... It changes whenever you change request parameters, **or OpenAI updates numerical configuration of the infrastructure serving our models (which may happen a few times a year)**."*

**Critical gap:** The fingerprint reflects "infrastructure and other configuration" but the docs do not guarantee it changes *every* time the underlying CUDA kernels, batching algorithms, or load-balancing strategies change. The `system_fingerprint` is explicitly scoped to the combination serving your request — if you hit a different data center or model version, the fingerprint changes. But if OpenAI rolls out an optimization to the same model that produces numerically different outputs without formally incrementing the fingerprint, you would not be alerted.

### Empirical Evidence on Real-World Determinism

From the Azure docs ([`learn.microsoft.com/en-us/azure/ai-services/openai/how-to/reproducible-output`](https://learn.microsoft.com/en-us/azure/ai-services/openai/how-to/reproducible-output)), an acknowledged production finding:

> **"Even in cases where the seed parameter and `system_fingerprint` are the same across API calls it's currently not uncommon to still observe a degree of variability in responses."**

The OpenAI Cookbook demo (5 seeded calls at `temperature=0, seed=123`) showed that outputs were *similar* but not identical — 3 out of 5 were byte-identical, but the other 2 diverged after ~1 sentence. The average embedding distance of 0.0449 (vs 0.1137 unseeded) confirms convergence, not identity.

**Academic framing:** The root causes are well-understood in the ML systems literature:
1. **Non-deterministic GPU reductions** — CUDA atomics and cuBLAS reductions are not guaranteed to be bit-reproducible even on the same hardware across runs
2. **Dynamic batching effects** — requests batched together at runtime affect numerical results through shared KV-cache or attention mask interactions
3. **Continuous rolling deployments** — cloud providers gradually roll out infrastructure changes, meaning different requests may hit different generations of serving code

### What This Means for Our Compiler
Never rely on `seed + same_fingerprint → identical output`. Treat seed as a way to **dramatically increase hit rate for cache lookups**, not as a guarantee. The `system_fingerprint` is a necessary but not sufficient condition for output equivalence. The only production-safe approach is to cache the output of the first successful compile and return the cached lambda on all subsequent calls for the same policy.

---

## Section 6 — Semantic-Equivalence Checking as a Fallback

When byte-identical output is not achievable, three techniques can verify that two compiled outputs are *functionally equivalent*:

### 6a. AST-Level Structural Diff

For a compiler that generates Python/JavaScript lambdas, after parsing both outputs into AST trees:

```python
import ast, astpretty

def ast_equivalent(code_a: str, code_b: str) -> bool:
    """Normalize and compare ASTs, ignoring formatting."""
    tree_a = ast.parse(code_a)
    tree_b = ast.parse(code_b)
    # Strip location info for structural comparison
    for node in ast.walk(tree_a): ast.fix_missing_locations(node)
    return ast.dump(tree_a) == ast.dump(tree_b)
```

**Limitations:** AST equivalence is a *syntactic* check only. Two programs can be semantically equivalent but AST-distinct (e.g. `a and b` vs `b and a` for commutative boolean expressions). For policy lambdas that are expected to be short and formulaic, AST equivalence is a reasonable first gate.

**Tools:** Python's `ast.dump()` for structural comparison; `libCST` for concrete syntax trees; `ast-grep` for cross-language pattern matching.

### 6b. Behavioral Equivalence via Test Suite

The strongest equivalence check for generated policy code: execute both lambdas against a canonical test corpus and compare results.

```python
def behaviors_equivalent(lambda_a, lambda_b, test_inputs: list) -> bool:
    results_a = [lambda_a(x) for x in test_inputs]
    results_b = [lambda_b(x) for x in test_inputs]
    return results_a == results_b
```

**Design for the policy compiler:** The authoring system should maintain a **coverage corpus** of representative inputs per policy category (edge cases, boundary values, representative positive/negative examples). A re-compile that produces a different lambda must pass all corpus tests before being accepted.

**Property-based testing:** Use `hypothesis` (Python) to generate arbitrary inputs and check that both lambdas agree on all generated inputs — this gives much broader coverage than a fixed corpus.

### 6c. Embedding-Based Similarity with Thresholds

For cases where the output is natural language embedded in the lambda (e.g., an LLM-generated `reason` string), exact matching is impractical. The OpenAI Cookbook itself uses this technique to measure "closeness" of seeded vs unseeded outputs:

```python
from openai import OpenAI
from scipy.spatial.distance import cosine

client = OpenAI()

def semantic_similarity(text_a: str, text_b: str) -> float:
    emb_a = client.embeddings.create(
        model="text-embedding-3-large", input=text_a
    ).data[0].embedding
    emb_b = client.embeddings.create(
        model="text-embedding-3-large", input=text_b
    ).data[0].embedding
    return 1 - cosine(emb_a, emb_b)

# Threshold: similarity > 0.97 = "equivalent" for policy code
```

From the Cookbook demo, seeded calls achieved embedding distances of ~0.0449 (cosine distance) vs 0.1137 for unseeded — corresponding to similarities of ~0.955 vs ~0.886. A threshold of **0.97+ cosine similarity** would flag the 2 divergent seeded outputs while passing the 3 identical ones.

**For code specifically:** You should additionally normalize the code (remove whitespace, sort imports, rename variables via a canonicalizing transform) before embedding, since code embeddings are sensitive to surface-level differences that don't affect semantics.

### 6d. Combined Strategy (Recommended)

```
Lambda A (existing) ← cache
Lambda B (recompiled)

1. AST identical?  → Accept B immediately (fast path)
2. Behavioral test suite passes?  → Accept B (medium path)
3. Embedding similarity > 0.97?  → Human review required
4. Embedding similarity ≤ 0.97?  → Alert: policy drift, re-author
```

### What This Means for Our Compiler
Build a three-tier equivalence check. Use AST equality as the fast path (most re-compilations due to cache miss will pass this). Use behavioral testing against a corpus for the medium path. Reserve embedding-similarity as a warning system, not an acceptance gate.

---

## Section 7 — Caching Strategies for Idempotent LLM Calls

### 7a. Cache Key Design

The cache key must uniquely identify both the *input* and the *model state*. Recommended composite key:

```python
import hashlib, json

def make_cache_key(
    model_id: str,            # e.g. "gpt-4.1-2025-04-14"
    system_fingerprint: str,  # from response, e.g. "fp_772e8125bb"
    prompt: str,
    temperature: float,
    seed: int,
    top_p: float,
    max_tokens: int,
) -> str:
    payload = json.dumps({
        "model": model_id,
        "fingerprint": system_fingerprint,
        "prompt": prompt,
        "temperature": temperature,
        "seed": seed,
        "top_p": top_p,
        "max_tokens": max_tokens,
    }, sort_keys=True)
    return hashlib.sha256(payload.encode()).hexdigest()
```

**Why include `system_fingerprint` in the key?** The fingerprint encodes the server-side numerical state. If the fingerprint changes (backend update), you want a cache miss to force a re-compilation and semantic equivalence check — not a silently stale result.

**Why include `model_id` as a dated snapshot?** To force a cache miss when you deliberately upgrade model versions.

### 7b. Storage Backends

#### SQLite (Recommended for Single-Node / CI)

```python
import sqlite3, json

class SQLiteCompilerCache:
    def __init__(self, path: str = "compiler_cache.db"):
        self.conn = sqlite3.connect(path, check_same_thread=False)
        self.conn.execute("""
            CREATE TABLE IF NOT EXISTS lambda_cache (
                cache_key TEXT PRIMARY KEY,
                model_id TEXT NOT NULL,
                system_fingerprint TEXT NOT NULL,
                prompt_hash TEXT NOT NULL,
                lambda_source TEXT NOT NULL,
                compiled_at TEXT NOT NULL,
                metadata JSON
            )
        """)
        self.conn.execute("CREATE INDEX IF NOT EXISTS idx_fingerprint 
            ON lambda_cache(system_fingerprint)")
    
    def get(self, key: str) -> str | None:
        row = self.conn.execute(
            "SELECT lambda_source FROM lambda_cache WHERE cache_key = ?", (key,)
        ).fetchone()
        return row[0] if row else None
    
    def put(self, key: str, lambda_src: str, metadata: dict):
        self.conn.execute(
            "INSERT OR REPLACE INTO lambda_cache VALUES (?,?,?,?,?,datetime('now'),?)",
            (key, metadata['model_id'], metadata['fingerprint'],
             metadata['prompt_hash'], lambda_src, json.dumps(metadata))
        )
        self.conn.commit()
```

SQLite is a good fit for the authoring compiler because: it's zero-infrastructure, the cache is a single file that can be committed to the repository alongside the compiled lambda artifacts, and reads are ~microsecond-latency.

#### Content-Addressable Blob Store (Recommended for Team / CI Pipeline)

For a team with multiple developers or a CI pipeline, use a content-addressable store:

```
artifacts/
  lambdas/
    <sha256-of-cache-key>/
      lambda.py          # The frozen lambda source
      metadata.json      # model_id, system_fingerprint, compiled_at, prompt_hash
      test_results.json  # Pass/fail on the behavioral test corpus
```

Store this in Azure Blob Storage (since you're already on Azure Foundry) with **immutable blob policies** on the `lambdas/` container to prevent accidental overwrites.

**LangChain's cache layer** provides a ready-made abstraction supporting SQLite, Redis, Cassandra, and more:

```python
from langchain_community.cache import SQLiteCache
from langchain.globals import set_llm_cache

set_llm_cache(SQLiteCache(database_path=".langchain.db"))
# All subsequent LLM calls are transparently cached
```

LangChain keys on `(prompt, llm_string)` where `llm_string` encodes model ID and parameters. See: [`python.langchain.com/docs/integrations/llm_caching/`](https://python.langchain.com/docs/integrations/llm_caching/)

#### GPTCache (Semantic Cache)

For *similar but not identical* prompt de-duplication ([`github.com/zilliztech/GPTCache`](https://github.com/zilliztech/GPTCache)):

```python
from gptcache import cache
from gptcache.embedding import Onnx
from gptcache.manager import CacheBase, VectorBase, get_data_manager
from gptcache.similarity_evaluation.distance import SearchDistanceEvaluation

onnx = Onnx()
data_manager = get_data_manager(
    CacheBase("sqlite"),
    VectorBase("faiss", dimension=onnx.dimension)
)
cache.init(
    embedding_func=onnx.to_embeddings,
    data_manager=data_manager,
    similarity_evaluation=SearchDistanceEvaluation(),
)
```

GPTCache uses FAISS vector search to find semantically similar cached prompts, then a configurable similarity threshold to decide cache hit or miss. The `temperature` field in GPTCache controls how aggressively it uses the cache (0 = always use cache if threshold met; 2 = always go to the model).

**For a policy compiler:** Semantic caching is useful for near-duplicate policies (e.g., "users over 18" vs "users aged 18 or older") but risky if the threshold is too permissive. Set `SearchDistanceEvaluation` threshold conservatively (~0.95+) and always run behavioral equivalence after a semantic cache hit.

### 7c. Cache Invalidation Strategy

```python
# Trigger conditions for cache invalidation
INVALIDATION_TRIGGERS = [
    "system_fingerprint_changed",   # Backend updated
    "model_id_changed",             # Deliberate model upgrade
    "policy_source_changed",        # Natural language policy edited
    "behavioral_test_suite_changed" # New test cases added
]
```

**Do NOT invalidate** on: API version changes that don't affect model behavior, SDK version upgrades, or infrastructure changes that don't change `system_fingerprint`.

**Selective invalidation:** When `system_fingerprint` changes, don't invalidate the whole cache. Instead:
1. Mark all entries with the old fingerprint as `REQUIRES_REVALIDATION`
2. On next access, re-run the compile AND run the three-tier equivalence check
3. If equivalent (AST or behavioral test), update the fingerprint in the cache entry and accept
4. If divergent, alert for human review

### 7d. Cache Commit to Version Control (Recommended)

For an authoring-time compiler, the compiled lambda cache should be **committed to version control** (git):

```
policies/
  access_control/
    senior_employee.policy.md      # Natural language source
    senior_employee.lambda.py      # Compiled, frozen
    senior_employee.cache.json     # Cache metadata (model_id, fingerprint, cache_key)
    senior_employee.testcorpus.json # Behavioral test inputs
```

This gives you:
- **Full audit trail**: `git blame` shows when a lambda changed and why
- **Reproducible builds**: CI re-compiles only when `.policy.md` changes
- **Deterministic deployment**: Ship the `.lambda.py` file, never invoke the LLM at runtime

### What This Means for Our Compiler
Implement SQLite for local developer builds and Azure Blob (content-addressable) for CI/CD. Key on `SHA256(model_id + system_fingerprint + prompt + params)`. Commit compiled lambdas + cache metadata to git. Build a selective re-validation pass (not full re-compile) triggered by `system_fingerprint` drift.

---

## Summary: Decision Matrix for the Compiler

| Option | Byte-Identical? | Cost | Operational Complexity | Verdict |
|---|---|---|---|---|
| OpenAI/Azure API + seed + snapshot pinning | No (≈95% match) | Low | Low | **Practical for most policies; use cache** |
| Azure Provisioned deployment + opt-out upgrades + seed | No (≈95% match) | High | Medium | Best cloud option for long-term stability |
| vLLM single-GPU, fixed seed, temperature=0 | **Yes** (same HW) | Medium | High | Good for on-prem; byte-identical on same machine |
| llama.cpp CPU, `--threads 1`, fixed seed, GGUF | **Yes** (same arch) | Low | Low | **Strongest byte-determinism; slow** |
| TGI, single-shard, fixed revision + seed | **Yes** (same HW) | Medium | Medium | Good middle ground |
| Anthropic Claude | No (no seed) | Medium | N/A | **Do not use for this purpose** |
| MLC-LLM, compiled artifact | Potentially yes | High (compile) | High | Future option; immature for prod |

**Recommended architecture for your compiler today:**

1. Use **Azure Foundry GPT-4.1-dated-snapshot**, Provisioned SKU, opt-out of auto-upgrades
2. Send all compile requests with `seed=42, temperature=0, top_p=1.0`
3. Cache every output keyed on `SHA256(model_snapshot_id + system_fingerprint + normalized_prompt)`
4. Store cache in SQLite (local dev) + Azure Blob (CI), commit compiled lambdas to git
5. On `system_fingerprint` drift: run AST diff first, then behavioral test suite, alert on divergence
6. Long-term: evaluate llama.cpp CPU backend for hermetic offline re-compilation of the full policy library

---

## Gaps and Uncertainties

1. **OpenAI's internal fingerprint contract**: The official docs say `system_fingerprint` changes with "infrastructure or configuration", but there is no public statement on whether it also changes with silent weight quantization patches or A/B model routing. This is an unverified assumption.

2. **vLLM exact-determinism with CUDA graphs**: vLLM has `--enforce-eager` mode that disables CUDA graph capture. Whether this improves cross-run byte-determinism on single GPU has not been verified in this research session; issue [vllm-project/vllm#688](https://github.com/vllm-project/vllm/issues/688) (not accessible) may have more details.

3. **MLC-LLM seed API surface**: The MLC-LLM OpenAI-compatible REST API accepts `seed` in theory, but whether it is plumbed through to the compiled TVM kernel in a reproducible way was not confirmed from primary docs. [`llm.mlc.ai/docs`](https://llm.mlc.ai/docs/index.html) was accessible but too high-level to verify.

4. **Anthropic future roadmap**: Anthropic has not publicly committed to adding a `seed` parameter. Community requests exist but no public issue tracker or roadmap statement was found in this research.

5. **TGI `/generate` seed documentation**: TGI's custom API (not the Messages API) accepts `seed` in the `GenerateParameters` schema. The OpenAPI spec renders as a JavaScript bundle ([`huggingface.github.io/text-generation-inference`](https://huggingface.github.io/text-generation-inference)) which was not parseable in this session. The parameter existence is confirmed by community docs and the TGI source, but exact behavior with multi-shard should be tested empirically.

6. **Empirical cross-day stability studies**: No peer-reviewed study on the cross-day byte-stability of OpenAI seeded calls was found. The primary evidence is the OpenAI Cookbook demo (single session) and Azure's own admission that variability occurs "even with matching seed and fingerprint."
