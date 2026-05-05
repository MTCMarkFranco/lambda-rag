// ---------------------------------------------------------------------------
// Contoso Financial Services — Azure Microservices Architecture Document
// Generates: Cloud-service-Architecture-Filled.docx
// Uses: docx@9.x  (npm install -g docx)
// ---------------------------------------------------------------------------
"use strict";
const {
  Document, Packer, Paragraph, TextRun, Table, TableRow, TableCell,
  Header, Footer, HeadingLevel, AlignmentType, LevelFormat,
  BorderStyle, WidthType, ShadingType, VerticalAlign, PageNumber,
  PageBreak, TableOfContents, ExternalHyperlink,
  UnderlineType
} = require("docx");
const fs = require("fs");
const path = require("path");

// ── helpers ────────────────────────────────────────────────────────────────
const DXA = (inches) => Math.round(inches * 1440);
const PAGE_W = DXA(8.5);
const PAGE_H = DXA(11);
const MARGIN = DXA(1);
const CONTENT_W = PAGE_W - 2 * MARGIN; // 9360

const BLUE  = "1F4E79";
const LBLUE = "2E75B6";
const TBLUE = "D5E8F0";
const THEAD = "1F4E79";
const GRAY  = "F2F2F2";
const DGRAY = "595959";
const WHITE = "FFFFFF";

const borderCell = { style: BorderStyle.SINGLE, size: 6, color: "CCCCCC" };
const borders = { top: borderCell, bottom: borderCell, left: borderCell, right: borderCell };

function h(level, text, bookmarkId) {
  const children = bookmarkId
    ? [new TextRun({ text, bold: true })]
    : [new TextRun(text)];
  return new Paragraph({
    heading: level,
    children,
    spacing: { before: 240, after: 120 },
  });
}

function p(text, opts = {}) {
  return new Paragraph({
    spacing: { after: 120 },
    children: [new TextRun({ text, ...opts })],
  });
}

function bold(text) { return new TextRun({ text, bold: true }); }
function run(text, opts = {}) { return new TextRun({ text, ...opts }); }

function bullet(text, level = 0) {
  return new Paragraph({
    numbering: { reference: "bullets", level },
    spacing: { after: 80 },
    children: [new TextRun(text)],
  });
}

function subBullet(text) { return bullet(text, 1); }

function numbered(text, level = 0) {
  return new Paragraph({
    numbering: { reference: "numbers", level },
    spacing: { after: 80 },
    children: [new TextRun(text)],
  });
}

function spacer(pts = 120) {
  return new Paragraph({ spacing: { after: pts }, children: [] });
}

function pageBreak() {
  return new Paragraph({ children: [new PageBreak()] });
}

function sectionRule() {
  return new Paragraph({
    spacing: { after: 200 },
    border: { bottom: { style: BorderStyle.SINGLE, size: 6, color: LBLUE, space: 1 } },
    children: [],
  });
}

// Two-column table row helper
function twoCol(left, right, lw = 4680, rw = 4680) {
  return new TableRow({
    children: [
      new TableCell({
        borders, width: { size: lw, type: WidthType.DXA },
        margins: { top: 80, bottom: 80, left: 120, right: 120 },
        children: [new Paragraph({ children: [new TextRun(left)] })],
      }),
      new TableCell({
        borders, width: { size: rw, type: WidthType.DXA },
        margins: { top: 80, bottom: 80, left: 120, right: 120 },
        children: [new Paragraph({ children: [new TextRun(right)] })],
      }),
    ],
  });
}

function headerRow(...cols) {
  const colW = Math.floor(CONTENT_W / cols.length);
  return new TableRow({
    tableHeader: true,
    children: cols.map((c) =>
      new TableCell({
        borders,
        width: { size: colW, type: WidthType.DXA },
        shading: { fill: THEAD, type: ShadingType.CLEAR },
        margins: { top: 80, bottom: 80, left: 120, right: 120 },
        verticalAlign: VerticalAlign.CENTER,
        children: [new Paragraph({
          children: [new TextRun({ text: c, bold: true, color: WHITE })],
        })],
      })
    ),
  });
}

function dataRow(cells, widths, shade = false) {
  return new TableRow({
    children: cells.map((c, i) =>
      new TableCell({
        borders,
        width: { size: widths[i], type: WidthType.DXA },
        shading: { fill: shade ? GRAY : WHITE, type: ShadingType.CLEAR },
        margins: { top: 80, bottom: 80, left: 120, right: 120 },
        children: [new Paragraph({ children: [new TextRun(c)] })],
      })
    ),
  });
}

function table(rows) {
  return new Table({
    width: { size: CONTENT_W, type: WidthType.DXA },
    rows,
  });
}

// ── numbering config ───────────────────────────────────────────────────────
const numbering = {
  config: [
    {
      reference: "bullets",
      levels: [
        { level: 0, format: LevelFormat.BULLET, text: "\u2022",
          alignment: AlignmentType.LEFT,
          style: { paragraph: { indent: { left: 720, hanging: 360 } } } },
        { level: 1, format: LevelFormat.BULLET, text: "\u25E6",
          alignment: AlignmentType.LEFT,
          style: { paragraph: { indent: { left: 1080, hanging: 360 } } } },
      ],
    },
    {
      reference: "numbers",
      levels: [
        { level: 0, format: LevelFormat.DECIMAL, text: "%1.",
          alignment: AlignmentType.LEFT,
          style: { paragraph: { indent: { left: 720, hanging: 360 } } } },
      ],
    },
  ],
};

// ── styles ────────────────────────────────────────────────────────────────
const styles = {
  default: {
    document: { run: { font: "Arial", size: 22 } },
  },
  paragraphStyles: [
    {
      id: "Heading1", name: "Heading 1", basedOn: "Normal", next: "Normal", quickFormat: true,
      run: { size: 36, bold: true, font: "Arial", color: BLUE },
      paragraph: { spacing: { before: 360, after: 200 }, outlineLevel: 0 },
    },
    {
      id: "Heading2", name: "Heading 2", basedOn: "Normal", next: "Normal", quickFormat: true,
      run: { size: 28, bold: true, font: "Arial", color: LBLUE },
      paragraph: { spacing: { before: 280, after: 160 }, outlineLevel: 1 },
    },
    {
      id: "Heading3", name: "Heading 3", basedOn: "Normal", next: "Normal", quickFormat: true,
      run: { size: 24, bold: true, font: "Arial", color: DGRAY },
      paragraph: { spacing: { before: 200, after: 120 }, outlineLevel: 2 },
    },
  ],
};

// ===========================================================================
// DOCUMENT SECTIONS
// ===========================================================================

// ── Cover Page ─────────────────────────────────────────────────────────────
function coverPage() {
  return [
    spacer(DXA(1.5) / 12),
    new Paragraph({
      alignment: AlignmentType.CENTER,
      spacing: { after: 240 },
      children: [new TextRun({
        text: "CONTOSO FINANCIAL SERVICES", bold: true, size: 52,
        font: "Arial", color: BLUE,
      })],
    }),
    new Paragraph({
      alignment: AlignmentType.CENTER,
      spacing: { after: 120 },
      children: [new TextRun({
        text: "Cloud Services Architecture Document", bold: true, size: 40,
        font: "Arial", color: LBLUE,
      })],
    }),
    new Paragraph({
      alignment: AlignmentType.CENTER,
      spacing: { after: 80 },
      children: [new TextRun({
        text: "Azure Microservices Platform — v1.0", size: 28,
        font: "Arial", color: DGRAY,
      })],
    }),
    spacer(600),
    table([
      headerRow("Date", "Version", "Description", "Author"),
      dataRow(["2025-09-10", "0.1", "Initial draft — scope and goals", "Cloud Architecture Team"], [2000, 1500, 4000, 1860], false),
      dataRow(["2025-10-15", "0.2", "Candidate architecture — networking, ACA, CosmosDB", "Cloud Architecture Team"], [2000, 1500, 4000, 1860], true),
      dataRow(["2025-11-20", "0.3", "Security model: Entra ID, Key Vault, RBAC", "Cloud Architecture Team"], [2000, 1500, 4000, 1860], false),
      dataRow(["2025-12-18", "0.4", "Deployment model, observability, DR strategy", "Cloud Architecture Team"], [2000, 1500, 4000, 1860], true),
      dataRow(["2026-01-22", "0.5", "Data model, performance benchmarks", "Cloud Architecture Team"], [2000, 1500, 4000, 1860], false),
      dataRow(["2026-02-14", "0.6", "QA review pass — all sections", "Cloud Architecture Team"], [2000, 1500, 4000, 1860], true),
      dataRow(["2026-03-05", "1.0", "Final release — approved by Architecture Review Board", "Cloud Architecture Team"], [2000, 1500, 4000, 1860], false),
    ]),
    new Paragraph({
      alignment: AlignmentType.CENTER,
      spacing: { before: 480, after: 120 },
      children: [new TextRun({
        text: "CLASSIFICATION: INTERNAL — RESTRICTED", bold: true, size: 18,
        font: "Arial", color: "C00000",
      })],
    }),
    pageBreak(),
  ];
}

// ── TOC ────────────────────────────────────────────────────────────────────
function tocSection() {
  return [
    new TableOfContents("Table of Contents", {
      hyperlink: true,
      headingStyleRange: "1-3",
    }),
    pageBreak(),
  ];
}

// ── Section 1: Introduction ────────────────────────────────────────────────
function section1() {
  return [
    h(HeadingLevel.HEADING_1, "1. Introduction"),
    p("This Cloud Services Architecture Document (CSAD) provides a comprehensive architectural overview of Contoso Financial Services' next-generation digital banking platform built on Microsoft Azure. The platform adopts a microservices architecture hosted on Azure Container Apps (ACA), supported by CosmosDB for global-scale data persistence, Azure App Configuration and Key Vault for configuration and secrets management, and a defence-in-depth security model based on Microsoft Entra ID and Azure RBAC."),
    p("This document covers all significant architectural decisions, trade-offs, and rationale made during the Elaboration and Construction phases of the programme. It is the authoritative reference for engineers, security reviewers, cloud operations, and the Architecture Review Board (ARB)."),

    h(HeadingLevel.HEADING_2, "1.1 Purpose"),
    p("The purpose of this document is to:"),
    bullet("Provide a single authoritative record of the target-state architecture for the Azure microservices platform."),
    bullet("Communicate architectural decisions and their rationale to all stakeholders."),
    bullet("Serve as a baseline for security review, infrastructure provisioning, and operational run-books."),
    bullet("Guide engineering teams during implementation, integration, and QA phases."),

    h(HeadingLevel.HEADING_2, "1.2 Scope"),
    p("The scope of this document encompasses:"),
    bullet("The Azure Microservices Platform consisting of ten domain-aligned microservices deployed on Azure Container Apps."),
    bullet("Azure Virtual Network topology, including hub-spoke design, private endpoints, and network security groups."),
    bullet("Azure Cosmos DB for NoSQL as the primary data store for all transactional microservices."),
    bullet("Azure App Configuration and Azure Key Vault for centralised configuration and secret management."),
    bullet("Microsoft Entra ID (Azure AD) for identity, authentication, and RBAC-based authorisation."),
    bullet("Observability: Azure Monitor, Application Insights, and Log Analytics."),
    bullet("Disaster recovery strategy targeting RPO 15 minutes / RTO 30 minutes."),
    p("Out of scope: end-user front-end applications, mainframe integration connectors, and legacy on-premises infrastructure."),

    h(HeadingLevel.HEADING_2, "1.3 Definitions, Acronyms and Abbreviations"),
    table([
      headerRow("Term / Acronym", "Definition"),
      dataRow(["ACA", "Azure Container Apps — managed serverless container platform"], [2000, 7360], false),
      dataRow(["ARB", "Architecture Review Board"], [2000, 7360], true),
      dataRow(["CSAD", "Cloud Services Architecture Document"], [2000, 7360], false),
      dataRow(["DDoS", "Distributed Denial-of-Service"], [2000, 7360], true),
      dataRow(["DR", "Disaster Recovery"], [2000, 7360], false),
      dataRow(["Entra ID", "Microsoft Entra ID (formerly Azure Active Directory)"], [2000, 7360], true),
      dataRow(["Hub-Spoke", "Azure Virtual Network topology pattern with a central hub VNet and peripheral spoke VNets"], [2000, 7360], false),
      dataRow(["KV", "Azure Key Vault"], [2000, 7360], true),
      dataRow(["MI", "Managed Identity — Azure workload identity requiring no stored credentials"], [2000, 7360], false),
      dataRow(["NSG", "Network Security Group — Azure layer-4 stateful firewall"], [2000, 7360], true),
      dataRow(["PE", "Private Endpoint — private IP address for an Azure PaaS service inside a VNet"], [2000, 7360], false),
      dataRow(["RBAC", "Role-Based Access Control"], [2000, 7360], true),
      dataRow(["RPO", "Recovery Point Objective"], [2000, 7360], false),
      dataRow(["RTO", "Recovery Time Objective"], [2000, 7360], true),
      dataRow(["TLS", "Transport Layer Security"], [2000, 7360], false),
      dataRow(["WAF", "Web Application Firewall"], [2000, 7360], true),
      dataRow(["Zero-Trust", "Security model that assumes breach and verifies every request explicitly"], [2000, 7360], false),
    ]),

    h(HeadingLevel.HEADING_2, "1.4 References"),
    bullet("Microsoft Azure Well-Architected Framework — https://learn.microsoft.com/azure/architecture/framework"),
    bullet("Azure Container Apps documentation — https://learn.microsoft.com/azure/container-apps"),
    bullet("Azure Cosmos DB best practices — https://learn.microsoft.com/azure/cosmos-db"),
    bullet("Microsoft Entra ID documentation — https://learn.microsoft.com/entra/identity"),
    bullet("NIST SP 800-53 Rev 5 — Security and Privacy Controls for Information Systems"),
    bullet("Contoso Financial Services Information Security Policy v3.2 (internal)"),
    bullet("Contoso Cloud Governance Framework v2.0 (internal)"),
    bullet("Azure Landing Zone — Enterprise-Scale reference architecture"),

    h(HeadingLevel.HEADING_2, "1.5 Overview"),
    p("Section 2 describes the architectural views used to document the system. Section 3 details architectural goals and constraints. Section 4 presents the primary use-case scenarios that drive architectural decisions. Section 5 is the Logical View covering the microservices decomposition and layer model. Section 6 is the Process View covering asynchronous messaging and concurrency. Section 7 is the Deployment View with the Azure topology and DR configuration. Section 8 is the Implementation View. Section 9 is the Data View. Sections 10 and 11 address performance and quality."),
    pageBreak(),
  ];
}

// ── Section 2: Architectural Representation ───────────────────────────────
function section2() {
  return [
    h(HeadingLevel.HEADING_1, "2. Architectural Representation"),
    p('This document follows the "4+1" view model of software architecture adapted for cloud-native systems on Azure. Each view targets a specific set of stakeholders and concerns:'),

    new Paragraph({
      spacing: { before: 200, after: 80 },
      children: [bold("Logical View"), run(" — Audience: Solution architects, senior engineers. Describes the microservices decomposition, domain ownership, inter-service contracts, and key abstractions.")],
    }),
    new Paragraph({
      spacing: { after: 80 },
      children: [bold("Process View"), run(" — Audience: Integration engineers, SREs. Describes asynchronous event flows, message brokers, concurrency patterns, and saga orchestration.")],
    }),
    new Paragraph({
      spacing: { after: 80 },
      children: [bold("Deployment View"), run(" — Audience: Cloud operations, platform engineers. Describes the Azure resource topology, virtual networking, regions, and DR configuration.")],
    }),
    new Paragraph({
      spacing: { after: 80 },
      children: [bold("Implementation View"), run(" — Audience: Developers, DevOps. Describes the container images, CI/CD pipelines, configuration injection, and layer structure.")],
    }),
    new Paragraph({
      spacing: { after: 80 },
      children: [bold("Use-Case View"), run(" — Audience: All stakeholders. Describes the scenarios with the most significant impact on the architecture.")],
    }),
    new Paragraph({
      spacing: { after: 80 },
      children: [bold("Data View"), run(" — Audience: Data engineers, DBAs. Describes the Cosmos DB data model, partitioning strategy, and data ownership per microservice.")],
    }),
    spacer(),
    table([
      headerRow("Architectural Activity", "CSAD Section"),
      dataRow(["Identify and prioritise significant use-cases", "Section 4"], [6240, 3120], false),
      dataRow(["Define candidate architecture — goals and constraints", "Section 3, 5.1, 10, 11"], [6240, 3120], true),
      dataRow(["Define initial Deployment Model", "Section 7"], [6240, 3120], false),
      dataRow(["Identify key abstractions and domain model", "Section 9"], [6240, 3120], true),
      dataRow(["Create the Logical/Analysis Model", "Section 5"], [6240, 3120], false),
      dataRow(["Create the Design Model (services, contracts)", "Section 5"], [6240, 3120], true),
      dataRow(["Document concurrency and async mechanisms", "Section 6, 7"], [6240, 3120], false),
      dataRow(["Create the Implementation Model", "Section 8"], [6240, 3120], true),
    ]),
    pageBreak(),
  ];
}

// ── Section 3: Architectural Goals and Constraints ────────────────────────
function section3() {
  return [
    h(HeadingLevel.HEADING_1, "3. Architectural Goals and Constraints"),
    p("This section describes the requirements and objectives with significant impact on the architecture."),

    h(HeadingLevel.HEADING_2, "3.1 Technical Platform"),
    p("The Contoso Financial Services microservices platform is deployed exclusively on Microsoft Azure, anchored in the Canada Central region (primary) and Canada East region (secondary DR). The following Azure services form the core technical platform:"),
    table([
      headerRow("Azure Service", "Role in Architecture"),
      dataRow(["Azure Container Apps (ACA)", "Managed serverless container runtime for all microservices. Provides built-in autoscaling (KEDA), Dapr sidecar support, and ingress management via Azure Front Door."], [2800, 6560], false),
      dataRow(["Azure Cosmos DB for NoSQL", "Primary transactional data store. Globally distributed, multi-region writes enabled for active-active HA. Each microservice owns a dedicated Cosmos DB database."], [2800, 6560], true),
      dataRow(["Azure App Configuration", "Centralised feature flags and non-secret runtime configuration. Integrated with ACA via App Configuration provider."], [2800, 6560], false),
      dataRow(["Azure Key Vault (Premium tier)", "Secrets management for connection strings, certificates, API keys, and encryption keys. HSM-backed keys for PCI-DSS compliance."], [2800, 6560], true),
      dataRow(["Microsoft Entra ID", "Identity plane. Provides OAuth 2.0 / OIDC authentication, Managed Identities for workloads, and RBAC authorisation across all Azure resources."], [2800, 6560], false),
      dataRow(["Azure Front Door (Premium)", "Global HTTP/HTTPS load balancer with WAF policy, DDoS protection, and TLS offload at the edge."], [2800, 6560], true),
      dataRow(["Azure Service Bus (Premium)", "Fully managed enterprise message broker. Used for domain event publishing and saga choreography between microservices."], [2800, 6560], false),
      dataRow(["Azure Container Registry (ACR)", "Private OCI-compliant container registry. Geo-replicated across Canada Central and Canada East."], [2800, 6560], true),
      dataRow(["Azure Monitor / Application Insights", "Unified observability: metrics, distributed tracing, log aggregation, and alerting."], [2800, 6560], false),
    ]),

    h(HeadingLevel.HEADING_2, "3.2 Transaction"),
    p("Individual microservices are transactionally consistent within their own Cosmos DB database using optimistic concurrency (ETag-based). Cross-service transactions are handled via the Saga pattern using Azure Service Bus. Each saga step either completes successfully or publishes a compensating transaction event to reverse prior steps. No distributed two-phase commit is used."),
    p("Idempotency keys are required on all POST and PATCH operations to support safe retries during transient failures."),

    h(HeadingLevel.HEADING_2, "3.3 Security"),
    p("The platform enforces a Zero-Trust security posture across all layers. The key security controls are:"),

    h(HeadingLevel.HEADING_3, "3.3.1 Identity and Authentication"),
    bullet("All user-facing APIs require JWT bearer tokens issued by Microsoft Entra ID (OAuth 2.0 Authorization Code Flow with PKCE)."),
    bullet("Service-to-service communication uses Managed Identity (MI) tokens — no shared secrets or stored credentials."),
    bullet("Multi-Factor Authentication (MFA) is mandatory for all privileged Entra ID accounts."),
    bullet("Conditional Access policies enforce device compliance and location-based controls for all administrative access."),

    h(HeadingLevel.HEADING_3, "3.3.2 Authorisation and RBAC"),
    bullet("Azure RBAC is applied at the Management Group, Subscription, Resource Group, and individual resource levels, following the principle of least privilege."),
    bullet("Custom RBAC roles are defined for platform engineers, application developers, security operations, and read-only auditors."),
    bullet("Application-level authorisation uses Entra ID App Roles and group-based claims propagated via JWT."),
    subBullet("Banking Operations role: full read/write on account and transaction services."),
    subBullet("Compliance Auditor role: read-only access to audit log service."),
    subBullet("Platform Engineer role: infrastructure management with no access to customer data."),

    h(HeadingLevel.HEADING_3, "3.3.3 Network Security"),
    bullet("All microservices run in a VNet-integrated Azure Container Apps Environment with no public IP."),
    bullet("Azure Front Door with WAF (OWASP 3.2 rule set) is the sole public ingress point."),
    bullet("All PaaS services (Cosmos DB, Key Vault, Service Bus, App Configuration, ACR) are accessible only via Private Endpoints inside the VNet — public access is disabled."),
    bullet("Network Security Groups (NSGs) enforce micro-segmentation between subnet tiers."),
    bullet("Azure DDoS Network Protection is enabled at the VNet level."),
    bullet("All inter-service traffic is TLS 1.2+ encrypted. TLS 1.0 and 1.1 are blocked at the NSG and Front Door policy levels."),

    h(HeadingLevel.HEADING_3, "3.3.4 Secrets Management"),
    bullet("No secrets, connection strings, or certificates are stored in environment variables, source code, or container images."),
    bullet("All secrets are stored in Azure Key Vault. Microservices retrieve secrets at startup via the ACA Key Vault secret store (CSI driver / Dapr Secret Store component)."),
    bullet("Key rotation is automated using Key Vault rotation policies. Application downtime during rotation is prevented by versioned secret references."),
    bullet("Key Vault diagnostic logs are streamed to Log Analytics. Alerts are configured for any failed access attempt."),

    h(HeadingLevel.HEADING_3, "3.3.5 Compliance"),
    p("The security architecture is designed to satisfy the following compliance frameworks:"),
    bullet("PCI DSS v4.0 — for cardholder data environments."),
    bullet("SOC 2 Type II — security, availability, and confidentiality."),
    bullet("OSFI E-23 (Technology and Cyber Risk Management) — Canadian federal financial regulation."),
    bullet("ISO/IEC 27001:2022 — information security management."),

    h(HeadingLevel.HEADING_2, "3.4 Persistence"),
    p("Azure Cosmos DB for NoSQL is the sole persistent data store for all microservices. The following design decisions apply:"),
    bullet("Database-per-service pattern: each microservice owns an isolated Cosmos DB database to enforce loose coupling and independent schema evolution."),
    bullet("Container (collection) design follows the single-partition-key pattern optimised for the primary read patterns of each service."),
    bullet("Cosmos DB analytical store is enabled for reporting workloads, fed into Azure Synapse Analytics via Synapse Link to avoid OLTP query pressure."),
    bullet("Geo-redundancy: multi-region writes across Canada Central (primary) and Canada East (secondary). Automatic failover is configured with priority 0 for Canada Central."),
    bullet("Server-side backups: continuous backup mode with point-in-time restore (PITR) up to 30 days."),

    h(HeadingLevel.HEADING_2, "3.5 Reliability and Availability"),
    p("Target SLA: 99.95% monthly availability for all customer-facing APIs."),
    bullet("Azure Container Apps provides automatic horizontal scaling via KEDA rules (HTTP request rate, Service Bus queue depth, CPU utilisation)."),
    bullet("Minimum replica count of 3 is enforced for all production microservices to avoid cold-start latency."),
    bullet("Cosmos DB multi-region write configuration provides automatic failover with < 5 minutes RTO for regional outages."),
    bullet("Azure Front Door provides global load balancing and instant traffic rerouting on origin health failures."),
    bullet("Circuit breaker pattern (via Dapr resiliency policies) prevents cascading failures across microservice dependencies."),
    bullet("DR runbooks are tested quarterly via Azure Chaos Studio fault injection experiments."),
    p("RPO: 15 minutes. RTO: 30 minutes. Both targets apply to a full regional failure scenario."),

    h(HeadingLevel.HEADING_2, "3.6 Performance"),
    p("Performance targets for production:"),
    table([
      headerRow("API / Operation", "Target P99 Latency", "Target Throughput"),
      dataRow(["Customer authentication (login)", "< 300 ms", "500 req/s"], [3500, 2800, 3060], false),
      dataRow(["Account balance inquiry", "< 150 ms", "2,000 req/s"], [3500, 2800, 3060], true),
      dataRow(["Funds transfer initiation", "< 500 ms", "300 req/s"], [3500, 2800, 3060], false),
      dataRow(["Transaction history (paginated)", "< 400 ms", "800 req/s"], [3500, 2800, 3060], true),
      dataRow(["Payment processing (end-to-end saga)", "< 3,000 ms", "150 req/s"], [3500, 2800, 3060], false),
    ]),
    p("Cosmos DB request units (RUs) are provisioned with autoscale enabled. Baseline provisioning is 4,000 RU/s per database with autoscale ceiling of 40,000 RU/s."),

    h(HeadingLevel.HEADING_2, "3.7 Internationalisation (i18n)"),
    p("The platform API layer is locale-agnostic (English-only for v1.0). All user-facing content is managed by the separate BFF (Backend-for-Frontend) tier and is out of scope for this document. All date-time values in API contracts use ISO 8601 UTC format. Monetary amounts use ISO 4217 currency codes."),
    pageBreak(),
  ];
}

// ── Section 4: Use-Case View ───────────────────────────────────────────────
function section4() {
  return [
    h(HeadingLevel.HEADING_1, "4. Use-Case View"),
    p("The following use-case scenarios have the most significant impact on the microservices architecture and the security model."),

    h(HeadingLevel.HEADING_2, "4.1 Customer Authentication and Authorisation"),
    p("A retail banking customer authenticates via the Contoso mobile application. The mobile app redirects to the Entra ID hosted login page (Authorization Code + PKCE flow). Upon successful MFA verification, Entra ID issues an access token and a refresh token. The access token contains the customer's object ID and assigned App Roles (e.g., RetailBanking.Read, RetailBanking.Transfer). The mobile BFF validates the token against the Entra ID JWKS endpoint and forwards requests to downstream microservices with the token in the Authorization header. Each microservice independently validates the token signature, expiry, and required scope/role before processing the request."),

    h(HeadingLevel.HEADING_2, "4.2 Funds Transfer"),
    p("An authenticated customer initiates a funds transfer from Account A to Account B. The Payments API accepts the request, validates the JWT, performs an idempotency check (Cosmos DB), and publishes a TransferInitiated event to Azure Service Bus. The Account Service consumes the event, deducts the balance from Account A (Cosmos optimistic concurrency), and publishes an AccountDebited event. The Payments API consumes AccountDebited, credits Account B, and publishes TransferCompleted. If any step fails, a compensating event is published and the saga rolls back via reverse operations. The customer receives a push notification via the Notification Service once TransferCompleted is received."),

    h(HeadingLevel.HEADING_2, "4.3 Configuration and Secrets Bootstrap"),
    p("On startup, each ACA microservice container retrieves its non-secret runtime configuration from Azure App Configuration using the ACA-managed App Configuration provider, authenticated via Managed Identity. Secrets (DB connection strings, encryption keys) are retrieved from Azure Key Vault via the Dapr Secret Store component, also using Managed Identity. The workload Managed Identity is granted only the specific Key Vault Secret Get permission for the secrets it requires — not the full Key Vault Secrets Officer role. This prevents any single compromised workload from accessing secrets owned by other services."),

    h(HeadingLevel.HEADING_2, "4.4 Security Incident — Suspicious Transaction Detection"),
    p("The Fraud Detection Service subscribes to all TransactionCreated events on Service Bus. It evaluates each transaction against ML-based risk models. If a transaction exceeds the risk threshold, the service publishes a TransactionFlagged event, which triggers: (a) suspension of the transaction in the Payments Service, (b) creation of an audit record in the Compliance Service, and (c) an alert to the operations team via Azure Monitor Action Groups. All actions are logged to the immutable Log Analytics workspace. The Compliance Auditor role can query audit records via the Compliance API but cannot modify them."),

    h(HeadingLevel.HEADING_2, "4.5 Deployment and Configuration Change"),
    p("A platform engineer pushes a new container image to Azure Container Registry. The GitHub Actions CI/CD pipeline runs unit, integration, and security scans (Microsoft Defender for DevOps). On passing all gates, the pipeline triggers an ACA revision update. ACA performs a rolling deployment with zero-downtime traffic splitting (10% canary for 10 minutes, then 100%). New feature flags are deployed to Azure App Configuration ahead of the code change and remain disabled until activated via the App Configuration UI or API by an authorised feature owner."),
    pageBreak(),
  ];
}

// ── Section 5: Logical View ───────────────────────────────────────────────
function section5() {
  return [
    h(HeadingLevel.HEADING_1, "5. Logical View"),

    h(HeadingLevel.HEADING_2, "5.1 Overview — Microservices Decomposition"),
    p("The platform is decomposed into ten domain-aligned microservices, each independently deployable and operating against its own Cosmos DB database. Services communicate synchronously over HTTPS (REST/JSON) for query operations and asynchronously via Azure Service Bus for state-changing domain events."),
    p("The following layer model governs the logical structure:"),
    bullet("Edge Layer: Azure Front Door (WAF, TLS termination, global routing)"),
    bullet("API Gateway Layer: APIM (Azure API Management) — rate limiting, JWT validation, API versioning"),
    bullet("Application Layer: ACA-hosted microservices (domain logic)"),
    bullet("Messaging Layer: Azure Service Bus (event-driven integration between services)"),
    bullet("Data Layer: Azure Cosmos DB for NoSQL (per-service database)"),
    bullet("Infrastructure Services: Key Vault, App Configuration, ACR, Log Analytics"),

    h(HeadingLevel.HEADING_2, "5.2 Microservices Catalogue"),
    table([
      headerRow("Service Name", "Domain", "Primary Responsibility", "ACA Environment"),
      dataRow(["Identity Service", "IAM", "Token validation, user profile caching, Entra ID integration", "Production"], [2200, 1500, 4200, 1460], false),
      dataRow(["Account Service", "Core Banking", "Account lifecycle, balance management, account statements", "Production"], [2200, 1500, 4200, 1460], true),
      dataRow(["Payments Service", "Payments", "Funds transfer initiation, saga orchestration, idempotency", "Production"], [2200, 1500, 4200, 1460], false),
      dataRow(["Cards Service", "Cards", "Card issuance, limits, virtual card generation", "Production"], [2200, 1500, 4200, 1460], true),
      dataRow(["Notification Service", "Notifications", "Push, SMS, and email notifications via Azure Communication Services", "Production"], [2200, 1500, 4200, 1460], false),
      dataRow(["Fraud Detection Service", "Risk", "Real-time transaction risk scoring, rule engine + ML model inference", "Production"], [2200, 1500, 4200, 1460], true),
      dataRow(["Compliance Service", "Regulatory", "Immutable audit log, regulatory report generation", "Production"], [2200, 1500, 4200, 1460], false),
      dataRow(["Configuration Service", "Platform", "Aggregates App Configuration feature flags and serves to BFF", "Production"], [2200, 1500, 4200, 1460], true),
      dataRow(["API Gateway BFF", "Presentation", "Backend-for-Frontend; aggregates calls, enforces scopes, formats responses for mobile/web", "Production"], [2200, 1500, 4200, 1460], false),
      dataRow(["Reporting Service", "Analytics", "Synapse Link queries, pre-aggregated reports, regulatory submissions", "Production"], [2200, 1500, 4200, 1460], true),
    ]),

    h(HeadingLevel.HEADING_2, "5.3 Inter-Service Communication"),
    p("Synchronous (REST over HTTPS):"),
    bullet("BFF -> Identity Service: token introspection, user profile"),
    bullet("BFF -> Account Service: balance inquiry, statement retrieval"),
    bullet("BFF -> Cards Service: card management"),
    bullet("All synchronous calls go via APIM, which enforces JWT validation and rate limiting."),
    p("Asynchronous (Azure Service Bus topics/subscriptions):"),
    bullet("Payments Service publishes: TransferInitiated, TransferCompleted, TransferFailed"),
    bullet("Account Service publishes: AccountDebited, AccountCredited, BalanceThresholdReached"),
    bullet("Cards Service publishes: CardIssued, CardBlocked, LimitChanged"),
    bullet("Fraud Detection Service publishes: TransactionFlagged, RiskScoreComputed"),
    bullet("Notification Service subscribes to: TransferCompleted, CardIssued, TransactionFlagged"),
    bullet("Compliance Service subscribes to: ALL domain events (dead-letter audit trail)"),

    h(HeadingLevel.HEADING_2, "5.4 Cross-Cutting Concerns"),
    new Paragraph({
      spacing: { after: 80 },
      children: [bold("Observability:"), run(" All microservices instrument OpenTelemetry traces and push to Application Insights. Structured logs use the Elastic Common Schema (ECS) format and are ingested into Log Analytics. Custom dashboards in Azure Monitor Workbooks provide real-time service health.")],
    }),
    new Paragraph({
      spacing: { after: 80 },
      children: [bold("Resiliency:"), run(" Dapr resiliency policies implement per-service circuit breakers (5 failures in 10 s = open), exponential retry with jitter (max 3 retries, base delay 500 ms), and timeout policies (5 s default).")],
    }),
    new Paragraph({
      spacing: { after: 80 },
      children: [bold("API Versioning:"), run(" All APIs use URI versioning (/v1/, /v2/). Breaking changes require a new major version. APIM enforces sunset headers on deprecated versions.")],
    }),
    new Paragraph({
      spacing: { after: 80 },
      children: [bold("Health Checks:"), run(" All containers expose /health/ready and /health/live endpoints consumed by ACA liveness/readiness probes.")],
    }),
    pageBreak(),
  ];
}

// ── Section 6: Process View ───────────────────────────────────────────────
function section6() {
  return [
    h(HeadingLevel.HEADING_1, "6. Process View"),
    p("This section describes concurrency mechanisms and asynchronous processing patterns."),

    h(HeadingLevel.HEADING_2, "6.1 Asynchronous Event Architecture"),
    p("Azure Service Bus Premium tier is used for all inter-service domain events. The following topology is in place:"),
    bullet("One Service Bus namespace per environment (dev, staging, production)."),
    bullet("Topics are named by domain event (e.g., contoso.payments.transfer, contoso.accounts.balance)."),
    bullet("Each consuming service has a dedicated subscription with a filter on the event type header."),
    bullet("Dead-letter queues (DLQ) are monitored by the Compliance Service. Any message landing in a DLQ triggers a P2 alert and an incident in the on-call system."),
    bullet("Session-enabled queues are used for saga correlation to ensure ordered processing of events belonging to the same transfer ID."),

    h(HeadingLevel.HEADING_2, "6.2 Saga Pattern — Funds Transfer"),
    p("The Funds Transfer saga follows the choreography pattern:"),
    numbered("Payments Service receives POST /v1/transfers, writes a Pending transaction record (Cosmos), and publishes TransferInitiated to Service Bus."),
    numbered("Account Service consumes TransferInitiated, applies debit to Account A with ETag-based optimistic concurrency, and publishes AccountDebited."),
    numbered("Payments Service consumes AccountDebited, applies credit to Account B, and publishes AccountCredited."),
    numbered("Payments Service marks the transaction as Completed and publishes TransferCompleted."),
    numbered("Notification Service and Compliance Service consume TransferCompleted independently."),
    p("Compensation flow on failure:"),
    numbered("If Account Service fails to debit (e.g., insufficient funds), it publishes TransferRejected with the reason code."),
    numbered("Payments Service consumes TransferRejected and marks the transaction as Failed."),
    numbered("Notification Service sends a failure notification to the customer."),

    h(HeadingLevel.HEADING_2, "6.3 Scaling and Concurrency"),
    p("Azure Container Apps uses KEDA (Kubernetes Event-Driven Autoscaling) with the following scaling rules per service:"),
    table([
      headerRow("Service", "Scale Trigger", "Min Replicas", "Max Replicas"),
      dataRow(["BFF / API Gateway", "HTTP Requests per second > 100", "3", "20"], [3000, 3500, 1300, 1560], false),
      dataRow(["Payments Service", "Service Bus queue depth > 20", "3", "15"], [3000, 3500, 1300, 1560], true),
      dataRow(["Account Service", "HTTP RPS + CPU > 70%", "3", "15"], [3000, 3500, 1300, 1560], false),
      dataRow(["Fraud Detection Service", "Service Bus queue depth > 10", "2", "10"], [3000, 3500, 1300, 1560], true),
      dataRow(["Notification Service", "Service Bus queue depth > 50", "2", "8"], [3000, 3500, 1300, 1560], false),
      dataRow(["Compliance Service", "Service Bus queue depth > 100", "2", "5"], [3000, 3500, 1300, 1560], true),
    ]),
    pageBreak(),
  ];
}

// ── Section 7: Deployment View ────────────────────────────────────────────
function section7() {
  return [
    h(HeadingLevel.HEADING_1, "7. Deployment View"),

    h(HeadingLevel.HEADING_2, "7.1 Azure Regions and Environments"),
    p("The platform spans two Azure regions in a primary / secondary configuration:"),
    table([
      headerRow("Environment", "Azure Region", "Purpose"),
      dataRow(["Production (Primary)", "Canada Central", "Live customer traffic"], [3000, 3000, 3360], false),
      dataRow(["Production (Secondary)", "Canada East", "Active DR — warm standby, Cosmos DB multi-write"], [3000, 3000, 3360], true),
      dataRow(["Staging", "Canada Central", "Pre-production validation, performance testing"], [3000, 3000, 3360], false),
      dataRow(["Development", "Canada Central", "Feature development, integration testing"], [3000, 3000, 3360], true),
    ]),

    h(HeadingLevel.HEADING_2, "7.2 Hub-Spoke Virtual Network Topology"),
    p("A hub-spoke VNet architecture is deployed in each region. The hub VNet contains shared networking infrastructure; each spoke VNet is dedicated to a specific workload or environment tier."),

    h(HeadingLevel.HEADING_3, "7.2.1 Hub VNet (Canada Central) — 10.0.0.0/16"),
    table([
      headerRow("Subnet", "CIDR", "Contents"),
      dataRow(["AzureFirewallSubnet", "10.0.0.0/26", "Azure Firewall Premium (forced-tunnel all egress)"], [2500, 1800, 5060], false),
      dataRow(["GatewaySubnet", "10.0.1.0/27", "VPN Gateway / ExpressRoute Gateway"], [2500, 1800, 5060], true),
      dataRow(["AzureBastionSubnet", "10.0.2.0/26", "Azure Bastion for secure administrative RDP/SSH"], [2500, 1800, 5060], false),
      dataRow(["SharedServicesSubnet", "10.0.3.0/24", "Log Analytics workspace, DNS resolver"], [2500, 1800, 5060], true),
    ]),

    h(HeadingLevel.HEADING_3, "7.2.2 Production Spoke VNet — 10.1.0.0/16"),
    table([
      headerRow("Subnet", "CIDR", "Contents", "NSG"),
      dataRow(["aca-environment-subnet", "10.1.1.0/23", "ACA Environment (all microservices — VNet integrated)", "nsg-aca-prod"], [2200, 1400, 3800, 1960], false),
      dataRow(["apim-subnet", "10.1.3.0/24", "Azure API Management (internal mode)", "nsg-apim-prod"], [2200, 1400, 3800, 1960], true),
      dataRow(["private-endpoints-subnet", "10.1.4.0/24", "Private Endpoints: Cosmos DB, Key Vault, Service Bus, App Config, ACR", "nsg-pe-prod"], [2200, 1400, 3800, 1960], false),
      dataRow(["frontdoor-origin-subnet", "10.1.5.0/27", "Origin for Azure Front Door private link", "nsg-fd-prod"], [2200, 1400, 3800, 1960], true),
    ]),

    h(HeadingLevel.HEADING_2, "7.3 Network Security Group Rules"),
    p("Key NSG rules enforcing micro-segmentation (deny-all default, allow-list exceptions):"),
    table([
      headerRow("NSG", "Rule Name", "Direction", "Source", "Destination", "Port", "Action"),
      dataRow(["nsg-aca-prod", "AllowAPIMInbound", "Inbound", "apim-subnet", "aca-env-subnet", "443", "Allow"], [1400, 2000, 1000, 1400, 1400, 800, 760], false),
      dataRow(["nsg-aca-prod", "AllowACAtoCosmosDB", "Outbound", "aca-env-subnet", "pe-subnet", "443", "Allow"], [1400, 2000, 1000, 1400, 1400, 800, 760], true),
      dataRow(["nsg-aca-prod", "AllowACAtoKeyVault", "Outbound", "aca-env-subnet", "pe-subnet", "443", "Allow"], [1400, 2000, 1000, 1400, 1400, 800, 760], false),
      dataRow(["nsg-aca-prod", "AllowACAtoServiceBus", "Outbound", "aca-env-subnet", "pe-subnet", "5671, 443", "Allow"], [1400, 2000, 1000, 1400, 1400, 800, 760], true),
      dataRow(["nsg-aca-prod", "DenyAllInbound", "Inbound", "Any", "Any", "Any", "Deny"], [1400, 2000, 1000, 1400, 1400, 800, 760], false),
      dataRow(["nsg-pe-prod", "AllowACAtoAllPE", "Inbound", "aca-env-subnet", "pe-subnet", "443", "Allow"], [1400, 2000, 1000, 1400, 1400, 800, 760], true),
      dataRow(["nsg-pe-prod", "DenyPublicInbound", "Inbound", "Internet", "pe-subnet", "Any", "Deny"], [1400, 2000, 1000, 1400, 1400, 800, 760], false),
    ]),

    h(HeadingLevel.HEADING_2, "7.4 RBAC Role Assignments"),
    p("The following Azure RBAC role assignments govern access to the production environment:"),
    table([
      headerRow("Principal (Group / MI)", "Scope", "Role", "Justification"),
      dataRow(["grp-platform-engineers", "Subscription", "Contributor", "Infrastructure provisioning via IaC (Terraform)"], [2400, 1800, 2200, 2960], false),
      dataRow(["grp-app-developers", "Resource Group: rg-prod-app", "Reader + ACA Contributor", "Deploy container revisions, read logs"], [2400, 1800, 2200, 2960], true),
      dataRow(["grp-security-ops", "Management Group", "Security Reader + Defender for Cloud Reader", "Security posture monitoring"], [2400, 1800, 2200, 2960], false),
      dataRow(["grp-compliance-auditors", "Log Analytics Workspace", "Log Analytics Reader", "Audit log queries — read-only"], [2400, 1800, 2200, 2960], true),
      dataRow(["mi-payments-svc", "Key Vault: kv-contoso-prod", "Key Vault Secrets User (specific secrets only)", "Retrieve Payments DB connection string"], [2400, 1800, 2200, 2960], false),
      dataRow(["mi-account-svc", "Key Vault: kv-contoso-prod", "Key Vault Secrets User (specific secrets only)", "Retrieve Account DB connection string"], [2400, 1800, 2200, 2960], true),
      dataRow(["mi-all-services", "App Configuration: appconfig-contoso-prod", "App Configuration Data Reader", "Read feature flags at startup"], [2400, 1800, 2200, 2960], false),
      dataRow(["mi-all-services", "Cosmos DB Account", "Cosmos DB Built-in Data Contributor (own DB only)", "Read/write to own database — enforced via resource scope"], [2400, 1800, 2200, 2960], true),
      dataRow(["mi-acr-pull", "ACR: contosoacr.azurecr.io", "AcrPull", "Pull container images at deploy time"], [2400, 1800, 2200, 2960], false),
    ]),

    h(HeadingLevel.HEADING_2, "7.5 Disaster Recovery"),
    p("The DR strategy follows an Active-Warm Standby model:"),
    bullet("Cosmos DB multi-region writes are active in both Canada Central and Canada East. In the event of a primary region failure, automatic failover redirects all writes to Canada East within 5 minutes."),
    bullet("Azure Container Apps Environments in Canada East run all microservices at minimum replica count (3) and are ready to serve traffic immediately."),
    bullet("Azure Front Door health probes continuously check the APIM health endpoint in both regions. On primary region failure, Front Door routes 100% of traffic to Canada East within 60 seconds."),
    bullet("Azure Key Vault and App Configuration are geo-redundant within each region and replicated to the paired region by the service."),
    bullet("Azure Container Registry is geo-replicated to Canada East to ensure pull availability during regional failover."),
    bullet("DR drills are conducted quarterly using Azure Chaos Studio experiments targeting ACA replica failures, Cosmos DB regional failover, and Service Bus namespace failover."),
    pageBreak(),
  ];
}

// ── Section 8: Implementation View ───────────────────────────────────────
function section8() {
  return [
    h(HeadingLevel.HEADING_1, "8. Implementation View"),

    h(HeadingLevel.HEADING_2, "8.1 Overview"),
    p("Each microservice is packaged as a Docker container image, built and pushed to Azure Container Registry via GitHub Actions CI/CD pipelines. Container images are based on the official .NET 9 or Node.js 22 LTS slim images, depending on the service's implementation language. All images are scanned for CVEs by Microsoft Defender for Containers before being promoted to production."),

    h(HeadingLevel.HEADING_2, "8.2 Container Image Standards"),
    bullet("Base images: mcr.microsoft.com/dotnet/aspnet:9.0-alpine (C# services), node:22-alpine (Node.js services)."),
    bullet("Non-root user: all containers run as UID 1001 (non-root). ACA SecurityContext enforces runAsNonRoot."),
    bullet("Read-only root filesystem: enabled for all services. Writable volumes are mounted only for /tmp."),
    bullet("Image tags: SHA-pinned digests in production. :latest tag is prohibited in production ACA revisions."),
    bullet("Image scanning: Microsoft Defender for Containers scans on push to ACR and on ACA deploy. Critical CVEs block deployment."),

    h(HeadingLevel.HEADING_2, "8.3 Layers"),
    table([
      headerRow("Layer", "Technology", "Responsibilities"),
      dataRow(["Edge", "Azure Front Door Premium + WAF", "DDoS, WAF OWASP 3.2, TLS 1.3 termination, global routing"], [1800, 3000, 4560], false),
      dataRow(["API Management", "Azure APIM (internal VNet)", "JWT validation, rate limiting, API versioning, subscription keys"], [1800, 3000, 4560], true),
      dataRow(["Application", "Azure Container Apps (ACA)", "Domain microservice logic, Dapr sidecars, KEDA autoscaling"], [1800, 3000, 4560], false),
      dataRow(["Messaging", "Azure Service Bus Premium", "Topic-based pub/sub, session queues, DLQ management"], [1800, 3000, 4560], true),
      dataRow(["Data", "Azure Cosmos DB for NoSQL", "Transactional persistence, optimistic concurrency, Change Feed"], [1800, 3000, 4560], false),
      dataRow(["Configuration", "Azure App Configuration + Key Vault", "Feature flags, non-secret config, secrets, certificates"], [1800, 3000, 4560], true),
      dataRow(["Identity", "Microsoft Entra ID", "OAuth 2.0 / OIDC, Managed Identities, Conditional Access"], [1800, 3000, 4560], false),
      dataRow(["Observability", "App Insights + Log Analytics + Azure Monitor", "Distributed traces, metrics, logs, alerting, dashboards"], [1800, 3000, 4560], true),
    ]),

    h(HeadingLevel.HEADING_2, "8.4 CI/CD Pipeline"),
    p("All microservices use a shared GitHub Actions pipeline template with the following stages:"),
    numbered("Build & Unit Test: dotnet build / npm ci, xUnit / Jest unit tests, code coverage gate (>= 80%)."),
    numbered("Static Analysis: SonarQube SAST scan, Microsoft Defender for DevOps secret scan, licence compliance check."),
    numbered("Container Build: docker buildx build --platform linux/amd64, push to ACR dev tag."),
    numbered("Integration Test: deploy to ACA dev environment, run API contract tests (Pact), smoke tests."),
    numbered("Security Scan: Microsoft Defender for Containers CVE scan on ACR image. Block on Critical severity."),
    numbered("Staging Promotion: push image with :staging tag, deploy to ACA staging environment, run load tests."),
    numbered("Production Deployment: Canary revision (10%) for 10 minutes, automated Canary analysis (error rate < 0.1%, P99 < SLA), then full rollout."),
    pageBreak(),
  ];
}

// ── Section 9: Data View ──────────────────────────────────────────────────
function section9() {
  return [
    h(HeadingLevel.HEADING_1, "9. Data View"),
    p("Each microservice owns a dedicated Azure Cosmos DB database. No cross-service joins are performed at the database level. Aggregated views are built via the Reporting Service using Synapse Link."),

    h(HeadingLevel.HEADING_2, "9.1 Cosmos DB Resource Layout"),
    table([
      headerRow("Service", "Cosmos DB Database", "Key Container(s)", "Partition Key", "RU/s (Autoscale Max)"),
      dataRow(["Account Service", "accounts-db", "accounts, statements", "/accountId", "40,000"], [2000, 2000, 2500, 1500, 1360], false),
      dataRow(["Payments Service", "payments-db", "transactions, idempotency-keys", "/transferId", "40,000"], [2000, 2000, 2500, 1500, 1360], true),
      dataRow(["Cards Service", "cards-db", "cards, card-events", "/cardId", "20,000"], [2000, 2000, 2500, 1500, 1360], false),
      dataRow(["Identity Service", "identity-db", "user-profiles, sessions", "/userId", "10,000"], [2000, 2000, 2500, 1500, 1360], true),
      dataRow(["Compliance Service", "compliance-db", "audit-events", "/correlationId", "10,000"], [2000, 2000, 2500, 1500, 1360], false),
      dataRow(["Fraud Detection", "fraud-db", "risk-scores, model-inputs", "/transactionId", "20,000"], [2000, 2000, 2500, 1500, 1360], true),
      dataRow(["Notification Service", "notifications-db", "notification-log", "/userId", "10,000"], [2000, 2000, 2500, 1500, 1360], false),
    ]),

    h(HeadingLevel.HEADING_2, "9.2 Data Ownership and Governance"),
    bullet("Each service's Managed Identity is granted Cosmos DB Built-in Data Contributor role scoped to its own database resource only. No service can read or write to another service's database."),
    bullet("Customer PII fields (name, address, date of birth) are encrypted at the application layer using Azure Key Vault-managed encryption keys before being stored in Cosmos DB. Field-level encryption uses AES-256-GCM."),
    bullet("The retention policy for the audit-events container in the Compliance Service is 7 years (regulatory requirement). TTL is disabled on this container."),
    bullet("The idempotency-keys container in the Payments Service has a TTL of 24 hours."),
    bullet("Cosmos DB Change Feed is consumed by the Reporting Service to maintain real-time aggregated views without querying OLTP containers."),
    bullet("Azure Purview is used for data classification and lineage tracking across Cosmos DB, Service Bus, and Synapse Analytics."),

    h(HeadingLevel.HEADING_2, "9.3 Backup and Recovery"),
    bullet("Cosmos DB continuous backup mode is enabled with PITR to any point within the last 30 days."),
    bullet("Backup restore operations require approval from two members of the Platform Engineering team (dual-control policy)."),
    bullet("Monthly restore drills are conducted to validate RPO and to confirm backup integrity."),
    pageBreak(),
  ];
}

// ── Section 10: Size and Performance ─────────────────────────────────────
function section10() {
  return [
    h(HeadingLevel.HEADING_1, "10. Size and Performance"),

    h(HeadingLevel.HEADING_2, "10.1 Expected Load"),
    table([
      headerRow("Metric", "Expected Value (Year 1)", "Peak Multiplier"),
      dataRow(["Registered users", "500,000", "N/A"], [3500, 3000, 2860], false),
      dataRow(["Daily active users", "80,000", "N/A"], [3500, 3000, 2860], true),
      dataRow(["Peak concurrent sessions", "12,000", "3x on month-end"], [3500, 3000, 2860], false),
      dataRow(["Transactions per day", "2,000,000", "5x on salary day"], [3500, 3000, 2860], true),
      dataRow(["API requests per second (peak)", "3,500 req/s", "10x Black Friday"], [3500, 3000, 2860], false),
      dataRow(["Average Cosmos DB document size", "2 KB", "N/A"], [3500, 3000, 2860], true),
      dataRow(["Service Bus messages per day", "8,000,000", "N/A"], [3500, 3000, 2860], false),
    ]),

    h(HeadingLevel.HEADING_2, "10.2 Performance Testing Strategy"),
    p("Performance testing is integrated into the CI/CD pipeline at the staging stage using Azure Load Testing:"),
    bullet("Baseline load test: 1,000 concurrent virtual users, 15-minute duration, run on every release candidate."),
    bullet("Stress test: ramp to 5,000 VU over 30 minutes, hold 10 minutes, ramp down. Run monthly."),
    bullet("Endurance test: 800 VU sustained for 4 hours. Run quarterly."),
    bullet("Spike test: instant ramp to 10,000 VU, held 5 minutes. Run on demand for major releases."),
    p("Gate criteria: P99 latency within SLA (see Section 3.6), error rate < 0.5%, no memory leak indicators over endurance test duration."),
    pageBreak(),
  ];
}

// ── Section 11: Quality ───────────────────────────────────────────────────
function section11() {
  return [
    h(HeadingLevel.HEADING_1, "11. Quality"),

    h(HeadingLevel.HEADING_2, "11.1 Security Quality"),
    bullet("Microsoft Defender for Cloud Secure Score target: >= 90%."),
    bullet("Vulnerability scanning: all container images scanned on push and daily. SLA for patching Critical CVEs: 24 hours; High CVEs: 7 days."),
    bullet("Penetration testing: annual external pen test + quarterly internal red team exercises."),
    bullet("Security chaos engineering: monthly fault injection via Chaos Studio targeting authentication failures and secret expiry scenarios."),

    h(HeadingLevel.HEADING_2, "11.2 Reliability Quality"),
    bullet("Monthly uptime SLA: 99.95% (customer-facing APIs)."),
    bullet("Chaos engineering: quarterly ACA replica failure injection, Cosmos DB failover tests, Service Bus outage simulation."),
    bullet("Incident response: P1 incident SLA — acknowledge within 5 minutes, mitigate within 30 minutes."),
    bullet("Change management: all production changes go through the Change Advisory Board (CAB) with mandatory rollback plan."),

    h(HeadingLevel.HEADING_2, "11.3 Maintainability"),
    bullet("Code coverage gate: minimum 80% line coverage on all microservice repositories."),
    bullet("API contract testing: all service-to-service contracts tested with Pact consumer-driven contract tests."),
    bullet("Infrastructure as Code: 100% of Azure resources managed via Terraform (IaC). No manual resource creation in production."),
    bullet("Architecture Decision Records (ADRs): all significant architectural decisions documented in the /docs/adr directory of each repository."),

    h(HeadingLevel.HEADING_2, "11.4 Observability Quality"),
    bullet("All microservices emit structured logs in Elastic Common Schema format to Log Analytics."),
    bullet("Distributed tracing correlation IDs are propagated across all synchronous and asynchronous calls."),
    bullet("SLO dashboards in Azure Monitor Workbooks track error budget burn rates for all customer-facing services."),
    bullet("Alerting: <= 2-minute detection time for P1 issues. Alerts route to PagerDuty on-call rotation."),
    spacer(),
    sectionRule(),
    new Paragraph({
      alignment: AlignmentType.CENTER,
      spacing: { before: 240, after: 120 },
      children: [new TextRun({
        text: "END OF DOCUMENT — Contoso Financial Services CSAD v1.0", bold: true,
        size: 18, font: "Arial", color: DGRAY,
      })],
    }),
    new Paragraph({
      alignment: AlignmentType.CENTER,
      spacing: { after: 120 },
      children: [new TextRun({
        text: "CLASSIFICATION: INTERNAL — RESTRICTED", bold: true, size: 18,
        font: "Arial", color: "C00000",
      })],
    }),
  ];
}

// ===========================================================================
// BUILD DOCUMENT
// ===========================================================================
const doc = new Document({
  numbering,
  styles,
  sections: [
    {
      properties: {
        page: {
          size: { width: PAGE_W, height: PAGE_H },
          margin: { top: MARGIN, right: MARGIN, bottom: MARGIN, left: MARGIN },
        },
      },
      headers: {
        default: new Header({
          children: [
            new Paragraph({
              children: [
                new TextRun({ text: "Contoso Financial Services — Azure Microservices Architecture", size: 16, font: "Arial", color: DGRAY }),
                new TextRun({ text: "\t", size: 16 }),
                new TextRun({ text: "INTERNAL — RESTRICTED", size: 16, font: "Arial", color: "C00000", bold: true }),
              ],
              tabStops: [{ type: "right", position: 9360 }],
              border: { bottom: { style: BorderStyle.SINGLE, size: 4, color: LBLUE, space: 1 } },
            }),
          ],
        }),
      },
      footers: {
        default: new Footer({
          children: [
            new Paragraph({
              children: [
                new TextRun({ text: "CSAD v1.0  |  May 2026", size: 16, font: "Arial", color: DGRAY }),
                new TextRun({ text: "\tPage ", size: 16, font: "Arial", color: DGRAY }),
                new TextRun({ children: [PageNumber.CURRENT], size: 16, font: "Arial", color: DGRAY }),
              ],
              tabStops: [{ type: "right", position: 9360 }],
              border: { top: { style: BorderStyle.SINGLE, size: 4, color: LBLUE, space: 1 } },
            }),
          ],
        }),
      },
      children: [
        ...coverPage(),
        ...tocSection(),
        ...section1(),
        ...section2(),
        ...section3(),
        ...section4(),
        ...section5(),
        ...section6(),
        ...section7(),
        ...section8(),
        ...section9(),
        ...section10(),
        ...section11(),
      ],
    },
  ],
});

Packer.toBuffer(doc).then((buffer) => {
  const outPath = path.join(__dirname, "Cloud-service-Architecture.docx");
  fs.writeFileSync(outPath, buffer);
  console.log("Written:", outPath);
}).catch((err) => {
  console.error("Error:", err);
  process.exit(1);
});
