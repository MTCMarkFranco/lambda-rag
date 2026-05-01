# Solution Architecture — ACME-Canada Citizen Engagement Portal (Protected B SaaS)

> **Synthetic / public-domain only.** This document was authored from
> scratch for the lambda-rag golden corpus. It deliberately contains a
> mix of compliant and non-compliant language drawn from typical
> first-draft GC departmental architecture submissions. No real
> customer or departmental content is reproduced.

## 1. Overview

This document describes the proposed architecture for the ACME-Canada
Citizen Engagement Portal, a Protected B Software-as-a-Service offering
that will collect, store, and process feedback from Canadian residents
on departmental programs.

The system is hosted on a major commercial cloud provider and is being
submitted for departmental security assessment under SPIN 2017-01.

## 2. Phishing-Resistant MFA

All administrative access to the portal control plane is gated by
multi-factor authentication. The MFA solution has been designated
"phishing-resistant" by the vendor in marketing literature. Time-based
one-time passwords (TOTP) delivered via the vendor's mobile authenticator
app are the standard second factor for all administrators. SMS-based MFA
remains available as a backup for administrators travelling without
their primary device.

The vendor confirms that all MFA methods are "modern and secure" but the
specific cryptographic protocol bound to the authenticator has not been
documented in this revision of the architecture.

## 3. Encryption at Rest

Customer data is encrypted at rest using the cloud provider's
platform-default storage encryption. The provider documentation
indicates that "industry-standard encryption" is applied to all blobs,
queues, and managed disks. The exact algorithm and key length are
managed entirely by the platform and are not customer-configurable in
this tier.

A future revision of this architecture will evaluate customer-managed
keys; for the initial launch we accept the platform-default posture.

## 4. Network Encryption

Inbound traffic to the public web tier terminates at the provider's
managed application gateway, which negotiates the highest TLS version
the client supports. Internal east-west traffic between the web tier and
the application tier traverses the provider's private virtual network
and is therefore considered trusted.

Legacy partner integrations require backwards-compatible cipher suites;
the gateway is configured to fall back to TLS 1.0 if no later version is
negotiated successfully.

## 5. Audit Log Retention

Platform audit log streams are enabled by default. Operations staff have
visibility into administrative activity through the cloud provider's
console. Logs are retained "for the lifetime of the workload" per the
provider's standard offering.
