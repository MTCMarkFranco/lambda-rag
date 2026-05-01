# Solution Architecture — Reference Compliant Cloud Workload (Protected B PaaS)

> **Synthetic / public-domain only.** Authored for the lambda-rag golden
> corpus as a positive-control reference architecture.

## 1. Overview

This is a reference solution architecture demonstrating how a Protected
B departmental workload can be deployed on commercial Platform-as-a-
Service primitives in a manner that satisfies the Government of Canada
Cloud Guardrails v2.0. It is intentionally minimal but correct on every
guardrail covered by the golden corpus.

## 2. Phishing-Resistant MFA

All administrative access — including the two registered global
administrators — is gated by phishing-resistant MFA. The supported
authenticators are FIDO2 security keys and PIV-derived smart card
credentials issued under the departmental PKI. Password-based and
SMS-based authentication are disabled in conditional access policy.

## 3. Canadian Data Centre Selection

Every data-plane resource — compute, managed databases, blob storage,
key vaults, and log analytics workspaces — is pinned to the provider's
Canada Central region (Toronto), with a designated paired failover
region of Canada East (Quebec). Provisioning outside Canadian data
centre regions is blocked by tenant-level policy.

## 4. Encryption at Rest

All persistent storage is encrypted at rest with AES-256 using a FIPS
140-2 validated cryptographic module. Customer-managed keys are stored
in an HSM-backed key vault in Canada Central with annual key rotation
and dual-control administrative access.

## 5. TLS 1.2 Configuration

All ingress traffic terminates with a minimum negotiated TLS version of
TLS 1.2; TLS 1.3 is preferred and offered first. TLS 1.0 and TLS 1.1 are
explicitly disabled. Cipher suites are restricted to the CSE-approved
subset.

## 6. Audit Log Strategy

Platform audit logs are streamed to a centralised log analytics
workspace and retained for 365 days online with a further 7 years in
tiered immutable storage. The log pipeline captures sign-in events
(interactive and non-interactive), configuration changes, and resource
provisioning activities. Two named information-security personnel are
registered as the platform security contacts.
