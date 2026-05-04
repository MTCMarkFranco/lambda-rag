# Ruleset Accuracy Evaluation #001

**Date:** 2026-05-04
**Ruleset:** `samples/contracts/contoso-demo-ruleset.json` @ `2.0.0`
**Document:** `samples/contracts/contoso-sample-contract.docx`
**Engine:** `LambdaRag.Cli` (post-Contoso scrub baseline, commit `fa55e42`)
**Baseline report:** [`eval-baselines/eval-001-baseline.json`](../../eval-baselines/eval-001-baseline.json)

> Tracking issue: [#62](https://github.com/MTCMarkFranco/lambda-rag/issues/62) — *Eval: holistic rule-by-rule accuracy review of contoso-demo-ruleset against bundled sample contract*

## Run summary

```
pass = 5
fail = 21
gap  = 1
total = 27 verdicts + 1 gap = 28 rule applications
```

## 1. Rule-by-rule judgment

Legend: ✅ engine outcome matches reviewer · ❌ outright wrong · ⚠️ outcome correct but rule weak · 💥 engine error reported as Fail.

| # | RuleId | Section | Engine | Reviewer | Predicate | Lambda | Anchor | Remediation | Notes |
|---|---|---|---|---|---|---|---|---|---|
| 0 | CTSO-AI-ADDENDUM | §3 AI | Fail | ✅ Fail | ✓ | ✓ | ⚠️ generic | ✓ | Real miss — no AI Addendum referenced |
| 1 | CTSO-AI-PRIVACY-SUPP | §3 AI | Pass | ❌ should Fail | ✓ | ✗ vacuous | — | — | Lambda fires only if literal `"personal data"` appears; §3 processes "transaction patterns" (effectively personal data) but rule passes vacuously |
| 2 | CTSO-CONF-001 | §6 Conf | Pass | ⚠️ Pass-but-weak | ✓ | weak | ⚠️ | — | Lambda passes on any `"year"` string; doesn't validate duration is sufficient |
| 3 | CTSO-CYBER-CRYPTO | §10 Sec | Fail | ✅ Fail | ✓ | ✓ | ⚠️ | ✓ | Real miss — no AES/TLS specs |
| 4 | CTSO-INDM-001 | §9 Liab | Fail | ✅ Fail | ✓ | ✓ | ⚠️ | ✓ | Real miss — no IP-indemnity defend obligation |
| 5 | CTSO-INS-CYBER-10M | §11 Ins | Fail | ✅ Fail | ✓ | ⚠️ aggregate | ⚠️ | ✓ | Outcome OK but `dollar_max` is whole-section; would mis-pass if cyber=$10M and GCL=$1M |
| 6 | CTSO-INS-GCL-5M | §11 Ins | Fail | ✅ Fail | ✓ | ⚠️ aggregate | ⚠️ | ✓ | Same `dollar_max` weakness |
| 7 | CTSO-IP-WORKFORHIRE | §8 IP | Fail | ✅ Fail | ✓ | ✓ | ⚠️ | ✓ | Real miss — IP vests in Vendor |
| 8 | CTSO-LAW-QUEBEC | §12 Law | Fail | ⚠️ context-dependent | ✓ | ✓ | ⚠️ | ✓ | Rule assumes Contoso = Quebec HQ; sample has Ontario/Toronto |
| 9 | CTSO-LIAB-001 | §9 Liab | Pass | ✅ Pass | ✓ | ✓ | ⚠️ | — | OK |
| 10 | CTSO-LIAB-001 (dup) | §11 Ins | Pass | ❌ should not fire | ✗ over-match | n/a | ✗ | — | Predicate `topics.Contains("liability")` matches §11 because of phrase "liability insurance" |
| 11 | CTSO-LIAB-CARVEOUTS | §9 Liab | Fail | ✅ Fail | ✓ | ✓ | ⚠️ | ✓ | Real miss — no fraud/IP carve-outs |
| 12 | CTSO-LIAB-CARVEOUTS (dup) | §11 Ins | Fail | ❌ should not fire | ✗ | n/a | ✗ | misleading | Same predicate over-match; remediation tells user to add carve-outs to "11. INSURANCE clause" |
| 13 | CTSO-PAY-INT-MAX | §4 Pay | Fail | ✅ Fail | ✓ | ✓ | ⚠️ on 4.3 | ✓ | Real miss (2% > 1.5%) but markup anchored to last paragraph (4.3) — happens to be the offender here, but pattern is luck |
| 14 | CTSO-PAY-NET45 | §4 Pay | Fail | ✅ Fail | ✓ | ⚠️ aggregate | ⚠️ | ✓ | `day_count_max=60` from 4.1 — works here, but mixes 60/30/15 day counts across the section |
| 15 | CTSO-PRIV-72H-Contoso | §7 Priv | Fail | ⚠️ outcome OK, lambda brittle | ✓ | ✗ literal | ⚠️ | ✓ | Lambda requires literal `"Contoso"`; contract uses defined term `"Company"` (= Contoso) |
| 16 | CTSO-PRIV-CONSENT | §7 Priv | Fail | ✅ Fail | ✓ | ✓ | ⚠️ | ✓ | Real miss — 7.4 says "Vendor's discretion" |
| 17 | CTSO-PRIV-EXPLICIT-LAWS | §7 Priv | Fail | ✅ Fail | ✓ | ✓ | ⚠️ | ✓ | Real miss — no PIPEDA/Law 25 |
| 18 | REMOVED-RULE | §7 Priv | Fail | ✅ Fail | ✓ | ✓ (double-neg) | ⚠️ | ✓ | Real miss — 7.2 allows transfer to any country |
| 19 | CTSO-PRIV-RETENTION | §7 Priv | Fail | ❌ engine error | ✓ | 💥 runtime exception | ✗ | ✓ | `year_count_max` undefined when no years present → exception silently treated as Fail with stack trace in errorMessage |
| 20 | CTSO-SUBK-APPROVAL | §2 Scope | Fail | ✅ Fail | ✓ | ✓ | ⚠️ | ✓ | Real miss — 2.3 lets Vendor subcontract w/o notice |
| 21 | CTSO-SVC-LOCATION | §5 Tax | Fail | ✅ Fail | ⚠️ | ✓ | ✗ wrong section | ⚠️ | Outcome correct but anchored to Tax clause; remediation says "in §5 TAX PROVISIONS" |
| 22 | CTSO-TAX-EXCL | §4 Pay | Fail | ✅ Fail | ✓ | ✓ | ✗ on 4.3 | ⚠️ | Original observation — markup lands on 4.3, real offender is 4.2 |
| 23 | CTSO-TAX-EXCL (dup) | §5 Tax | Fail | ⚠️ redundant | ✓ | ✓ | ⚠️ | ⚠️ | Same logical rule fires twice across §4 and §5 |
| 24 | CTSO-TERM-001 | §1 Term | Pass | ⚠️ Pass-but-weak | ✓ | weak | ⚠️ | — | Passes if any day-count appears |
| 25 | CTSO-TERM-CONV | §1 Term | Fail | ✅ Fail | ✓ | ✓ | ⚠️ | ✓ | Real miss — symmetric termination, not Contoso-only |
| 26 | CTSO-WAR-001 | — | Gap | ✅ Gap | n/a | n/a | n/a | ✓ | Correct — no warranty section in contract |

## 2. Tallies

| Dimension | Count | % |
|---|---|---|
| Engine outcome matches reviewer | 22 / 28 | **79 %** |
| Outright wrong outcomes (FN / FP / engine error) | 4 (#1, #10, #12, #19) | 14 % |
| Outcome correct but rule weak / brittle | 6 (#2, #5, #6, #14, #15, #24) | 21 % |
| Anchor visibly wrong or misleading | ~24 / 27 | **89 %** |
| Remediation references wrong clause | 2 (#12, #21) | 7 % |
| Duplicate firings (same rule, multiple sections) | 3 pairs (#9/10, #11/12, #22/23) | — |

## 3. Systemic issues — ranked by impact

1. **Markup anchors land on section, not offending substring.** ~89 % of comments. The original `4.3 vs 4.2` problem is the rule, not the exception. Highest UX-credibility risk.
2. **Predicate over-match via `topics.Contains("liability")`.** §11 Insurance picks up `liability` topic from "liability insurance" and triggers limitation-of-liability rules. Predicates need to distinguish *operative-for-topic* from *mentions-topic*.
3. **Engine treats lambda runtime exceptions as `Fail`.** `CTSO-PRIV-RETENTION` blew up on missing `year_count_max`; should surface as `Error` outcome, not silent Fail with the stack trace embedded in `errorMessage`.
4. **Substring matching is too literal.** Rules requiring `"Contoso"` miss when contract uses defined term `"Company"`. Need defined-party resolution before lambda eval, or a `parties.Resolve("Contoso")` helper.
5. **Aggregate features (`dollar_max` / `day_count_max` / `percent_max`) are too coarse.** Would let asymmetric cyber-vs-GCL or 60-vs-15-day splits silently pass. Need feature extraction at clause granularity, or per-keyword filtering.
6. **Same logical rule fires N times across N matching sections** (CTSO-TAX-EXCL, CTSO-LIAB-001 / CARVEOUTS). Emits multiple comments for one obligation.
7. **Vacuous-true conditional lambdas** (CTSO-AI-PRIVACY-SUPP). `if(X) then(Y)` quietly passes when X is absent even when Y is the actual obligation we want present.
8. **Weak presence-only lambdas** (CTSO-CONF-001, CTSO-TERM-001). Pass on any keyword, don't validate the obligation's strength.

## 4. Recommended interventions (ranked)

| Pri | Intervention | Impact | Touches |
|---|---|---|---|
| P0 | Engine: catch lambda exceptions → emit `Error` outcome | Eliminates silent failures (#19) | `LambdaRag.Evaluation` |
| P0 | Engine: anchor markup to lambda-matched substring (or first keyword in evidence list) | Fixes 89 % of misleading comments | `LambdaRag.Markup`, projection layer |
| P0 | Rule fix: predicate hardening on `liability` rules → require `is_operative_for_topic` | Removes 2 false-positive firings | `contoso-demo-ruleset.json` |
| P0 | Rule fix: rewrite CTSO-AI-PRIVACY-SUPP without vacuous-true conditional | Removes 1 false negative | ruleset |
| P1 | Engine: resolve party defined-terms (`Company` ↔ `Contoso`) before lambda eval | Removes brittle literal matches across all rules | `LambdaRag.Projection` (or a pre-lambda preprocessor) |
| P1 | Engine: per-clause feature extraction (`dollar_amounts` keyed by nearest topical keyword) | Hardens insurance + payment rules | `LambdaRag.Indexing` / projection |
| P1 | Rule fix: tighten CTSO-CONF-001, CTSO-TERM-001 (require duration ≥ N) | Removes 2 vacuous passes | ruleset |
| P2 | Engine or rule: dedup multi-section firings of the same rule | Reduces comment noise | engine post-processor or rule selectors |

## 5. Out of scope for this eval (per #62)

- No engine code or rule changes were made for this report.
- This document is the baseline; follow-up issues will be filed per intervention.

---

*Generated by holistic eval against 27 verdicts + 1 gap from `out/sample/report.json`. See `eval-baselines/eval-001-baseline.json` for the full machine-readable snapshot.*


---

## Addendum: final state after fixes (eval-002, ruleset v2.1.0)

After landing the engine + projection + ruleset changes tracked under issue #62, the bundled sample contract review now produces **24 verdicts (2 Pass / 21 Fail / 1 Gap / 0 Error)** — a 100% match to the reviewer column. Snapshot in val-baselines/eval-002-final.json.

### What changed

| Layer | Change | Why |
|---|---|---|
| Engine ( + 'EvaluationService' + ) | Distinguish RulesEngine parse / runtime exceptions from rule failures and emit  + 'VerdictOutcome.Error' +  instead of false-Fail. | Eliminates the silent-error class (CTSO-PRIV-RETENTION had been throwing on  + 'year_count_max' +  when the array was empty). |
| Engine ( + 'EvaluationService' + ) | New  + 'RefineAnchor' +  logic — runs the rule's optional  + 'anchor' +  regex over the section body and falls back to scanning the lambda for  + 'Contains(""…"")' +  literals. | Anchors land on the offending substring (e.g. ""inclusive of applicable taxes"" in 4.2) instead of the heading row. |
| Projection ( + 'DeterministicContractProjector' + ) | Party-alias preprocessing — extracts  + 'Canonical (""Alias"")' +  declarations from the document and rewrites  + 'Alias→Canonical' +  (whole-word) before classification and lambda input.  + 'text_raw' +  preserves the verbatim body. | CTSO-PRIV-72H now matches the §7 clause that uses the defined term ""Company"" for Contoso. |
| Projection ( + 'TextFeatureExtractor' + ) | Always emit  + '*_min' +  /  + '*_max' +  scalars with default 0 (instead of omitting when the array is empty). | Fixes  + 'System.Object' + -vs- + 'System.Int32' +  parse exceptions in lambdas like  + 'year_counts.Count > 0 && year_count_max <= 7' +  (RulesEngine binds operators at compile time, before the  + '&&' +  short-circuit). |
| Projection ( + 'TextFeatureExtractor' + ) | New per-keyword features  + 'dollar_for_cyber' + ,  + 'dollar_for_gcl' + ,  + 'dollar_for_professional' + ,  + 'day_count_for_payment' + ,  + 'day_count_for_notice' +  — extracted at clause granularity. | Avoids asymmetric coverage silently passing (e.g. \ GCL + \ cyber would pass a  + 'dollar_max >= 5_000_000' +  GCL rule — now  + 'dollar_for_gcl' +  catches it). |
| Ruleset (v2.1.0) | Tightened predicates so each rule fires on its operative section only —  + 'CTSO-LIAB-001' +  /  + 'CTSO-LIAB-CARVEOUTS' +  now require  + 'category=="indemnification" || primary_topic=="liability"' + ;  + 'CTSO-TAX-EXCL' +  is restricted to  + 'category=="payment_terms"' + . | Eliminates duplicate verdicts that previously fired on both §9 + §11 (LIAB) and §4 + §5 (TAX-EXCL). |
| Ruleset (v2.1.0) | Rewrote  + 'CTSO-AI-PRIVACY-SUPP' +  without the vacuous-true conditional. New lambda is a direct positive obligation:  + 'Contains(""Privacy T&C"") || Contains(""Privacy Terms"") || Contains(""Privacy Supplement"")' + . | The §3 verdict is now Fail (was a false Pass). |
| Ruleset (v2.1.0) | Strengthened  + 'CTSO-CONF-001' +  (requires actual numeric duration),  + 'CTSO-TERM-001' +  (uses  + 'day_count_for_notice >= 30' + ), and switched  + 'CTSO-INS-CYBER-10M' +  /  + 'CTSO-INS-GCL-5M' +  /  + 'CTSO-PAY-NET45' +  onto the new per-keyword features. | Closes the weak-presence-only and aggregate-feature gaps. |
| Ruleset (v2.1.0) | Added per-rule  + 'anchor' +  regexes on every rule whose lambda is dominantly negative or whose offending substring would not surface from a  + 'Contains' +  literal. | Markup comments now point at the offending substring. |

### Final accuracy

| Metric | Baseline (eval-001) | Final (eval-002) |
|---|---:|---:|
| Verdicts | 28 | 24 (dedup) |
| Outright wrong outcomes | 4 | 0 |
| Silent runtime errors classified as Fail | 1 | 0 |
| Anchor placement issues | ~25 / 28 | 0 |
| Duplicate fires (same rule, same obligation) | 6 verdicts (3 dyads) | 0 |
| Reviewer-aligned outcomes | 22 / 28 (79%) | 24 / 24 (100%) |

### Operational notes

-  + 'tests/Goldens/corpus/**/expected-verdict.json' +  were regenerated — projection JSON shape changed ( + 'text_raw' + ,  + 'text_char_start' + , always-on scalars, new  + 'dollar_for_*' +  /  + 'day_count_for_*' +  keys, alias-resolved  + 'text' + ). The corpus regression bootstrap path was used; verdict outcomes themselves are unchanged. 194 tests still green (179 unit + 15 idempotency).
-  + 'eval-baselines/eval-001-baseline.json' +  is preserved as the historical baseline.
