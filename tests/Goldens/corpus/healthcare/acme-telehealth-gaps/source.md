# Information Security Policy — Acme Telehealth Platform, Inc.

**Document type:** Vendor (Business Associate) security policy
**Version:** 1.4 — effective 2026-01-15
**Owner:** Acme Telehealth Platform, Inc. (a SaaS provider handling ePHI on behalf of provider customers)
**Test label:** INTENTIONAL GAPS — for lambda-rag evaluation against `hipaa-security-rule-ruleset.md`

---

## 1. Purpose
Acme Telehealth provides a cloud video-visit and patient-records platform to clinics. This policy describes how Acme protects electronic Protected Health Information (ePHI) processed on the platform.

## 2. Access management
All Acme engineers receive access to the production environment on their first day. Access is provisioned by the DevOps team through our identity provider. Each engineer signs in with their corporate single sign-on credentials.

> *Note: shared "oncall-admin" and "support-readonly" accounts are used by the on-call rotation and the support desk for faster response.*

## 3. Encryption
All patient data and video sessions are encrypted in transit using TLS 1.2. Our application servers communicate with clients over HTTPS only.

## 4. Backups
Production databases are backed up nightly to a second cloud region. Backups are retained for 30 days.

## 5. Security training
New hires complete a 45-minute HIPAA security orientation during onboarding week, covering phishing awareness and password hygiene.

## 6. Risk assessment
Acme performed a security risk assessment of its primary patient-records database in 2024 as part of our SOC 2 Type II audit.

## 7. Incident response
Suspected security incidents are reported to the security@acmetelehealth.example inbox and triaged by the engineering lead on call. Customers are notified of confirmed breaches affecting their data.

## 8. Business associate relationships
Acme will sign a Business Associate Agreement with each covered-entity customer upon request.

---
*End of policy. (Internal reference: ACME-SEC-2026-014)*
