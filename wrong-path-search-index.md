# Wrong Path: Moving Rules into a Search Index

> Why the "index-as-source-of-truth runtime" direction (PRs #99 onward,
> culminating in `feat/108-projection-rules-pattern`) was a wrong turn,
> and what I (the assistant) consistently got wrong while trying to
> patch around the symptoms. Captured at the hard reset back to commit
> `93d7ca7` (the last good state before that direction was taken).

## TL;DR

- **Rules belong on disk as `{ruleset}.json` next to the sample they
  apply to** — not in an Azure Search index. The on-disk path was
  fast, deterministic, debuggable, and trivially reproducible across
  machines.
- The moment rules moved into a search index and the runtime started
  *retrieving* them, every downstream system (markup, redlining,
  comment placement, the rewriter) inherited "fuzzy retrieval" failure
  modes that don't exist when the runtime just deserializes a JSON
  file.
- The fixes I kept applying (gating gap summaries, loosening guard
  rails, narrowing clause-widening, regenerating goldens) were all
  treating *downstream symptoms* of one upstream mistake.
- The right move was always to revert. We've now reverted to
  `93d7ca7` and the path forward is: keep rules on disk, scope changes
  to authoring/extraction tooling only.

## What "the wrong path" actually was

Starting around commits `bc04dc5` (#76, "AI Search authoring pipeline,
Phase A") and finalized in `95a0807` (#99, "index-as-source-of-truth
runtime"), the project changed from:

- **Before (good):** authoring tooling indexed rules into Azure Search
  for *discovery/UX*, but the runtime evaluation engine read a plain
  JSON file (`{ruleset}.json`) from disk. Deterministic, hermetic,
  testable with goldens.
- **After (wrong):** the runtime itself called into `IRuleStore` →
  `AzureSearchRuleStore`, which used hybrid BM25 + vector retrieval
  to fetch rules at evaluation time. Now the set of rules that fired
  against a given document depended on which embeddings were live in
  the index, the retrieval threshold, and the index state — none of
  which are visible from the repo.

Once the runtime depended on the index, several things broke at once
(or became impossible to reason about):

1. **Sample/ruleset coupling is lost.** The repo used to have an
   obvious 1:1 mapping: `samples/contracts/contoso-sample-contract.docx`
   was meant to be reviewed with
   `samples/contracts/contoso-demo-ruleset.json`. After the move, the
   filesystem ruleset was deleted/orphaned, and you had to "seed" the
   index to even reproduce the demo. The same sample can produce
   different verdicts depending on index state — i.e. the demo is no
   longer reproducible.
2. **Determinism guardrail had to be loosened.** PR #75 ("no AI Search
   at runtime") was an explicit guardrail. The index-as-runtime work
   forced loosening it, which silently allowed the runtime to depend
   on network state.
3. **Clause-widening produced grotesque spans.** With rules being
   retrieved fuzzily, many Fail verdicts came back with no precise
   anchor (no regex hit, no `Contains()` literal hit), so
   `EvaluationService.RefineSpans` fell into a fallback path that
   widened the clause to the entire matched section — sometimes 30+
   paragraphs. The markup engine then anchored the comment range
   across those 30+ paragraphs (so Word put the balloon at the top of
   the document) and the rewriter "replaced the entire title section"
   with one line.
4. **Comments-at-the-top bug.** The visible user-facing symptom of
   #3 was: comments cluster at the start of the document while
   redlines look mostly correct further down. That is what the user
   was repeatedly reporting.

## Symptoms I kept "fixing" downstream instead of upstream

This is the part I want to remember. Each of these was a real, plausible
fix in isolation — and each one was patching a *symptom* of the
upstream wrong path:

- **Gating the gap summary behind `--include-gap-summary`.** This made
  the literal "top-of-doc dump" go away for the gap case, but left the
  same anchor-at-document-top behavior in place for any Fail verdict
  whose clauseSpan spanned the whole section.
- **Loosening the rules-engine guardrail to honor `gateThreshold`.**
  This unlocked verdicts the index was producing, but it didn't fix
  the fact that the spans those verdicts carried were untrustworthy.
- **Narrowing clause-widening to the first paragraph** in
  `RefineSpans` (`if (hit is null) … WidenToParagraph(0, 1)`). This
  is genuinely a better default and probably worth keeping if the
  retrieval-runtime ever comes back — but on this branch it was the
  third or fourth attempt to mop up after the wrong path, and it
  destabilized goldens for unrelated tests.
- **Regenerating golden snapshots.** Any time a "fix" changed
  computed spans, the corpus-regression goldens drifted. The fact
  that "the goldens needed regenerating" so often was itself the
  signal — deterministic systems don't move their goldens this much.

In retrospect every one of these was me defending a wrong upstream
decision instead of saying "this whole runtime-index path is the
problem".

## How I (the assistant) should have caught this sooner

Signals I had and didn't act on:

1. **The user said "it used to work flawlessly before we moved to a
   search index"** more than once. That sentence was a clean pointer
   to the wrong commit. I treated it as background flavor and kept
   patching forward.
2. **The repo had an explicit determinism guardrail (#75) that said
   "no AI Search at runtime".** Any change that requires removing or
   loosening a determinism guardrail is, by default, suspect.
3. **The same sample directory had a `contoso-demo-ruleset.json`
   that was being *deleted* or *ignored* by the new path.** When a
   sample loses its sibling ruleset and you start "seeding the
   index" to reproduce the demo, you have replaced a file read with
   a network call — that is the actual regression.
4. **Goldens kept needing regeneration.** Deterministic platforms
   produce stable byte-for-byte output. When you find yourself
   updating goldens on every fix, that is the bug.

## Rules I'm writing down for next time

- **`samples/{vertical}/{name}.docx` must always have a sibling
  `{name}-ruleset.json` (or equivalent) and that file is the runtime
  source of truth for the demo.** No "seed the index first" step.
- **The runtime evaluation engine must not perform network I/O to
  load rules.** Authoring tooling can publish into Azure Search for
  discovery; the runtime reads from disk. The PR #75 guardrail
  exists for a reason — restore it.
- **If a user says "it worked before X", I should treat X as a
  hypothesis to test before applying any downstream patch.** Even a
  single `git log --oneline` + `git diff X^..X --stat` would have
  shown me the runtime-index move on the first turn.
- **Goldens drifting on a "small fix" is a red flag, not a chore.**
  When goldens move, stop and ask whether the underlying change is
  actually a refactor of behavior the user didn't ask for.
- **Comments at the top of the doc + redlines correct elsewhere = an
  upstream span problem, not a comment-placement bug.** OOXML
  `commentRangeStart` / `commentRangeEnd` cluster at the top because
  *the span itself is too wide*. Fix the span source, not the
  renderer.

## What we are reverting to and why

- **Revert target:** commit `93d7ca7` —
  `fix(#96): remove invalid =literal projection mappings and default to AFD endpoint (#97)`.
- This is the last commit on `main` (`origin/main` at the time of
  this write is `9c40265`, which is one fix further on top of
  `93d7ca7`'s parent line). `93d7ca7` precedes #99
  (`feat(#98): index-as-source-of-truth runtime`), which is the
  commit that introduced runtime dependence on the search index.
- After this revert, `samples/contracts/contoso-demo-ruleset.json`
  exists again, and `samples/contracts/contoso-sample-contract.docx`
  can be reviewed against it with the standard CLI flow, fully
  deterministic, no index seeding required.

## What is *not* lost

Real improvements that happened between `93d7ca7` and the reset point
(redline anchoring fixes, ComplianceEditor agent, sentence spacing,
list preservation, paragraph-aligned clause spans, ARB ruleset, etc.)
were genuinely useful and may be worth cherry-picking back onto this
base in small, scoped PRs — but only if they don't reintroduce the
runtime-index dependency. The directional mistake was specifically
"move the *runtime* off disk and into Azure Search", not any of the
authoring or markup work.
