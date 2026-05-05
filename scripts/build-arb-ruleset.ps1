# Builds samples/contracts/arb-ruleset.json from architecture-review-board policies.json
# Each rule uses SemanticFunctions.MatchesAnyMeaning + gateThreshold, exercising the
# new LLM-vectorize / cosine-similarity path against architecture design documents.

param(
  [string]$Source = "C:\Projects\architecture-review-board\back-end\file_processing\data\policies.json",
  [string]$Out    = "C:\Projects\lambda-rag\samples\contracts\arb-ruleset.json"
)

# Header → (topic, severity-tag, meaning-phrases, anchor regex, remediation hint)
# topic must exist in architecture-review.v1 topic map.
$mappings = @{
  "Shared Responsibility Model"                              = @{ topic="compliance_posture";    phrases=@("shared responsibility","CSP responsibilities","cloud consumer accountability","security obligations split","provider versus customer security"); anchor="shared responsibility[^\n]*"; reason="Define which security controls are owned by the cloud provider versus the customer." }
  "Governance"                                               = @{ topic="compliance_posture";    phrases=@("cloud governance","steering committee","architecture review board","approval workflow","cloud strategy","onboarding governance"); anchor="governance[^\n]*"; reason="Document the governance body, approval flow, and architecture-review process for cloud workloads." }
  "Roles and Accountabilities"                               = @{ topic="security_iam";          phrases=@("RACI matrix","roles and responsibilities","accountable owner","DRI","operations accountability","security accountability"); anchor="role[s]?[^\n]*"; reason="Specify named owners (RACI) for each layer of the architecture: app team, platform team, security team, CSP." }
  "Asset Management in the Cloud"                            = @{ topic="data_classification";   phrases=@("cloud asset inventory","tagging strategy","resource tagging","asset register","data classification labels","CMDB"); anchor="asset[s]?[^\n]*"; reason="Define mandatory resource tags (owner, environment, data-classification, cost-center) and an asset inventory mechanism." }
  "Access Control"                                           = @{ topic="zero_trust";            phrases=@("least privilege","just-in-time access","role-based access control","conditional access","privileged identity management","Entra ID groups","zero trust"); anchor="access control[^\n]*"; reason="Apply least-privilege RBAC, JIT/PIM elevation, and conditional access for all human and service principals." }
  "Cryptography"                                             = @{ topic="encryption_at_rest";    phrases=@("encryption at rest","encryption in transit","TLS 1.2","customer-managed keys","key vault","Key Vault","HSM","key rotation"); anchor="encryption[^\n]*"; reason="Encrypt data at rest with customer-managed keys (Key Vault / HSM) and enforce TLS 1.2+ in transit." }
  "Operations Security"                                      = @{ topic="vulnerability_mgmt";    phrases=@("patch management","vulnerability scanning","Defender for Cloud","SIEM integration","incident response","Sentinel","anti-malware"); anchor="operation[s]? security[^\n]*"; reason="Wire workloads to centralised SIEM/Defender, run regular vuln scans, and define an incident-response runbook." }
  "Security Requirements for Cloud Service Providers"        = @{ topic="compliance_posture";    phrases=@("CSP attestation","SOC 2","ISO 27001","FedRAMP","third-party risk","vendor security review","data processing agreement","PCI"); anchor="cloud service provider[^\n]*"; reason="Cite the CSP's attestations (SOC 2 / ISO 27001 / FedRAMP) and the contractual security clauses relied upon." }
  "Compliance Monitoring"                                    = @{ topic="audit_logging";         phrases=@("continuous compliance","Azure Policy","compliance dashboard","drift detection","audit logs","activity log","control monitoring"); anchor="compliance[^\n]*"; reason="Enable Azure Policy / continuous compliance monitoring and route audit logs to a tamper-evident store." }
  "Security Exceptions"                                      = @{ topic="compliance_posture";    phrases=@("security exception process","risk acceptance","compensating control","exception register","time-bound waiver"); anchor="exception[s]?[^\n]*"; reason="Document any deviations as time-bound exceptions with compensating controls, owner, and review date." }
  "Implementation"                                           = @{ topic="change_management";     phrases=@("change management","release approval","deployment pipeline","rollback plan","blue-green deployment","change advisory board"); anchor="implementation[^\n]*"; reason="Define the deployment / change-management process with approvals, rollback, and CAB review." }
  "Architect for Stability and Resiliency by Design"         = @{ topic="reliability";           phrases=@("high availability","fault tolerance","resiliency","availability zones","multi-region","RTO","RPO","disaster recovery","graceful degradation"); anchor="(resilien|stabilit|availab)[^\n]*"; reason="Specify HA topology (AZs / multi-region), measured RTO/RPO targets, and DR runbook." }
  "Compliance by Design"                                     = @{ topic="compliance_posture";    phrases=@("compliance by design","privacy by design","regulatory mapping","GDPR","HIPAA","FINTRAC","data residency"); anchor="compliance[^\n]*"; reason="Map the design to applicable regulations (GDPR / HIPAA / data-residency) and embed controls up-front." }
  "Security by Design"                                       = @{ topic="threat_modeling";       phrases=@("security by design","threat model","STRIDE","attack surface","secure SDLC","SAST DAST","penetration testing"); anchor="security[^\n]*"; reason="Include a threat model (STRIDE) and integrate SAST/DAST + pen testing into the SDLC." }
  "Cloud First design"                                       = @{ topic="infra_as_code";         phrases=@("cloud-first","PaaS-first","managed services","serverless","platform-as-a-service","cloud-native"); anchor="cloud[ -]?first[^\n]*"; reason="Default to managed PaaS / serverless services rather than IaaS or self-hosted equivalents." }
  "API First design"                                         = @{ topic="infra_as_code";         phrases=@("API-first","OpenAPI","REST contract","API gateway","contract-driven","Swagger"); anchor="api[ -]?first[^\n]*"; reason="Publish OpenAPI / Swagger contracts and front services with an API-gateway / management layer." }
  "DevOps Automation"                                        = @{ topic="ci_cd";                 phrases=@("DevOps","CI/CD","continuous integration","continuous deployment","Azure DevOps","GitHub Actions","pipeline automation","infrastructure as code"); anchor="(devops|automation|pipeline)[^\n]*"; reason="Automate build / test / deploy through CI/CD pipelines with infrastructure-as-code (Terraform / Bicep)." }
  "Scalabilty and Reusability"                               = @{ topic="scalability";           phrases=@("horizontal scaling","auto-scaling","reusable component","stateless service","scale-out","load balancer","elasticity"); anchor="(scalab|reusab|elastic)[^\n]*"; reason="Design stateless, horizontally-scalable services with auto-scale rules and shared reusable components." }
  "Modularize"                                               = @{ topic="infra_as_code";         phrases=@("modular design","microservices","loosely coupled","bounded context","separation of concerns","service decomposition"); anchor="modular[^\n]*"; reason="Decompose into loosely-coupled modules / microservices with well-defined boundaries." }
  "Maximize Value"                                           = @{ topic="cost_optimization";     phrases=@("cost optimization","FinOps","reserved instances","right-sizing","cost governance","spending alerts","value realization","TCO"); anchor="(cost|value|finops)[^\n]*"; reason="Apply FinOps controls: cost alerts, right-sizing, reserved capacity, tagging-driven chargeback." }
}

$src = Get-Content $Source -Raw | ConvertFrom-Json
$rules = New-Object System.Collections.Generic.List[object]
$idx = 0
foreach ($p in $src) {
  $idx++
  $h = $p.header
  if (-not $mappings.ContainsKey($h)) { Write-Warning "No mapping for header: $h"; continue }
  $m = $mappings[$h]
  $severity = if ($p.mandatory) { "Critical" } else { "Violation" }
  $phrasesPipe = ($m.phrases -join "|")
  $headerSlug = ($h.ToUpper() -replace '[^A-Z0-9]+','-').Trim('-')
  if ($headerSlug.Length -gt 30) { $headerSlug = $headerSlug.Substring(0,30).Trim('-') }
  $rid = "ARB-{0:D2}-{1}" -f $idx, $headerSlug
  $rule = [ordered]@{
    id              = $rid
    version         = "1.0.0-semantic"
    naturalLanguage = "Architecture must address: $h"
    predicate       = "true"
    lambda          = "SemanticFunctions.MatchesAnyMeaning(input1.id, ""$phrasesPipe"", 0.62)"
    appliesToSchema = @{ type = "object" }
    selector        = @{ kind = "path"; path = "`$.sections[*]" }
    severity        = $severity
    gateThreshold   = 0.45
    sourceSpan      = @{ documentId = "arb-cloud-security-directive"; charStart = 0; charLength = 1; pageNumber = 1; headingPath = $null }
    evidenceQuote   = $h
    anchor          = $m.anchor
    remediation     = "Add a section to the architecture document covering ""$h"". $($m.reason) (Reference: ARB Cloud Security Directive — $($p.category))."
    metadata        = @{ reviewer = "arb"; mandatory = "$($p.mandatory)"; category = "$($p.category)"; sourcePolicy = $h }
  }
  $rules.Add($rule) | Out-Null
}

$ruleset = [ordered]@{
  id          = "rs_arb_cloud_security_semantic"
  version     = "1.0.0-semantic"
  domain      = "architecture-review"
  publishedAt = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ" -AsUTC)
  metadata    = @{
    source     = "Derived from architecture-review-board policies.json (Cloud Security Directive, 20 policies)"
    note       = "Each rule uses SemanticFunctions.MatchesAnyMeaning at threshold 0.62 with gateThreshold 0.45 to fully exercise the embedding-backed semantic predicates introduced in #67/#68."
    topicMap   = "architecture-review.v1"
  }
  rules       = $rules
}

$json = $ruleset | ConvertTo-Json -Depth 12
[System.IO.File]::WriteAllText($Out, $json)
Write-Host ("Wrote {0} rules to {1}" -f $rules.Count, $Out)
