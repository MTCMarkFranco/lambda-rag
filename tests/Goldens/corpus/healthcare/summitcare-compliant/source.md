# Business Associate Agreement & Security Addendum — SummitCare Cloud Services, LLC

**Document type:** Business Associate Agreement (BAA) + Security Safeguards Addendum
**Version:** 3.0 — effective 2026-03-01
**Parties:** SummitCare Cloud Services, LLC ("Business Associate") and Covered Entity customer
**Test label:** CLEAN / COMPLIANT — for lambda-rag evaluation against `hipaa-security-rule-ruleset.md`

---

## Part A — Business Associate Agreement terms
1. **Permitted uses.** Business Associate (BA) shall use or disclose ePHI only as permitted by this Agreement or required by law, and shall not use or disclose ePHI in a manner that would violate the HIPAA Privacy or Security Rule if done by the Covered Entity.
2. **Safeguards.** BA shall implement administrative, physical, and technical safeguards that reasonably and appropriately protect the confidentiality, integrity, and availability of ePHI, as detailed in Part B.
3. **Subcontractors.** BA shall ensure that any subcontractor that creates, receives, maintains, or transmits ePHI on its behalf agrees in writing to the same restrictions and conditions (flow-down BAA).
4. **Breach notification.** BA shall report any breach of unsecured ePHI to the Covered Entity without unreasonable delay and no later than 30 calendar days after discovery.
5. **Termination.** Upon termination, BA shall return or destroy all ePHI where feasible; where infeasible, protections continue for as long as BA retains the ePHI.

## Part B — Security Safeguards Addendum

### B1. Security Management Process
SummitCare conducts a **documented, organization-wide risk analysis** annually and after any material system change, covering every system that creates, receives, maintains, or transmits ePHI. Identified risks are tracked to remediation in a risk register reviewed quarterly by the Security Officer *(maps: H-1, H-2)*.

### B2. Sanction Policy
Workforce members who violate security policies are subject to a documented progressive disciplinary process up to and including termination. Violations and sanctions are logged by HR *(maps: H-2)*.

### B3. Workforce Access — Authorization, Least Privilege, Termination
Access to ePHI is **role-based and granted on the principle of least privilege**. Access requests require manager and Security Officer approval. Upon role change or separation, access is **revoked within 4 hours** through an automated deprovisioning workflow tied to the HR system *(maps: H-3)*.

### B4. Security Awareness & Training
All workforce members complete **HIPAA security training at hire and annually thereafter**, supplemented by quarterly phishing simulations and periodic security reminders. Completion is tracked and enforced *(maps: H-4)*.

### B5. Contingency Plan
SummitCare maintains: (a) a **data backup plan** — encrypted backups every 6 hours, replicated cross-region, retained 90 days; (b) a **disaster recovery plan** with defined RTO of 4 hours and RPO of 6 hours, tested semi-annually; and (c) an **emergency-mode operation plan** ensuring continued access to critical ePHI during outages *(maps: H-5)*.

### B6. Access Control
Every user has a **unique identifier**; shared or generic accounts are prohibited. Sessions enforce **automatic logoff after 15 minutes** of inactivity. ePHI is encrypted at rest using AES-256 *(maps: H-6, H-8)*.

### B7. Audit Controls
All systems containing ePHI generate **immutable audit logs** of access and modification events. Logs are centrally aggregated and **reviewed weekly** by the security team, with automated alerting on anomalous access *(maps: H-7)*.

### B8. Transmission Security
ePHI in transit is protected with **TLS 1.3** and integrity controls. ePHI at rest is encrypted with AES-256. Remote administrative access requires VPN plus multi-factor authentication *(maps: H-8)*.

---
*Signed on behalf of SummitCare Cloud Services, LLC. (Internal reference: SCC-BAA-3.0)*
