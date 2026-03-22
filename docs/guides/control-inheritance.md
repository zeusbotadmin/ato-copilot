# Control Inheritance & CRM Guide

This guide explains how to use the **Control Inheritance** page in the ATO Copilot Dashboard to manage inheritance designations, generate Customer Responsibility Matrices, apply CSP profiles, and import CRM spreadsheets.

## Overview

Every control in your selected NIST 800-53 baseline must be designated as one of:

| Type | Meaning |
|------|---------|
| **Inherited** | Fully provided by the CSP (e.g., Azure Government) |
| **Shared** | Split responsibility between CSP and customer |
| **Customer** | Fully the customer's responsibility |
| **Undesignated** | Not yet classified (default) |

The Control Inheritance page surfaces summary metrics, inline editing, bulk operations, audit trails, CRM export/import, pre-built CSP profiles, and org-level inheritance defaults.

## Navigation

1. Open a registered system from the Dashboard.
2. In the left sidebar under **Compliance Posture**, click **Control Inheritance**.

## Header Toolbar

The header area contains action buttons that adapt based on context:

| Button | Visibility | Description |
|--------|-----------|-------------|
| **View Org Defaults** | Always | Opens modal showing org-level inheritance defaults |
| **Derive Org Defaults** | Always | Derives defaults from capability mappings and cascades to all systems |
| **Generate CRM** | Always | Generates and exports the Customer Responsibility Matrix |
| **⋯ More Actions** | When org defaults exist | Dropdown with Apply CSP Profile and Import CRM |
| **Apply CSP Profile** | When no org defaults | Apply a pre-built CSP inheritance profile |
| **Import CRM** | When no org defaults | Import inheritance designations from a CRM spreadsheet |

When org defaults are active, CSP Profile and CRM Import move into the "More Actions" (⋯) dropdown to reduce clutter.

## Managing Designations

### Inline Editing

Click any row in the table to edit its inheritance type:

1. Select the **Inheritance Type** dropdown (Inherited / Shared / Customer).
2. If Inherited or Shared, enter the **Provider** name (e.g., "Azure Government").
3. Optionally fill in **Customer Responsibility** for Shared/Customer controls.
4. Click **Save** — the change is recorded with a Manual audit entry.

### Bulk Update

1. Select multiple controls using the row checkboxes (or the header checkbox for all visible).
2. The **Bulk Update Toolbar** appears above the table.
3. Choose the inheritance type, provider, and responsibility.
4. Click **Apply** — all selected controls are updated in one operation logged as a BulkUpdate source.

### Filtering & Search

- **Family filter** — Narrow to a specific NIST family (e.g., AC, AU, CM).
- **Type filter** — Show only Inherited, Shared, Customer, or Undesignated controls.
- **Search** — Free-text search by control ID.
- **Sort** — Click column headers to sort ascending/descending.
- **Pagination** — Navigate pages for large baselines (50 controls per page).

## Audit History

Click any table row to open the **Audit History Panel** on the right side. It shows:

- Who made each change and when.
- Previous → New values for inheritance type, provider, and responsibility.
- The change source (Manual, BulkUpdate, ProfileApply, CrmImport, OrgDerived, OrgPropagation).

## Generating the CRM

1. Click **Generate CRM** in the header.
2. The CRM view shows controls grouped by family with a summary statistics row.
3. Choose an **export format** (CSV or Excel) and a **layout**:
   - **Custom** — Control ID, Family, Inheritance Type, Provider, Customer Responsibility, Designation Source
   - **FedRAMP** — Aligned with FedRAMP CRM template columns plus Designation Source
   - **eMASS** — Aligned with eMASS import format plus Designation Source
4. Click **Export** to download the file.

## Applying a CSP Profile

Pre-built CSP profiles automatically set inheritance designations for known CSP-provided controls.

1. Click **Apply CSP Profile** in the header.
2. Select a profile (e.g., "Azure Government — FedRAMP High").
3. Choose conflict resolution:
   - **Skip** — Do not overwrite controls that already have a designation.
   - **Overwrite** — Replace existing designations with profile values.
4. Click **Preview Changes** to see how many controls will be set.
5. Click **Apply Profile** to commit the changes.

## Importing a CRM

If you have an existing CRM spreadsheet, you can import it to set designations in bulk.

1. Click **Import CRM** in the header.
2. Drag and drop a CSV or Excel file (or click Browse).
3. Review the detected columns and adjust the **column mapping**:
   - Map source columns to: Control ID (required), Inheritance Type (required), Provider, Customer Responsibility.
4. Review the sample data preview.
5. Choose conflict resolution (Overwrite or Skip).
6. Click **Apply Import** — controls not found in the baseline are flagged.

## Org-Level Inheritance Defaults

Org-level defaults provide a centralized way to define inheritance designations that apply across **all** registered systems in your organization. Instead of setting designations manually per system, you derive defaults from your Security Capabilities Library — capabilities already mapped to NIST controls automatically produce org-wide inheritance rules.

### How It Works

1. **Security capabilities** are mapped to NIST controls in the Capabilities Library (one-time setup).
2. **Derive Org Defaults** scans all org-wide capability-control mappings and creates an `OrgInheritanceDefault` per control with the correct inheritance type, provider, and source capability references.
3. **Cascade propagation** — each derived default is automatically pushed to every system baseline, creating `OrgDerived` designations for controls that don't already have a manual override.
4. When capabilities change (added, updated, deleted), defaults are re-derived and propagated automatically.

### Deriving Org Defaults

1. Click **Derive Org Defaults** in the header toolbar.
2. The system scans org-wide capability-control mappings.
3. A confirmation shows how many controls were derived and how many systems were updated.
4. The summary bar updates with Org Defaults and Overrides counts.

### Viewing Org Defaults

1. Click **View Org Defaults** to open the org defaults modal.
2. The modal lists every default with: Control ID, Inheritance Type, Provider, Source Capabilities, Mapping Role.
3. Use search and pagination to navigate large default sets.

### Source Filter

The **All Sources** dropdown in the filter bar lets you filter the grid by designation source:

| Filter | Shows |
|--------|-------|
| All Sources | All controls (default) |
| Org Defaults | Controls with `OrgDerived` designation source |
| System Overrides | Controls manually set via Manual, CSP Profile, CRM Import, or Bulk Update |
| Undesignated | Controls with no designation |

### Source Badges

Each designated control in the table displays a colored badge indicating its source:

| Badge | Color | Meaning |
|-------|-------|---------|
| Org Default | Teal | Derived from an org-level default |
| CSP Profile | Purple | Applied from a CSP profile |
| CRM Import | Sky | Imported from a CRM spreadsheet |
| Manual | Gray | Set manually or via bulk update |

Controls derived from org defaults also show a teal checkmark tooltip. Controls that override an existing org default show an amber warning tooltip with the org default details.

### Org Default Coverage Banner

When org defaults exist, a teal banner appears below the page header:

> **24 of 339** controls have org-level defaults. Apply a CSP profile to fill remaining gaps (optional).

### Reverting to Org Defaults

If a control was manually overridden but you want to restore the org default:

1. Select one or more controls using the row checkboxes.
2. Click **Revert to Org Defaults** in the bulk action toolbar.
3. Selected controls are reset to their org-derived designation. Controls without an org default are skipped.

### Designation Sources

Every inheritance designation tracks how it was set:

| Source | Description |
|--------|-------------|
| `OrgDerived` | Derived from org-level capability-control mappings |
| `OrgPropagation` | Cascaded to a system during org default derivation |
| `Manual` | Set directly by a user via inline editing |
| `BulkUpdate` | Applied via multi-select bulk update |
| `ProfileApply` | Applied from a CSP profile |
| `CrmImport` | Imported from a CRM spreadsheet |

The designation source is included in the CRM export as a "Designation Source" column across all three layout formats (Custom, FedRAMP, eMASS).

---

## Summary Bar

The summary cards at the top show:

| Card | Description |
|------|-------------|
| Total | Total controls in the baseline |
| Inherited | Controls fully provided by CSP |
| Shared | Shared responsibility controls |
| Customer | Customer-responsible controls |
| Undesignated | Controls not yet classified |
| Inheritance % | Percentage of controls with a designation |
| Org Defaults | Controls with org-level default designations (shown when org defaults exist) |
| Overrides | Controls with system-level override designations (shown when org defaults exist) |
