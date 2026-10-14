# AccountFlow — Account Opening Workflow & MIS Tracker

AccountFlow is a web application for tracking client account-opening cases from the CSE's request to the account being opened. It replaces a shared Excel inward/outward register with one case per Reference ID that moves through six stages, with secure sign-in, team-specific desks, in-app queries, an audit trail, automatic RFT / TAT / SLA / MIS figures, and AI assistance built into the workflow.

The business rules — stages, the 22-status master list, field names and calculated fields — follow the requirement document in [`docs/`](docs/).

<!-- Screenshots: add PNGs to docs/screenshots/ and uncomment.
| Operations desk | Case page | Signed-form AI pre-check | MIS dashboard |
|---|---|---|---|
| ![](docs/screenshots/desk.png) | ![](docs/screenshots/case.png) | ![](docs/screenshots/precheck.png) | ![](docs/screenshots/mis.png) |
-->

## Features

- **Six-stage workflow** — CSE request → form preparation → signed form → discrepancy resolution → submission to MOFSL → account opened. A state machine allows each of the 24 actions only from the right status and only for the right team.
- **Operations maintains the tracker** — logs requests, raises queries, prepares and sends the application form, reviews the signed form, raises discrepancies, submits to MOFSL and tracks every query to resolution. CSE and MOFSL act from their own screens (CSE responds to queries and sends signed forms; MOFSL raises queries, accepts Operations' responses, confirms when there are no (further) queries, and sends the account details once the account is opened), and every step lands in the same tracker record.
- **CSE works inside the app** — sends requests with documents, answers queries, uploads the signed form; Operations reviews every response.
- **In-app queries** — each query is a dated thread between teams; waiting time is counted as query hold days.
- **Resubmissions as new records** — a correction creates a new record with the same Reference ID, so history is never overwritten.
- **Automatic calculations** — Original Inward Date, Resubmission Count, RFT/NRFT, Gross and Net TAT, Current Aging, SLA breach, Stage 1–6 TAT and stage-wise SLA, Current Stage.
- **MIS dashboard** — by CSE, location and segment, with a stage pipeline, overall summary and filters.
- **Access control** — Admin-managed users, teams, roles, permissions and per-user overrides; case-level visibility; masking of client identifiers.
- **AI assistance** — fills forms from the CSE's text, checks completeness and drafts queries, reviews CSE replies, pre-checks signed forms with a vision model. Code makes the decisions and Operations confirms.
- **Audit history** — every action records who, team, from/to status and when.

## Tech stack

| Part | Technology |
|---|---|
| Backend — `Backend/AccountOpeningTracker.Api` | .NET 10 Web API (C#), ASP.NET Core Identity, Entity Framework Core 10, SQL Server LocalDB |
| Tests — `Backend/AccountOpeningTracker.Tests` | xUnit (83 tests) |
| Frontend — `Frontend` | React 19, TypeScript, Vite |
| AI | Groq API — `openai/gpt-oss-120b` (text), `qwen/qwen3.8-27b` (vision); PDFtoImage for PDF pages |

```
Backend/AccountOpeningTracker.Api/
  Controllers/   API endpoints (Auth, Admin, Requests, Accounts, Cases, ApplicationForm, AI)
  Services/      Business logic (CaseService, CaseAccessService, AiAutomationService, ...)
  Workflow/      WorkflowEngine (state machine) and AI decision policies
  Models/        Entities and calculators (RFT, TAT, stage TAT, working days, MIS)
  Security/      Permission checks, masking and redaction
  Data/          ApplicationDbContext
  Ai/            AI providers and prompts
  Migrations/    EF Core migrations
Backend/AccountOpeningTracker.Tests/   Unit tests
Frontend/src/    pages/, components/, lib/ (api.ts, auth, router, types)
docs/            Requirement document
tools/           run.cmd / task.cmd: build + test + frontend build
```

---

## 1. First-time setup

Prerequisites: .NET 10 SDK, `dotnet-ef` (`dotnet tool install -g dotnet-ef`), Node 20+, and SQL Server LocalDB (installed with Visual Studio). The connection string in `appsettings.json` uses `(localdb)\ProjectModels`; change it if your LocalDB instance has a different name.

```powershell
cd Backend\AccountOpeningTracker.Api

# 1) Secrets: stored in your user profile, never in source control
dotnet user-secrets set "Bootstrap:AdminEmail"    "admin@yourcompany.com"
dotnet user-secrets set "Bootstrap:AdminPassword" "<strong password>"   # 10+ chars, upper, lower, digit, symbol
dotnet user-secrets set "Ai:Groq:ApiKey"          "<your Groq key>"     # https://console.groq.com (optional: the app works without AI)

# 2) Create / update the database
dotnet ef database update

# 3) Run the API (https://localhost:7242)
dotnet run --launch-profile https
```

```powershell
cd Frontend
npm install        # first time only
npm run dev        # http://localhost:5173 (proxies /api to https://localhost:7242)
```

Open http://localhost:5173. The login page is always the first page.

**Tests:** `dotnet test Backend\AccountOpeningTracker.Tests` · **Everything at once:** `tools\run.cmd` (build, tests, frontend build; output in `tools\logs\last-run.log`).

**Migrations** only add tables and columns; they never drop existing data. If a migration is pending, the API logs an error at startup and skips seeding until you run `dotnet ef database update`. (`Database:ApplyMigrationsOnStartup=true` applies them automatically; it is off by default.)

## 2. First Administrator (secure bootstrap)

- There is **no public registration** and **no default password**.
- On startup, if no active Administrator exists and `Bootstrap:AdminEmail` / `Bootstrap:AdminPassword` are set (user secrets or environment variables `Bootstrap__AdminEmail` / `Bootstrap__AdminPassword`), the API creates that user with the Administrator role. This is audited.
- Once an Administrator exists, the bootstrap values are ignored. Remove the password secret after the first sign-in (`dotnet user-secrets remove "Bootstrap:AdminPassword"`) and change the password under *My account*.

**Optional demo users (Development only):**

```powershell
dotnet user-secrets set "DemoUsers:Enabled"  "true"
dotnet user-secrets set "DemoUsers:Password" "<a demo password you choose>"
dotnet user-secrets set "DemoUsers:CseName"  "<a CSE name used on records>"   # optional
```

This creates `ops.demo@`, `cse.demo@`, `mofsl.demo@` and `mis.demo@accountflow.local` with matching roles and teams. They are not production credentials.

## 3. Access model

- **Permissions** (18) are checked by the server on every request. A **role** is a set of permissions; Admin can edit roles or create new ones. Seeded roles: Administrator, Operations Executive, CSE, MOFSL Processor, MIS Analyst.
- **Teams** belong to a workspace (Operations, CSE, MOFSL, MIS, Admin). A workflow action needs both its permission and membership of that team.
- **Per-user overrides** grant or deny a single permission; deny always wins.
- **Case scope:** Operations sees all cases; a CSE sees only their own cases; MOFSL sees only cases submitted to MOFSL. Cases outside a user's scope return 404.
- **Masking:** without `cases.view.sensitive`, client name, PAN, account number and free text are masked in API responses.
- **Guard-rails:** no self-deactivation, at least one active Administrator, and nobody can grant permissions they don't hold.
- **Sessions:** Identity cookie, `HttpOnly` + `SameSite=Strict`, 8 h sliding; passwords hashed by Identity; 15-minute lockout after 5 failed sign-ins; sign-in rate-limited by IP. The UI only hides what the API would refuse anyway.

## 4. Six-stage workflow (`Workflow/WorkflowEngine.cs`)

| Stage | Who | What happens |
|---|---|---|
| 1 CSE request | CSE → Operations | CSE sends the request with documents → Operations logs it in the tracker → review → *Raise query to CSE* ⇄ CSE responds → Operations accepts or follows up |
| 2 Form | Operations | Operations prepares the application form (AI can pre-fill it), saves it, then sends it for signature once it has no format errors → **Physical:** to the CSE · **Digital:** to the client. Printable version available. |
| 3 Signed form | CSE → Operations | CSE uploads the signed form and documents → Operations reviews it next to the form that was sent → *Raise discrepancy* / *Return form*, or *Found in order* |
| 4 Resolution | CSE → Operations | The CSE's correction creates a new record (*Discrepancy Resolution* or *Resubmission*) with the same Reference ID → Operations accepts (Found in order) or follows up (Still pending) |
| 5 MOFSL | Operations ⇄ MOFSL | Operations *Submit to MOFSL* → MOFSL *Raise MOFSL query* or *No query — all clear* → Operations *Respond to MOFSL query* → MOFSL *Accept Operations response*, *Raise another query* or *Query resolved*. Only after *No query* / *Query resolved* does MOFSL get *✓ Account opened*. Status stays within the requirement's list (Submitted to MOFSL / Query Raised by MOFSL / Query Resolved); *Action By* shows whose turn it is. |
| 6 Account opened | MOFSL → Operations | MOFSL *✓ Account opened — send details to Operations* (Account Number, Account Opening Date) → Operations sees them on the case and *Log account details in tracker* (pre-filled from MOFSL's details) → *Send communication & close case* |
| Exceptions | Operations | On hold / Resume, Reject, Cancelled by client |

Every action is checked against the current status, open queries, required fields and action dates (not in the future, not before the inward date). All 24 actions go through one endpoint, `POST /api/cases/{referenceId}/actions/{actionCode}`. Direct correction of workflow fields requires `workflow.override` and is audited.

## 5. Calculations (deterministic, unit-tested)

- **Original Inward Date** — earliest inward date across all records of the Reference ID; never reset.
- **Resubmission Count** — records for the Reference ID − 1.
- **RFT / NRFT** — NRFT if any record had a Stage 1 query, a Stage 3 discrepancy or return, or a Stage 5 MOFSL query.
- **Gross TAT** — working days (Mon–Fri) from Original Inward Date to Account Opening Date, or to today while open. **Net TAT** — Gross TAT − query hold days.
- **Current Aging** — Original Inward Date to today, for open cases.
- **SLA breach** — Gross TAT above the threshold (default 7 working days, set by Admin).
- **Stage 1–6 TAT and stage-wise SLA** — working days spent in each stage, each flagged against the same threshold. Stages 1–5 add up to Gross TAT.
- **MIS** — one value per Reference ID (its latest record), so resubmissions add records but not cases. Filters apply to every figure.

TAT and aging are recalculated on every read, so open cases age without anyone editing them. Code: `Models/RftHelper.cs`, `TatHelper.cs`, `StageTatHelper.cs`, `MisCalculator.cs`.

## 6. AI automation

Rule: **AI reads and drafts, code checks and decides, a person confirms.**

| # | Feature | Trigger | What it does |
|---|---|---|---|
| 1 | **Quick fill** | *Fill form with AI* when logging a request | Extracts reference, client, PAN, account type, channel, location, segment, CSE, RM and notes from the CSE's text. Values must appear in the text and pass validation. |
| 2 | **Completeness check** | A new case is saved | Required-field rules (code) plus an AI check for the account type (e.g. Minor → guardian details). Missing items → a drafted Stage 1 query to CSE; complete → move to form preparation. |
| 3 | **CSE reply review** | CSE responds to a query | Compares the reply with the query. Resolved → accept; not resolved → follow-up listing what is outstanding. Never marks a signed form "found in order". |
| 4 | **Application form fill** | *Fill with AI* on the form | Fills empty fields from the CSE's request and replies; values must appear in that text and pass format checks. |
| 5 | **Signed-form pre-check (vision)** | CSE uploads the signed form / corrected documents, or *Run AI check* | A vision model reads up to 3 images or PDF pages (PDFs rendered with PDFtoImage) and reports document type, readability, whether a signature is present, and the values written. **Code** compares name, PAN, DOB, bank account and IFSC with the case. Issues produce a drafted discrepancy that Operations edits and confirms. It never judges whether a signature is genuine. |

Also: an **AI case briefing** on the case page and **AI MIS commentary** on the dashboard (read-only).

**Modes** (Admin → Settings): **Automatic** — the AI applies steps itself (shown in History as *AccountFlow AI*). **Suggest only** (recommended) — each result is a suggestion Operations applies with one click. **Off** — nothing runs automatically; checks can still be run by hand.

**Safeguards:** the AI returns JSON that is validated; `Workflow/AutomationPolicy.cs` turns it into steps using confidence thresholds (≥ 0.60 to act on gaps, ≥ 0.75 to move a case forward); every step runs through the same workflow rules and audit as a person. The AI cannot change TAT, SLA, RFT or MIS figures, skip a stage, or bypass permissions. If the AI is unavailable, work continues by hand.

**Provider and data:** Groq free tier, called only from the backend (OpenAI-compatible API); the key never reaches the browser. Other providers are a setting (`Ai:Provider`: Ollama, Anthropic, OpenAI, Disabled). Client name, PAN, account number, e-mails and phone numbers are redacted before the completeness check, reply review, briefing and MIS commentary. Quick fill, form fill and the document pre-check need the real text or image — use synthetic data. Only call metadata is logged (Admin → AI usage).

## Future AI Enhancements

### 1. AI-Powered Document Verification Copilot
Extend the existing AI document checks to analyze an entire account-opening document set collectively. A multimodal AI model could extract and cross-verify information across application forms, PAN documents, address proofs, and other supporting documents to identify missing documents, inconsistent information, and potential discrepancies. It could also generate a verification report and draft discrepancy queries for human review.

### 2. AI-Powered Case Risk & Root-Cause Intelligence
Introduce AI-driven analysis of case history and MIS data to identify cases at risk of SLA breaches, recurring workflow bottlenecks, and common causes of delays. The system could explain the factors contributing to a case's risk and recommend appropriate follow-up actions, helping Operations prioritize pending work and management identify process improvements.

**Potential technology extensions:** Multimodal AI models for document analysis, reasoning-capable language models for case intelligence, and structured integration with the existing .NET backend and SQL Server database.

*These are proposed extensions and are not part of the current implementation. Any AI-generated findings or recommendations would require appropriate validation and human review.*

## 7. Demonstration script

1. **Admin:** create a team, a role and users — an Operations user, a CSE user (with a *CSE name* matching the case) and a MOFSL user.
2. **CSE:** *Send request to Operations* with details and a sample document. **Operations:** *Incoming requests* → *Log in tracker* → *Fill form with AI* → save. The AI completeness check suggests the Stage 1 query or the move to form preparation.
3. **CSE:** *Queries to respond to* → reply. The AI reviews the reply; Operations accepts or follows up.
4. **Operations:** *Application form* → *Fill with AI* → edit → *Save form* → *Send to CSE for signature*. **CSE:** *Forms to get signed* → *Send signed form to Operations* with the signed scan. **Operations:** *Signed forms to verify* → review the AI pre-check → *Raise discrepancy* or *Form found in order* → *Submit to MOFSL*.
5. **MOFSL:** *Raise MOFSL query* (or *No query — all clear*) → **Operations:** *Respond to MOFSL query* → **MOFSL:** *Accept Operations response* / *Raise another query* / *Query resolved* → *✓ Account opened — send details to Operations*. **Operations:** *Log account details in tracker* → *Send communication & close case*.
6. **MIS dashboard** and **History** show the case, its figures and its full audit trail.
7. **Unauthorized access:** as the CSE user, open `/admin/users` (Not available); call `GET /api/admin/users` (403) or any `/api/...` without signing in (401).

The demo cases used in the live presentation (DEMO-FORM-01, DEMO-SIGN-01, DEMO-MOFSL-01, DEMO-RFT-01) live in the local demo database; on a fresh setup, create cases by following the steps above. The Groq free tier allows about two image checks per minute, so space out document uploads.

## 8. Known limitations

- In-app communication only; no e-mail, SMS or WhatsApp notifications.
- Working days are Monday–Friday; holidays are not excluded.
- The signature check confirms a signature is present; it does not verify that it is genuine.
- A CSE's case scope matches the CSE name stored on records.
- The Groq free tier has rate and daily limits; when reached, AI features show a message and the workflow continues without AI.
