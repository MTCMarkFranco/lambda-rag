"""Phase 4 helper: attach a factSchema to the ruleset and rewrite ~20 rules
to evaluationMode='facts'. Idempotent — safe to rerun. Overwrites the
existing rewrites."""
import json, sys, copy

P = r"rulesets/architecture-review/architecture-v1.json"
with open(P, "r", encoding="utf-8") as f:
    rs = json.load(f)

# ── factSchema ─────────────────────────────────────────────────────────────
schema = {
    "id": "ea-v1-facts",
    "version": "1",
    "concepts": [
        {"name": "encryption_declared", "type": "Boolean",
         "description": "Section discusses encryption of some data/state as an operational requirement."},
        {"name": "encryption_at_rest",  "type": "Boolean",
         "description": "Section requires or discusses encryption of data at rest specifically."},
        {"name": "encryption_in_transit", "type": "Boolean",
         "description": "Section requires or discusses encryption of data in transit."},
        {"name": "encryption_algorithm", "type": "Enum",
         "description": "Named symmetric-encryption algorithm the section refers to.",
         "enumValues": ["AES-256", "AES-128", "ChaCha20-Poly1305", "AES-GCM"]},
        {"name": "key_rotation_days", "type": "Integer",
         "description": "Maximum key-rotation cadence in days the section stipulates."},
        {"name": "mfa_required", "type": "Boolean",
         "description": "Section requires multi-factor authentication."},
        {"name": "tls_min_version", "type": "Enum",
         "description": "Minimum TLS version the section requires.",
         "enumValues": ["1.0", "1.1", "1.2", "1.3"]},
        {"name": "data_classification", "type": "Enum",
         "description": "Named data classification level the section refers to.",
         "enumValues": ["Public", "Internal", "Confidential", "Restricted"]},
        {"name": "storage_region", "type": "Text",
         "description": "Verbatim region/geography the section pins storage to."},
        {"name": "residency_boundary_declared", "type": "Boolean",
         "description": "Section declares a data-residency or storage-region boundary."},
        {"name": "logging_enabled", "type": "Boolean",
         "description": "Section requires that some logs be captured/enabled."},
        {"name": "logging_retention_days", "type": "Integer",
         "description": "Log retention period in days the section stipulates."},
        {"name": "backup_declared", "type": "Boolean",
         "description": "Section discusses backup or DR of data."},
        {"name": "rto_hours", "type": "Integer",
         "description": "Recovery Time Objective the section stipulates, in hours."},
        {"name": "rpo_hours", "type": "Integer",
         "description": "Recovery Point Objective the section stipulates, in hours."},
    ],
}
rs["factSchema"] = schema

# ── rule rewrites ──────────────────────────────────────────────────────────
# Each entry: rule id → (lambda, requiredFacts).
# Lambda syntax follows RulesEngine's C#-style expression grammar; the
# evaluator gates any null RequiredFact before invocation so we can
# safely dereference (see EvaluationService.EvaluateFactRuleAsync).
rewrites = {
    # Encryption
    "EA-AKS-013":  ("facts.encryption_at_rest == true",
                    ["encryption_at_rest"]),
    "EA-DATA-002": ("facts.encryption_in_transit == true",
                    ["encryption_in_transit"]),
    "EA-DATA-003": ('facts.tls_min_version == "1.3" || facts.tls_min_version == "1.2"',
                    ["tls_min_version"]),
    "EA-DATA-007": ('(facts.data_classification == "Confidential" || facts.data_classification == "Restricted") && facts.encryption_declared == true',
                    ["data_classification", "encryption_declared"]),
    # Key rotation (compound: encryption + rotation cadence)
    "EA-IAM-010":  ("facts.key_rotation_days <= 90",
                    ["key_rotation_days"]),
    "EA-SECR-003": ("facts.encryption_at_rest == true && facts.key_rotation_days <= 365",
                    ["encryption_at_rest", "key_rotation_days"]),
    # MFA
    "EA-IAM-004":  ("facts.mfa_required == true",
                    ["mfa_required"]),
    "EA-IAM-035":  ("facts.mfa_required == true",
                    ["mfa_required"]),
    "EA-IAM-016":  ("facts.mfa_required == true",
                    ["mfa_required"]),
    # Classification
    "EA-PRIV-001": ('facts.data_classification == "Public" || facts.data_classification == "Internal" || facts.data_classification == "Confidential" || facts.data_classification == "Restricted"',
                    ["data_classification"]),
    "EA-DATA-006": ('facts.data_classification == "Confidential" || facts.data_classification == "Restricted"',
                    ["data_classification"]),
    # Residency
    "EA-COMP-003": ("facts.residency_boundary_declared == true",
                    ["residency_boundary_declared"]),
    "EA-PRIV-003": ("facts.residency_boundary_declared == true",
                    ["residency_boundary_declared"]),
    "EA-PRIV-004": ("facts.residency_boundary_declared == true",
                    ["residency_boundary_declared"]),
    # Logging (compound: enabled + retention)
    "EA-AUDIT-001": ("facts.logging_enabled == true",
                     ["logging_enabled"]),
    "EA-AKS-008":   ("facts.logging_enabled == true",
                     ["logging_enabled"]),
    "EA-AKS-009":   ("facts.logging_enabled == true && facts.logging_retention_days >= 30",
                     ["logging_enabled", "logging_retention_days"]),
    # Backup / DR
    "EA-AKS-005":   ("facts.backup_declared == true",
                     ["backup_declared"]),
    "EA-AKS-006":   ("facts.backup_declared == true",
                     ["backup_declared"]),
    "EA-AKS-007":   ("facts.backup_declared == true && facts.rpo_hours <= 24",
                     ["backup_declared", "rpo_hours"]),
}

by_id = {r["id"]: r for r in rs["rules"]}
rewritten = 0
for rid, (lam, req) in rewrites.items():
    r = by_id.get(rid)
    if not r:
        print(f"  MISS {rid}")
        continue
    r["evaluationMode"] = "facts"
    r["lambda"] = lam
    r["requiredFacts"] = req
    # Rules in fact-mode don't use the classic selector/predicate/gate,
    # but keep the fields as-is so byte-identity of pre-Pillar-12 rules
    # is untouched; the evaluator ignores them for fact-mode rules.
    rewritten += 1

with open(P, "w", encoding="utf-8", newline="\n") as f:
    json.dump(rs, f, indent=2, ensure_ascii=False)

print(f"Wrote {P}")
print(f"Rewrote {rewritten}/{len(rewrites)} rules to evaluationMode=facts")
print(f"factSchema: {len(schema['concepts'])} concepts")
