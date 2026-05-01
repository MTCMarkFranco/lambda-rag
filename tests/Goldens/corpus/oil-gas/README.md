# Corpus — `oil-gas`

Topic map: **`oil-gas.v1`**.

## Public sources

All rules in `ruleset.json` are derived from public Canadian regulatory
text:

- **Canadian Energy Regulator Onshore Pipeline Regulations (SOR/99-294)**
  ("CER OPR"), in particular ss. 31 (maintenance safety manual), 32
  (emergency management program), and 40 (pipeline integrity management
  program). Public regulation under the *Canadian Energy Regulator Act*.
  - https://laws-lois.justice.gc.ca/eng/regulations/SOR-99-294/
- **Regulations Respecting Reduction in the Release of Methane and
  Certain Volatile Organic Compounds (Upstream Oil and Gas Sector)**,
  SOR/2018-66 — federal methane regulations under CEPA, 1999.
  - https://laws-lois.justice.gc.ca/eng/regulations/SOR-2018-66/
- **Alberta Energy Regulator Directive 071** — *Emergency Preparedness
  and Response Requirements for the Petroleum Industry*. Public AER
  directive under *Responsible Energy Development Act*.
- **Constitution Act, 1982, s. 35** — duty to consult and accommodate
  Indigenous peoples (*Haida Nation*, 2004 SCC 73; affirmed for energy
  projects in *Tsleil-Waututh Nation v. Canada (Attorney General)*,
  2018 FCA 153).

No customer content. Synthetic candidate documents written specifically
to exercise the rules.

## Rules at a glance

| Rule id | Source | Mandate level |
|---|---|---|
| `OG-PIM-001` | CER OPR s. 40 (pipeline integrity management program) | Mandatory |
| `OG-EMERGENCY-001` | CER OPR s. 32 + AER Directive 071 (emergency management program) | Mandatory |
| `OG-METHANE-001` | SOR/2018-66 (federal methane regs) | Mandatory |
| `OG-DECOM-001` | CER OPR + AER Directive 011 (abandonment) | Mandatory |
| `OG-INDIG-001` | s. 35 + *Haida Nation* + *Tsleil-Waututh* | Mandatory |

## Documents

- `doc-001-pipeline-ops-manual-with-gaps/` — operations manual that
  partially addresses CER OPR but is silent on methane and Indigenous
  consultation.
- `doc-002-clean-pipeline-program/` — full program document addressing
  every rule.
