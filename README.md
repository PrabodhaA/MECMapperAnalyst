# MEC Map Analyst — Deployment Guide

## What this is
A web chat application where consultants upload an Infor MEC/IEC mapper XML file
and ask questions about it in plain English. Powered by Claude AI + the mec-map-analyst skill.

## Architecture
```
wwwroot/index.html          → Azure Static Web App (free)
MapAnalystFunction/         → C# Azure Function .NET 8 (free — 1M calls/month)
  AnalyseFunction.cs        → POST /api/chat  +  GET /api/health
  Skills/mec-map-analyst.md → Bundled skill — your rules, nobody else sees them
```

---

## Prerequisites

Install these once on your machine:

1. **Git** — https://git-scm.com
2. **.NET 8 SDK** — https://dotnet.microsoft.com/download/dotnet/8.0
3. **Azure Functions Core Tools v4** — https://learn.microsoft.com/azure/azure-functions/functions-run-local
   ```
   npm install -g azure-functions-core-tools@4 --unsafe-perm true
   ```
4. **Azure CLI** — https://learn.microsoft.com/cli/azure/install-azure-cli
5. **VS Code** (optional but recommended) + Azure Functions extension

---

## Step 1 — Get your Anthropic API key

1. Go to https://console.anthropic.com
2. Settings → API Keys → Create Key
3. Copy the key — you'll need it in Step 4

---

## Step 2 — Push code to GitHub

1. Create a new **private** GitHub repository (e.g. `mec-analyst-app`)
2. From the project folder:
   ```bash
   git init
   git add .
   git commit -m "Initial commit"
   git remote add origin https://github.com/YOUR-USERNAME/mec-analyst-app.git
   git push -u origin main
   ```

---

## Step 3 — Create Azure Static Web App

1. Go to https://portal.azure.com
2. Search for **Static Web Apps** → Create
3. Fill in:
   - **Subscription**: your subscription
   - **Resource Group**: create new → `mec-analyst-rg`
   - **Name**: `mec-analyst-app`
   - **Plan**: Free
   - **Region**: East US (or closest to you)
   - **Source**: GitHub
   - **Organization**: your GitHub username
   - **Repository**: `mec-analyst-app`
   - **Branch**: `main`
   - **Build Presets**: Custom
   - **App location**: `wwwroot`
   - **API location**: `MapAnalystFunction`
   - **Output location**: (leave blank)
4. Click **Review + Create** → **Create**

Azure will automatically create a GitHub Actions workflow and deploy your app.
Your URL will be something like: `https://mango-sand-abc123.azurestaticapps.net`

---

## Step 4 — Add your Anthropic API key to Azure

**IMPORTANT: Never put your API key in code or commit it to GitHub.**

1. In Azure Portal → your Static Web App → **Configuration**
2. Click **+ Add**
3. Name: `ANTHROPIC_API_KEY`
4. Value: paste your Anthropic API key
5. Click **OK** → **Save**

The Azure Function reads this automatically via `Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")`.

---

## Step 5 — Set a budget alert (IMPORTANT)

To avoid surprise charges:

1. Azure Portal → search **Cost Management + Billing**
2. **Budgets** → **+ Add**
3. Set amount: $10/month
4. Add your email for alerts at 80% and 100%

Note: Azure Functions has 1 million free executions/month — you won't exceed this
for internal team use. The only cost is Anthropic API usage (~$0.01–0.05 per conversation).

---

## Step 6 — Test locally (optional)

```bash
cd MapAnalystFunction

# Add your API key to local.settings.json
# Edit: "ANTHROPIC_API_KEY": "your-key-here"

# Run the function locally
func start
```

Then open `wwwroot/index.html` in a browser.
Change `const API_BASE = '/api'` to `const API_BASE = 'http://localhost:7071/api'` for local testing.
Remember to revert before committing.

---

## Updating the skill

When you want to update the skill rules:

1. Edit `MapAnalystFunction/Skills/mec-map-analyst.md`
2. Commit and push:
   ```bash
   git add .
   git commit -m "Update skill rules — add IJQ-021"
   git push
   ```
3. GitHub Actions automatically redeploys to Azure in ~2 minutes.
   Nobody needs to do anything — the update is live for everyone.

---

## Adding a second skill later

1. Add a new `.md` file to `MapAnalystFunction/Skills/`
   e.g. `Skills/iec-java-quality.md`
2. In `AnalyseFunction.cs`, update the skill loading to support multiple skills
   (or keep separate Function endpoints per skill)
3. Commit and push — done.

---

## Troubleshooting

**"Could not connect to the server"**
→ Check Azure Function is deployed: Azure Portal → Static Web App → Functions tab

**"Failed to get response from Claude API"**
→ Check ANTHROPIC_API_KEY is set in Azure Configuration → Application Settings

**Map upload returns empty response**
→ Map may be too large. Strip `<Links>` and `<SchemaOut>` sections before uploading
  (the Function does this automatically, but very large files may still timeout)

**Function times out (> 45 seconds)**
→ Large maps with complex questions can be slow. Split the question into smaller parts
  or pre-strip the map XML before uploading.

---

## File structure reference
```
mec-analyst-app/
├── .gitignore
├── README.md
├── wwwroot/
│   ├── index.html                  ← Chat UI
│   └── staticwebapp.config.json    ← Azure routing
└── MapAnalystFunction/
    ├── MapAnalystFunction.csproj
    ├── Program.cs
    ├── host.json
    ├── local.settings.json         ← LOCAL ONLY — never committed
    ├── AnalyseFunction.cs          ← C# Azure Function
    └── Skills/
        └── mec-map-analyst.md      ← Bundled skill — your IP
```
