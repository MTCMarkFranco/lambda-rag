# Test plan for v1.2.0 review week

## 1. Sanity check — same doc, same verdict (determinism)

Run the CTC doc twice, diff the reports. Should be byte-identical.

```powershell
cd C:\projects\lambda-rag
dotnet run --project src/LambdaRag.Cli -- review `
  --document "samples\architecture\Example PSA - 16614 Shipping 360 ARB2 v2.1.pdf" `
  --ruleset rs_architecture_v1 `
  --out out\test-run-1

dotnet run --project src/LambdaRag.Cli -- review `
  --document "samples\architecture\Example PSA - 16614 Shipping 360 ARB2 v2.1.pdf" `
  --ruleset rs_architecture_v1 `
  --out out\test-run-2

fc /b out\test-run-1\report.json out\test-run-2\report.json
```

✅ **Look for:** "no differences encountered" — this is Pillar 1 (determinism) working.

## 2. Diagnostic — see the PDF parse quality

```powershell
dotnet run --project src/LambdaRag.Cli -- dump-tree `
  --document "samples\architecture\Example PSA - 16614 Shipping 360 ARB2 v2.1.pdf" `
  --out out\ctc-tree.json

dotnet run --project src/LambdaRag.Cli -- dump-tree `
  --document "docs\ARCHITECTURE.md" `
  --out out\arch-tree.json
```

👀 **Look for:** The PDF tree will have ~7 junk "heading" nodes with 200+ char titles — that's #173 in the flesh. The MD tree will be clean. This tells you where the PDF review is being noisy at the source.

## 3. Real testing — throw new docs at it

Pick 2–3 architecture docs you haven't run through yet (PDF or MD). For each:

```powershell
dotnet run --project src/LambdaRag.Cli -- review `
  --document "<path>" `
  --ruleset rs_architecture_v1 `
  --out out\<doc-name>
```

Then open `report.json` and check:

| What to inspect | Good signal | Bad signal / bug to file |
|---|---|---|
| **PASS verdicts** | Real evidence citations from the doc | Cited text is unrelated to the rule → false positive |
| **FAIL verdicts** | Rule genuinely not addressed | Rule IS addressed but lambda-rag missed it → recall miss |
| **N/A verdicts** | Rule truly doesn't apply to this doc type | Rule applies but got skipped → scope bug |
| **Evidence snippets** | Coherent sentences | Junk fragments from PDF heading noise → symptom of #173 |
| **PASS/FAIL/N/A ratio** | Roughly matches your gut read of the doc | Wildly skewed one way → either ruleset or parser issue |

## 4. Cross-check against your own judgment

For 3–5 verdicts per doc, ask: **"Do I agree with this verdict, and is the cited evidence the right evidence?"** That's the only ground truth that matters.

## What to record

Keep a lightweight log (even a text file):
- Doc name + page count
- Total PASS / FAIL / N/A counts
- Any verdict you disagree with → note rule ID, verdict, and why
- Any junk evidence snippet → note rule ID + snippet (feeds #173)

That log becomes the input to v1.3.0 planning next week.

**One thing to keep in mind:** on PDFs, some verdicts will look off because of #173. That's expected and *known*. On markdown, verdicts should be much cleaner — good doc for calibrating trust.