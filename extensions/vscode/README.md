# ATO Copilot — VS Code Extension

GitHub Copilot Chat participant (`@ato`) for NIST 800-53 compliance assessment, remediation planning, and ATO documentation within VS Code.

## Features

- **`@ato` Chat Participant** — Ask compliance questions directly in GitHub Copilot Chat
- **Slash Commands** — `/compliance`, `/knowledge`, `/config` for targeted agent routing
- **File Analysis** — Analyze the current editor file for compliance issues
- **Workspace Analysis** — Scan `.bicep`, `.tf`, `.yaml`, `.yml`, `.json` files across the workspace
- **Export** — Export analysis results as Markdown, JSON, or HTML
- **Template Saving** — Save generated IaC templates organized by type

## Installation

1. Open VS Code
2. Go to Extensions (`Ctrl+Shift+X` / `Cmd+Shift+X`)
3. Search for "ATO Copilot"
4. Click **Install**

### From Source

```bash
cd extensions/vscode
npm install
npm run compile
```

Then press `F5` to launch the Extension Development Host.

## Commands

| Command | Description |
|---------|-------------|
| `ATO Copilot: Check Health` | Verify connection to the MCP Server |
| `ATO Copilot: Configure` | Open extension settings |
| `ATO Copilot: Analyze Current File` | Analyze the active editor file for compliance |
| `ATO Copilot: Analyze Workspace` | Scan all supported files in the workspace |
| `ATO: Sign In` | Run an Entra device-code flow against the configured cloud and cache the token in VS Code SecretStorage (Feature 051 / US5) |
| `ATO: Sign Out` | Clear the cached token from SecretStorage and update the status bar |
| `ATO: Switch Tenant` | Restart the device-code flow against a different Entra tenant for multi-tenant accounts |

## Authentication

Feature 051 (US5) wires CAC/PIV sign-in directly into the extension.

### Sign in for the first time

1. Run any `@ato` command in Copilot Chat (or invoke
   **ATO: Sign In** from the command palette).
2. A VS Code modal shows an 8-character Entra **user code** and a
   countdown.
3. The extension opens your default browser to the cloud-appropriate
   device-login URL —
   `https://microsoft.com/devicelogin` for **AzurePublic** or
   `https://microsoft.us/devicelogin` for **AzureUSGovernment** — the
   choice is driven by the server's `Auth:Cloud` setting reported via
   `/api/auth/login-config`.
4. Insert your CAC/PIV, enter the user code, complete the standard
   sign-in.
5. The extension caches the access + refresh token in **VS Code
   SecretStorage**, keyed per tenant (`ato.token.<tid>`), and writes
   `ATO: <displayName>` to the status bar.

### Subsequent invocations

- Tokens refresh **silently** via MSAL Node — no prompt unless the
  refresh path itself fails.
- A `401` from the MCP server (token rejected, conditional-access
  change, account disabled) surfaces a **single** notification
  "Sign in again to ATO Copilot" with a button. The extension does
  NOT silently retry.

### Switch tenants

Run **ATO: Switch Tenant** — a quick-pick lists every tenant you have
a cached token for, plus an "Add another tenant…" item that opens a
fresh device-code flow. The status bar updates to reflect the active
tenant.

### Per-tenant SecretStorage

Tokens are stored under separate keys per Entra tenant so a
multi-tenant developer can keep sessions for several customers active
simultaneously. Running **ATO: Sign Out** clears only the current
tenant's entry; use **ATO: Sign Out (All)** to flush every cached
token.

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `atoCopilot.apiUrl` | `http://localhost:3001` | MCP Server URL |
| `atoCopilot.apiKey` | `""` | API key for authentication |
| `atoCopilot.timeout` | `30000` | Request timeout in milliseconds |
| `atoCopilot.enableLogging` | `false` | Enable debug logging to output channel |

## Usage

### Chat with @ato

In Copilot Chat, type:

```
@ato How do I comply with AC-2 access account management?
```

### Use Slash Commands

```
@ato /compliance What controls apply to my storage accounts?
@ato /knowledge Explain FedRAMP Moderate baseline
@ato /config Show current compliance configuration
```

### Analyze Files

1. Open a `.bicep`, `.tf`, or `.yaml` file
2. Open Command Palette (`Ctrl+Shift+P` / `Cmd+Shift+P`)
3. Run **ATO Copilot: Analyze Current File for Compliance**
4. View findings in the side panel with severity-colored badges

## Requirements

- VS Code 1.90.0 or later
- GitHub Copilot Chat extension
- ATO Copilot MCP Server running (default: `http://localhost:3001`)

## Development

```bash
npm install
npm run compile
npm test
```

## License

MIT
