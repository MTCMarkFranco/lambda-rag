# contract corpus — Canadian commercial-contract baseline

## Source attribution

The rules in `ruleset.json` are derived from a composite of public
Canadian commercial-contract baselines:

| Source | What it informs |
|---|---|
| Treasury Board of Canada Secretariat — **Standard Acquisition Clauses and Conditions (SACC) Manual** ([buyandsell.gc.ca](https://buyandsell.gc.ca/policy-and-guidelines/standard-acquisition-clauses-and-conditions-manual)) | Termination, limitation of liability, payment-terms baselines |
| **Treasury Board Directive on Payments** | Net-30 prompt-payment baseline |
| **PIPEDA** (Personal Information Protection and Electronic Documents Act, S.C. 2000, c. 5) | Privacy clause requirements |
| Standard Canadian-law commercial-contract checklists (Canadian Bar Association, CIPS Canada) | Governing-law / venue baselines |

All sources are public Canadian Government of Canada or
publicly-published professional-association material. No customer
content is reproduced.

| Field | Value |
|---|---|
| Topic map | `contract.v1` |
| Sanitisation | None required — public sources only |

## Rules in this set

Five rules covering the recurring high-impact Canadian-commercial-
contract issues:

| ID | Topic | What it checks |
|---|---|---|
| `CAN-CONTRACT-PAY-001` | `payment_terms` | Payment net 30 or shorter |
| `CAN-CONTRACT-GOV-001` | `governing_law` | Governing law is a Canadian jurisdiction |
| `CAN-CONTRACT-PRIVACY-001` | `privacy` | Privacy clause references PIPEDA or substantially similar provincial law |
| `CAN-CONTRACT-LIAB-001` | `liability` | Quantified cap on aggregate liability tied to fees |
| `CAN-CONTRACT-TERM-001` | `termination` | Both termination-for-cause and termination-for-convenience clauses present |

These five were chosen because:

- They are the issues that recur in *every* Canadian commercial-contract
  review training material we examined.
- Each maps onto a topic already in `contract.v1` so no topic-map
  extension was required.
- The pass / fail surface is unambiguous in plain text — important for
  a deterministic regression corpus.

## Documents in this corpus

| Doc id | Purpose | Expected outcomes |
|---|---|---|
| `doc-001-msa-with-gaps` | A US-flavoured MSA dropped onto a Canadian customer — net-45 payment, Delaware governing law, GDPR-only privacy clause, no liability cap, missing termination-for-convenience. | Multiple `Fail` verdicts. Low score. |
| `doc-002-clean-msa` | A clean Canadian-law MSA. All five rules pass. | All `Pass`. Score = 1.0. |

## Relationship to `samples/contracts/ruleset.json`

The bundled `samples/contracts/ruleset.json` shipped before this corpus
was a smaller US-flavoured demo (Delaware, ISO 27001) used as the
running example throughout the README. The corpus ruleset here is a
**Canadian-flavoured replacement** that the test suite uses; the
sample ruleset is unchanged so existing READMEs and quick-start
walkthroughs continue to work.
