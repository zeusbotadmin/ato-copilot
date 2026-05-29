-- ============================================================================
--  ATO Copilot — Flankspeed CSP-portfolio demo seed.
--  Target:  AtoCopilot (SQL Server in docker compose, container ato-copilot-sql)
--  Idempotent: re-running is a no-op (deterministic IDs + IF NOT EXISTS guards).
--
--  What this seeds (so the /portfolio dashboard cards light up):
--    * 3 Assessments      (one per mission-owner tenant; required FK parents)
--    * 4 AuthorizationDecisions  (2 Authorized | 1 InProcess | 1 Denied)
--    * 6 Findings                (1 Critical | 3 High | 2 Medium | 0 Low)
--    * 4 PoamItems               (all Ongoing → count as open)
--    * 1 Deviation               (Pending → counts as open)
--
--  Targets the 5 demo systems already reassigned from the system tenant
--  to the 3 mission-owner orgs (PEO-790 / PMA 290 / PMS 408) — see
--  the reassign-flankspeed-systems.sql sibling script.
--
--  Run via:
--    scripts/seed-flankspeed.sh
-- ============================================================================
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
USE AtoCopilot;

DECLARE @PEO790 UNIQUEIDENTIFIER = '4A5E1C76-4743-48E8-9C19-DDAF27CE376F';
DECLARE @PMA290 UNIQUEIDENTIFIER = '62373322-306C-462C-A29D-41491287AD49';
DECLARE @PMS408 UNIQUEIDENTIFIER = '4A87B9F7-027C-445F-B3EE-6F3DC3D87F60';

DECLARE @Sys_Coastal   NVARCHAR(72) = '44a2f788-4808-4b88-bcf4-fea1d63779ab';
DECLARE @Sys_EagleEye  NVARCHAR(72) = 'c267a009-dd48-4555-a194-7edecbbb45b4';
DECLARE @Sys_EagleNest NVARCHAR(72) = '9b6bd346-d188-4d51-a1ae-7371f8b93607';
DECLARE @Sys_Phoenix   NVARCHAR(72) = '32ef294f-e725-4430-94cc-785a7b47398c';
DECLARE @Sys_Polar     NVARCHAR(72) = '939b29a5-3010-4dd1-8c2c-420e4d74eca3';

DECLARE @Now  DATETIME2 = SYSUTCDATETIME();
DECLARE @90d  DATETIME2 = DATEADD(DAY, 90, @Now);
DECLARE @180d DATETIME2 = DATEADD(DAY, 180, @Now);
DECLARE @1y   DATETIME2 = DATEADD(YEAR, 1, @Now);
DECLARE @3y   DATETIME2 = DATEADD(YEAR, 3, @Now);

-- Deterministic IDs (PKs) so re-runs are idempotent ------------------
DECLARE @Assess_PEO790  NVARCHAR(200) = 'flankspeed-seed-assess-peo790';
DECLARE @Assess_PMA290  NVARCHAR(200) = 'flankspeed-seed-assess-pma290';
DECLARE @Assess_PMS408  NVARCHAR(200) = 'flankspeed-seed-assess-pms408';

DECLARE @AD_Coastal    NVARCHAR(72) = 'fs-seed-ad-coastal';
DECLARE @AD_EagleNest  NVARCHAR(72) = 'fs-seed-ad-eagle-nest';
DECLARE @AD_Phoenix    NVARCHAR(72) = 'fs-seed-ad-phoenix';
DECLARE @AD_Polar      NVARCHAR(72) = 'fs-seed-ad-polar';

DECLARE @F_EE_AC2  NVARCHAR(200) = 'flankspeed-seed-f-ee-ac2';
DECLARE @F_EE_SI4  NVARCHAR(200) = 'flankspeed-seed-f-ee-si4';
DECLARE @F_PHX_SC7 NVARCHAR(200) = 'flankspeed-seed-f-phx-sc7';
DECLARE @F_PB_IA2  NVARCHAR(200) = 'flankspeed-seed-f-pb-ia2';
DECLARE @F_PB_AU12 NVARCHAR(200) = 'flankspeed-seed-f-pb-au12';
DECLARE @F_PB_CM7  NVARCHAR(200) = 'flankspeed-seed-f-pb-cm7';

DECLARE @P_EE_AC2  NVARCHAR(72) = 'fs-seed-p-ee-ac2';
DECLARE @P_EN_SI4  NVARCHAR(72) = 'fs-seed-p-en-si4';
DECLARE @P_PB_IA2  NVARCHAR(72) = 'fs-seed-p-pb-ia2';
DECLARE @P_PB_CM7  NVARCHAR(72) = 'fs-seed-p-pb-cm7';

DECLARE @D_PB_CM7  NVARCHAR(72) = 'fs-seed-d-pb-cm7';

BEGIN TRY
  BEGIN TRAN;

  -- Assessments (parents for Findings.AssessmentId FK) ---------------
  IF NOT EXISTS (SELECT 1 FROM dbo.Assessments WHERE Id = @Assess_PEO790)
    INSERT dbo.Assessments
      (Id, TenantId, SubscriptionId, Framework, Baseline, ScanType, Status,
       InitiatedBy, AssessedAt, CompletedAt, ProgressMessage, ComplianceScore,
       TotalControls, PassedControls, FailedControls, NotAssessedControls,
       ControlFamilyResults, ExecutiveSummary, SubscriptionIds, ScanPillarResults,
       RegisteredSystemId)
    VALUES
      (@Assess_PEO790, @PEO790, '00000000-0000-0000-0000-000000000aaa',
       'NIST_SP_800_53', 'Moderate', 'Comprehensive', 2,
       'system@spin-agent.local', @Now, @Now, 'Seeded for portfolio demo', 92.5,
       300, 278, 2, 20, '[]', 'Seeded for Flankspeed portfolio demo.', '[]', '{}',
       @Sys_EagleEye);

  IF NOT EXISTS (SELECT 1 FROM dbo.Assessments WHERE Id = @Assess_PMA290)
    INSERT dbo.Assessments
      (Id, TenantId, SubscriptionId, Framework, Baseline, ScanType, Status,
       InitiatedBy, AssessedAt, CompletedAt, ProgressMessage, ComplianceScore,
       TotalControls, PassedControls, FailedControls, NotAssessedControls,
       ControlFamilyResults, ExecutiveSummary, SubscriptionIds, ScanPillarResults,
       RegisteredSystemId)
    VALUES
      (@Assess_PMA290, @PMA290, '00000000-0000-0000-0000-000000000bbb',
       'NIST_SP_800_53', 'Moderate', 'Comprehensive', 2,
       'system@spin-agent.local', @Now, @Now, 'Seeded for portfolio demo', 71.0,
       300, 250, 1, 49, '[]', 'Seeded for Flankspeed portfolio demo.', '[]', '{}',
       @Sys_Phoenix);

  IF NOT EXISTS (SELECT 1 FROM dbo.Assessments WHERE Id = @Assess_PMS408)
    INSERT dbo.Assessments
      (Id, TenantId, SubscriptionId, Framework, Baseline, ScanType, Status,
       InitiatedBy, AssessedAt, CompletedAt, ProgressMessage, ComplianceScore,
       TotalControls, PassedControls, FailedControls, NotAssessedControls,
       ControlFamilyResults, ExecutiveSummary, SubscriptionIds, ScanPillarResults,
       RegisteredSystemId)
    VALUES
      (@Assess_PMS408, @PMS408, '00000000-0000-0000-0000-000000000ccc',
       'NIST_SP_800_53', 'Moderate', 'Comprehensive', 2,
       'system@spin-agent.local', @Now, @Now, 'Seeded for portfolio demo', 42.0,
       300, 220, 3, 77, '[]', 'Seeded for Flankspeed portfolio demo.', '[]', '{}',
       @Sys_Polar);

  -- AuthorizationDecisions (4 rows) ----------------------------------
  IF NOT EXISTS (SELECT 1 FROM dbo.AuthorizationDecisions WHERE Id = @AD_Coastal)
    INSERT dbo.AuthorizationDecisions
      (Id, TenantId, RegisteredSystemId, DecisionType, DecisionDate, ExpirationDate,
       ResidualRiskLevel, ResidualRiskJustification, ComplianceScoreAtDecision,
       FindingsAtDecision, IssuedBy, IssuedByName, IsActive)
    VALUES
      (@AD_Coastal, @PEO790, @Sys_Coastal, 'Ato', @Now, @3y, 'Low',
       'Operational baseline meets all 800-53 moderate controls.', 92.5,
       '[]', 'ao.peo790@navy.mil', 'CAPT R. Halsey, AO PEO-790', 1);

  IF NOT EXISTS (SELECT 1 FROM dbo.AuthorizationDecisions WHERE Id = @AD_EagleNest)
    INSERT dbo.AuthorizationDecisions
      (Id, TenantId, RegisteredSystemId, DecisionType, DecisionDate, ExpirationDate,
       ResidualRiskLevel, ResidualRiskJustification, ComplianceScoreAtDecision,
       FindingsAtDecision, IssuedBy, IssuedByName, IsActive)
    VALUES
      (@AD_EagleNest, @PMA290, @Sys_EagleNest, 'AtoWithConditions', @Now, @1y, 'Medium',
       'Two CAT II findings under remediation; POA&M tracked.', 78.0,
       '[{"id":"f1","sev":"High"}]', 'ao.pma290@navy.mil', 'COL S. Nimitz, AO PMA 290', 1);

  IF NOT EXISTS (SELECT 1 FROM dbo.AuthorizationDecisions WHERE Id = @AD_Phoenix)
    INSERT dbo.AuthorizationDecisions
      (Id, TenantId, RegisteredSystemId, DecisionType, DecisionDate, ExpirationDate,
       ResidualRiskLevel, ResidualRiskJustification, ComplianceScoreAtDecision,
       FindingsAtDecision, IssuedBy, IssuedByName, IsActive)
    VALUES
      (@AD_Phoenix, @PMA290, @Sys_Phoenix, 'Iatt', @Now, @90d, 'Medium',
       'Testing phase; limited connectivity, no production data.', 71.0,
       '[]', 'ao.pma290@navy.mil', 'COL S. Nimitz, AO PMA 290', 1);

  IF NOT EXISTS (SELECT 1 FROM dbo.AuthorizationDecisions WHERE Id = @AD_Polar)
    INSERT dbo.AuthorizationDecisions
      (Id, TenantId, RegisteredSystemId, DecisionType, DecisionDate, ExpirationDate,
       ResidualRiskLevel, ResidualRiskJustification, ComplianceScoreAtDecision,
       FindingsAtDecision, IssuedBy, IssuedByName, IsActive)
    VALUES
      (@AD_Polar, @PMS408, @Sys_Polar, 'Dato', @Now, NULL, 'Critical',
       'Critical AC and SC findings; system must not operate.', 42.0,
       '[{"id":"f3","sev":"Critical"},{"id":"f4","sev":"High"}]',
       'ao.pms408@navy.mil', 'RDML K. Mitscher, AO PMS 408', 1);

  -- Findings (6 rows) ------------------------------------------------
  -- Severity int: Critical=0 | High=1 | Medium=2 | Low=3 | Informational=4
  -- Status   int: Open=0 | InProgress=1
  -- ScanSource int: Resource=0 | Policy=1 | Defender=2
  IF NOT EXISTS (SELECT 1 FROM dbo.Findings WHERE Id = @F_EE_AC2)
    INSERT dbo.Findings
      (Id, TenantId, ControlId, ControlFamily, Title, Description, Severity, Status,
       ResourceId, ResourceType, RemediationGuidance, DiscoveredAt,
       AutoRemediable, Source, ScanSource, RemediationType, RiskLevel,
       AssessmentId, ControlTitle, ControlDescription, StigFinding,
       RemediationTrackingStatus, CatSeverity)
    VALUES
      (@F_EE_AC2, @PEO790, 'AC-2', 'AC', 'Inactive privileged accounts not disabled',
       'Two service accounts have not been used in 90+ days and remain enabled.',
       1, 0, '/subs/x/rg/eagle-eye/sa-svc-01', 'Microsoft.Authorization/roleAssignments',
       'Disable accounts inactive for >60 days per AC-2(3).', @Now,
       0, 'Defender', 0, 0, 1, @Assess_PEO790,
       'Account Management', 'Manage information system accounts...', 0, 0, 'CatII');

  IF NOT EXISTS (SELECT 1 FROM dbo.Findings WHERE Id = @F_EE_SI4)
    INSERT dbo.Findings
      (Id, TenantId, ControlId, ControlFamily, Title, Description, Severity, Status,
       ResourceId, ResourceType, RemediationGuidance, DiscoveredAt,
       AutoRemediable, Source, ScanSource, RemediationType, RiskLevel,
       AssessmentId, ControlTitle, ControlDescription, StigFinding,
       RemediationTrackingStatus, CatSeverity)
    VALUES
      (@F_EE_SI4, @PEO790, 'SI-4', 'SI', 'Storage account public network access',
       'Storage account allows public network access; default action is Allow.',
       2, 0, '/subs/x/rg/eagle-eye/st-data-01', 'Microsoft.Storage/storageAccounts',
       'Restrict to selected networks per SI-4 logging boundaries.', @Now,
       1, 'Policy', 1, 0, 0, @Assess_PEO790,
       'Information System Monitoring', 'Monitor the information system...', 0, 0, 'CatIII');

  IF NOT EXISTS (SELECT 1 FROM dbo.Findings WHERE Id = @F_PHX_SC7)
    INSERT dbo.Findings
      (Id, TenantId, ControlId, ControlFamily, Title, Description, Severity, Status,
       ResourceId, ResourceType, RemediationGuidance, DiscoveredAt,
       AutoRemediable, Source, ScanSource, RemediationType, RiskLevel,
       AssessmentId, ControlTitle, ControlDescription, StigFinding,
       RemediationTrackingStatus, CatSeverity)
    VALUES
      (@F_PHX_SC7, @PMA290, 'SC-7', 'SC', 'NSG allows inbound 3389 from 0.0.0.0/0',
       'Network security group attached to a test VM exposes RDP to the internet.',
       1, 0, '/subs/x/rg/phoenix/nsg-test-01', 'Microsoft.Network/networkSecurityGroups',
       'Restrict 3389 source to engineering bastion CIDR per SC-7.', @Now,
       1, 'Defender', 0, 0, 1, @Assess_PMA290,
       'Boundary Protection', 'Monitor and control communications...', 0, 0, 'CatII');

  IF NOT EXISTS (SELECT 1 FROM dbo.Findings WHERE Id = @F_PB_IA2)
    INSERT dbo.Findings
      (Id, TenantId, ControlId, ControlFamily, Title, Description, Severity, Status,
       ResourceId, ResourceType, RemediationGuidance, DiscoveredAt,
       AutoRemediable, Source, ScanSource, RemediationType, RiskLevel,
       AssessmentId, ControlTitle, ControlDescription, StigFinding,
       RemediationTrackingStatus, CatSeverity)
    VALUES
      (@F_PB_IA2, @PMS408, 'IA-2', 'IA', 'MFA not enforced for global administrators',
       'Two global administrators have MFA registered but not enforced via Conditional Access.',
       0, 0, '/tenants/pms408/conditional-access/0', 'Microsoft.Graph/conditionalAccessPolicies',
       'Enforce MFA via CA policy targeting Directory Roles per IA-2(1).', @Now,
       0, 'Defender', 2, 0, 1, @Assess_PMS408,
       'Identification and Authentication', 'Identify users and authenticate...', 0, 0, 'CatI');

  IF NOT EXISTS (SELECT 1 FROM dbo.Findings WHERE Id = @F_PB_AU12)
    INSERT dbo.Findings
      (Id, TenantId, ControlId, ControlFamily, Title, Description, Severity, Status,
       ResourceId, ResourceType, RemediationGuidance, DiscoveredAt,
       AutoRemediable, Source, ScanSource, RemediationType, RiskLevel,
       AssessmentId, ControlTitle, ControlDescription, StigFinding,
       RemediationTrackingStatus, CatSeverity)
    VALUES
      (@F_PB_AU12, @PMS408, 'AU-12', 'AU', 'Diagnostic settings missing on Key Vault',
       'Critical Key Vault has no diagnostic settings; audit events not captured.',
       1, 0, '/subs/x/rg/polar/kv-prod-01', 'Microsoft.KeyVault/vaults',
       'Configure diagnostic settings to Log Analytics per AU-12.', @Now,
       1, 'Policy', 1, 0, 0, @Assess_PMS408,
       'Audit Record Generation', 'Generate audit records for events...', 0, 0, 'CatII');

  IF NOT EXISTS (SELECT 1 FROM dbo.Findings WHERE Id = @F_PB_CM7)
    INSERT dbo.Findings
      (Id, TenantId, ControlId, ControlFamily, Title, Description, Severity, Status,
       ResourceId, ResourceType, RemediationGuidance, DiscoveredAt,
       AutoRemediable, Source, ScanSource, RemediationType, RiskLevel,
       AssessmentId, ControlTitle, ControlDescription, StigFinding,
       RemediationTrackingStatus, CatSeverity)
    VALUES
      (@F_PB_CM7, @PMS408, 'CM-7', 'CM', 'Function App allows all CORS origins',
       'Function App CORS configured with wildcard (*), exceeding least functionality.',
       2, 0, '/subs/x/rg/polar/func-api-01', 'Microsoft.Web/sites',
       'Restrict CORS to known FQDNs per CM-7.', @Now,
       1, 'Policy', 1, 0, 0, @Assess_PMS408,
       'Least Functionality', 'Configure information system...', 0, 0, 'CatIII');

  -- PoamItems (4 rows, all Status='Ongoing' → count as open) ---------
  IF NOT EXISTS (SELECT 1 FROM dbo.PoamItems WHERE Id = @P_EE_AC2)
    INSERT dbo.PoamItems
      (Id, TenantId, RegisteredSystemId, Weakness, WeaknessSource,
       SecurityControlNumber, CatSeverity, PointOfContact, PocEmail,
       ResourcesRequired, CostEstimate, ScheduledCompletionDate, Status,
       Comments, CreatedAt, RowVersion)
    VALUES
      (@P_EE_AC2, @PEO790, @Sys_EagleEye,
       'Inactive privileged accounts not disabled',
       'Defender for Cloud', 'AC-2', 'CatII',
       'ISSM PEO-790', 'issm.peo790@navy.mil',
       '40 engineer hours', 12000.00, @90d, 'Ongoing',
       'Pending automation rollout to org-wide account hygiene runbook.', @Now, NEWID());

  IF NOT EXISTS (SELECT 1 FROM dbo.PoamItems WHERE Id = @P_EN_SI4)
    INSERT dbo.PoamItems
      (Id, TenantId, RegisteredSystemId, Weakness, WeaknessSource,
       SecurityControlNumber, CatSeverity, PointOfContact, PocEmail,
       ResourcesRequired, CostEstimate, ScheduledCompletionDate, Status,
       Comments, CreatedAt, RowVersion)
    VALUES
      (@P_EN_SI4, @PMA290, @Sys_EagleNest,
       'Storage public network access enabled',
       'Azure Policy', 'SI-4', 'CatII',
       'ISSM PMA 290', 'issm.pma290@navy.mil',
       '20 engineer hours', 6000.00, @180d, 'Ongoing',
       'Awaiting network segmentation design approval.', @Now, NEWID());

  IF NOT EXISTS (SELECT 1 FROM dbo.PoamItems WHERE Id = @P_PB_IA2)
    INSERT dbo.PoamItems
      (Id, TenantId, RegisteredSystemId, Weakness, WeaknessSource,
       SecurityControlNumber, CatSeverity, PointOfContact, PocEmail,
       ResourcesRequired, CostEstimate, ScheduledCompletionDate, Status,
       Comments, CreatedAt, RowVersion)
    VALUES
      (@P_PB_IA2, @PMS408, @Sys_Polar,
       'MFA not enforced for global administrators',
       'Defender for Cloud', 'IA-2', 'CatI',
       'ISSO PMS 408', 'isso.pms408@navy.mil',
       '8 engineer hours + identity governance license', 4500.00, @90d, 'Ongoing',
       'CA policy drafted, pending change advisory board approval.', @Now, NEWID());

  IF NOT EXISTS (SELECT 1 FROM dbo.PoamItems WHERE Id = @P_PB_CM7)
    INSERT dbo.PoamItems
      (Id, TenantId, RegisteredSystemId, Weakness, WeaknessSource,
       SecurityControlNumber, CatSeverity, PointOfContact, PocEmail,
       ResourcesRequired, CostEstimate, ScheduledCompletionDate, Status,
       Comments, CreatedAt, RowVersion)
    VALUES
      (@P_PB_CM7, @PMS408, @Sys_Polar,
       'Function App allows all CORS origins',
       'Azure Policy', 'CM-7', 'CatIII',
       'ISSO PMS 408', 'isso.pms408@navy.mil',
       '4 engineer hours', 1500.00, @180d, 'Ongoing',
       'Coordinating with downstream consumers on allow-list.', @Now, NEWID());

  -- Deviations (1 row, Status='Pending' → counts as open) ------------
  IF NOT EXISTS (SELECT 1 FROM dbo.Deviations WHERE Id = @D_PB_CM7)
    INSERT dbo.Deviations
      (Id, TenantId, RegisteredSystemId, DeviationType, Status, ControlId, CatSeverity,
       Justification, CompensatingControls, EvidenceReferences, ExpirationDate,
       ReviewCycle, RequestedBy, RequestedAt, CreatedAt)
    VALUES
      (@D_PB_CM7, @PMS408, @Sys_Polar,
       'RiskAcceptance', 'Pending', 'CM-7', 'CatIII',
       'Wildcard CORS retained on read-only public preview endpoint pending API gateway rollout.',
       'WAF rules restrict request methods; endpoint serves cached static content only.',
       '[]',
       @180d, '180d', 'isso.pms408@navy.mil', @Now, @Now);

  COMMIT TRAN;
  PRINT '─────────────────────────────────────────────────────────────';
  PRINT 'Flankspeed seed COMMITTED. Totals now in DB:';
  SELECT
    (SELECT COUNT(*) FROM dbo.RegisteredSystems
       WHERE TenantId IN (@PEO790, @PMA290, @PMS408))               AS Systems,
    (SELECT COUNT(*) FROM dbo.AuthorizationDecisions
       WHERE IsActive = 1 AND TenantId IN (@PEO790, @PMA290, @PMS408)) AS ActiveDecisions,
    (SELECT COUNT(*) FROM dbo.Findings
       WHERE Status IN (0,1) AND TenantId IN (@PEO790, @PMA290, @PMS408)) AS OpenFindings,
    (SELECT COUNT(*) FROM dbo.PoamItems
       WHERE Status = 'Ongoing' AND TenantId IN (@PEO790, @PMA290, @PMS408)) AS OpenPoams,
    (SELECT COUNT(*) FROM dbo.Deviations
       WHERE Status IN ('Pending','Approved') AND TenantId IN (@PEO790, @PMA290, @PMS408)) AS OpenDeviations;
END TRY
BEGIN CATCH
  IF @@TRANCOUNT > 0 ROLLBACK TRAN;
  PRINT 'ROLLED BACK: ' + ERROR_MESSAGE();
  PRINT '  Line: ' + CAST(ERROR_LINE() AS NVARCHAR(10));
  THROW;
END CATCH;
