# Rationale — `doc-001-pipeline-ops-manual-with-gaps`

## What this document tests

A real-world-shaped operations manual that addresses the *operational*
parts of CER OPR (PIM, ERP, asset integrity) but is **silent on**:

- methane management / LDAR (federal SOR/2018-66)
- abandonment financial assurance (CER + AER D-011)
- Indigenous consultation (s. 35)

## Expected pattern (locked into `expected-verdict.json`)

- `OG-PIM-001` — **Pass.** *Pipeline Integrity Management Program*
  section names the program and CER OPR.
- `OG-EMERGENCY-001` — **Pass.** *Emergency Response Plan* section
  cites CER OPR s. 32.
- `OG-METHANE-001` — **Gap.** No methane / LDAR / SOR-2018-66 reference.
- `OG-DECOM-001` — **Fail.** *Decommissioning* section explicitly
  defers financial assurance.
- `OG-INDIG-001` — **Gap.** No Indigenous consultation reference.

## Public-source attribution

- CER OPR, SOR/99-294
- SOR/2018-66 (methane regulations)
- AER Directive 071 / 011
- Constitution Act, 1982, s. 35
