# Solution Architecture — Departmental Modelling Workbench (Protected B IaaS)

> **Synthetic / public-domain only.** Authored for the lambda-rag golden
> corpus; not derived from any real submission.

## 1. Overview

The Departmental Modelling Workbench provides a high-performance compute
environment for analytical workloads operating on Protected B research
data. It is deployed on Infrastructure-as-a-Service primitives in a
hyperscale commercial cloud, selected via the GC Cloud Brokering Service.

## 2. Phishing-Resistant MFA

All administrative entry to the workbench requires phishing-resistant
MFA enforced at the cloud tenant identity provider. The approved
authenticators are FIDO2 hardware security keys (YubiKey 5 series) for
human administrators, and PIV-derived credentials for federated PSPC
contractors. Username/password and SMS-based factors are explicitly
disabled in tenant policy. Two named global administrators are
registered, with break-glass credentials sealed in the departmental
vault.

## 3. Canadian Data Centre Selection

All workbench compute, storage, and database services are pinned to the
provider's Canada Central region (Toronto), with paired-region failover
to Canada East (Quebec). No data plane component is permitted to provision
outside Canada; this is enforced through Azure Policy / AWS SCP / GCP
Organization Policy depending on the underlying CSP. Cross-region
replication targets are restricted to the Canada Central / Canada East
pair.

## 4. Encryption at Rest

All persistent storage volumes, blob containers, and managed-disk types
are encrypted at rest with AES-256 using FIPS 140-2 validated
cryptographic modules provisioned by the platform key vault. Customer-
managed keys are rotated annually. The key vault is bound to the same
Canadian region as the data and is HSM-backed.

## 5. TLS 1.2 Configuration

All public-facing endpoints terminate TLS at the platform-managed load
balancer with a minimum TLS version of TLS 1.2. TLS 1.3 is preferred
where supported by the client. TLS 1.0 and TLS 1.1 are disabled in
listener policy. Cipher suites are restricted to the CSE-approved subset
documented in ITSP.40.062.

## 6. Audit Log Strategy

Platform-level audit logs are streamed to a central log analytics
workspace. The pipeline ingests sign-in records, role-assignment changes,
and resource-provisioning activities. Detailed retention requirements
are still under review by the departmental records-management office;
the workbench team has reserved capacity for whatever retention period
is ultimately mandated.
