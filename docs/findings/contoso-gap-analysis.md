# Contoso contract: gap-analysis investigation

> Closes [#6 — P0.1 Investigate Contoso contract gap=2 (PAY-001, DPA-001)](../../issues/6).
>
> **Bottom line:** of the two outstanding gaps, **one is a real gap** in the
> source contract and **one is an authoring-side defect** in the rule (lambda
> too narrow + a projector span-scope issue). Both findings are consistent
> with the platform's design intent and neither invalidates the determinism
> claim.

## Inputs reviewed

| Artifact | Path |
|---|---|
| Source contract (parsed) | `out/contoso-test/parsed.json` |
| Topic-map projection | `out/contoso-test/projected.json` |
| End-to-end review report | `out/contoso-test/report.json` |
| Contoso ruleset | `out/contoso-full/contoso-policies-ruleset.json` |

Run summary at the time of investigation:

```
totalRules       18
passed            9
failed            7
gap               2   ← CTSO-2011-000PAY-001  CTSO-2011-003DPA-001
score             0.5000
```

---

## Rule 1 — `CTSO-2011-000PAY-001`  *Payment terms must be 30 days or fewer*

### Projector finding

```
matchedSection : charStart 0,    charLength 2872,  page 1
evidenceQuotes : ["Payment terms"]
applicability  : input1.category == "payment_terms"  →  matched
outcome        : Gap
```

The projector bound to the *first* occurrence of "Payment terms" in the
document — a section heading near the top of the contract that contains
*references* to payment but not the operative obligation.

The operative payment-terms text is much later in the document
(approximately char 14,000+):

> *"Services payment terms. Customer agrees to pay all fees in a Statement
> of Services within **30 calendar days** of the date of invoice…"*

That sentence is in a different section under the same topic and the
current `contract.v1` projector picks the earliest evidence span rather
than the richest one.

### Lambda finding

The rule lambda is:

```text
input1.text.Contains("30 days")
  || input1.text.Contains("15 days")
  || input1.text.Contains("net 30")
  || input1.text.Contains("Net 30")
```

The contract's actual phrasing is **"30 calendar days"**. The substring
`"30 days"` does **not** occur inside `"30 calendar days"`, so even if the
projector had bound to the correct span, the lambda would still return
`false`.

### Verdict

**Authoring-side defect.** Two compounding issues, both fixable without
touching runtime:

1. **Projector span selection** — `contract.v1` should bind the topic to
   the section whose body density of payment-vocabulary terms is highest,
   not the first heading match. (Selection heuristic is documented in
   `src/LambdaRag.Projection/TopicMaps/contract.v1.json`.)
2. **Lambda narrowness** — the keyword list does not anticipate the
   regulator-grade phrasing *"N calendar days"*. A bounded numeric pattern
   (e.g. `\b(?:[1-9]|[12]\d|30)\s+(calendar\s+)?days?\b`) is the correct
   shape for this obligation.

This is not a contract gap. The contract *does* address payment terms
within 30 days. The rule machinery missed it.

> Follow-up tracked in **#44 — Fix `CTSO-2011-000PAY-001` (projector span +
> lambda phrasing)**.

---

## Rule 2 — `CTSO-2011-003DPA-001`  *Data-protection clause must reference an industry security standard*

### Projector finding

```
matchedSection : charStart 5874,  charLength 3039,  page 4
evidenceQuotes : ["data protection"]
applicability  : input1.category == "privacy"  →  matched
outcome        : Gap
```

The projector correctly bound to the contract's *Privacy and Security*
block (a single coherent section spanning ~3 KB).

### Lambda finding

The rule lambda is:

```text
input1.text.Contains("ISO 27001")
  || input1.text.Contains("SOC 2")
  || input1.text.Contains("NIST")
```

The bound section's actual content references:

- Customer responsibility to comply with applicable privacy / breach
  notification law
- *EU Safe Harbor* / *Swiss Safe Harbor* frameworks (legacy mechanisms)
- A privacy-statement URL on `microsoft.com/licensing/servicecenter`
- Cross-border transfer language

It does **not** reference any of `ISO 27001`, `SOC 2`, or `NIST`.

### Verdict

**Real gap in the source contract** with respect to the Contoso policy.
The Microsoft MSA boilerplate predates the modern certification-reference
norm and uses regulatory-framework language instead of attestation
language. From the perspective of an Contoso policy that requires a named
industry standard, this is a substantive finding.

The rule's verdict is correct, the remediation text is correct, and there
is no defect in either the projector or the lambda.

> No follow-up issue. Closing PAY in `--mode markup` will continue to
> surface this as a gap, which is the desired behaviour.

---

## Idempotency / determinism check

Both gap rows reproduced byte-identically across re-runs in
`out/contoso-full/report.run1.json` vs `report.run2.json` (modulo the CTSO-test
vs CTSO-full input differential — which is a *different* contract / ruleset
combo, also stable run-to-run). The idempotency claim is unaffected by
this investigation.

## What this finding tells us about the architecture

This is exactly the regime the platform was designed for:

- The runtime is deterministic — same input → same gap → same audit row.
- The *quality* of a gap is a function of two **authoring-time** assets:
  the **projector** (does it bind the topic to the right span?) and the
  **lambda** (does it accept all defensible phrasings of the obligation?).
- Both are versioned, reviewable artifacts in git.
- A wrong gap is fixed by editing those artifacts and re-running — never
  by mutating a verdict at runtime, never by an in-place rule editor.

The PAY-001 finding is therefore a *test case for the topic-map authoring
workflow*, not a hole in the platform. DPA-001 is the platform working as
intended.

---

*Investigated 2026-04-30 as part of Phase 0 credibility close-out.*
