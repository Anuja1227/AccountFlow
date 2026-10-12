# AccountFlow — Account Opening Workflow & MIS Tracker

AccountFlow replaces the manual Excel inward/outward register for client onboarding. It is a
database-backed web app with secure sign-in, Admin-managed users, teams, roles and permissions,
separate Operations / CSE / MOFSL / MIS / Admin workspaces, a validated six-stage workflow,
inter-team queries, an audit trail, automatic RFT/TAT/SLA/MIS figures and AI assistance built
into the workflow.

| Part | Technology |
|---|---|
| `Backend/AccountOpeningTracker.Api` | .NET 10 Web API, EF Core 10, SQL Server LocalDB, ASP.NET Core Identity |
| `Backend/AccountOpeningTracker.Tests` | xUnit tests for calculations, workflow, permissions, redaction and AI output validation |
| `Frontend` | React 19 + TypeScript + Vite (no new npm dependencies) |
| `tools/run.cmd` | Helper that runs `tools/task.cmd` and writes `tools/logs/last-run.log` |

---

## 1. First-time setup

Prerequisites: .NET 10 SDK, `dotnet-ef` (`dotnet tool install -g dotnet-ef`), Node 20+, and SQL Server LocalDB (installed with Visual Studio).

```powershell
cd Backend\AccountOpeningTracker.Api

# 1) Secrets: stored in your user profile, never in source control
dotnet user-secrets set "Bootstrap:AdminEmail"    "admin@yourcompany.com"
dotnet user-secrets set "Bootstrap:AdminPassword" "<strong password>"   # 10+ chars, upper, lower, digit, symbol
dotnet user-secrets set "Ai:Groq:ApiKey"          "<your free Groq key>" # https://console.groq.com

# 2) Database: additive migration, existing Accounts rows are preserved
dotnet ef database update

# 3) Run the API (https://localhost:7242)
dotnet run --launch-profile https
```

```powershell
cd Frontend
npm install        # only needed once
npm run dev        # http://localhost:5173  (proxies /api to https://localhost:7242)
```

Open http://localhost:5173. The **login page is always the first page you see**.

### Migrations

`20261011074701_AddAuthTeamsWorkflowAi` only **adds**:
- nullable columns `Accounts.StatusBeforeHold`, `Accounts.CreatedAt` and `Accounts.UpdatedAt`
- tables `AspNetUsers` (+ claims/logins/tokens), `Teams`, `AppRoles`, `Permissions`, `RolePermissions`,
  `AppUserRoles`, `UserTeams`, `UserPermissionOverrides`, `SystemSettings`, `CaseQueries`,
  `CaseQueryMessages`, `AuditEvents` and `AiInteractionLogs`

It does not drop, rename or alter existing data. If migrations are pending at startup, the API logs an
error and skips seeding. You can set `Database:ApplyMigrationsOnStartup=true` to apply them
automatically (it is off by default).

---

## 2. Provisioning the first Administrator (secure bootstrap)

- There is **no public registration** and **no default password**.
- On startup, if no *active* user holds the `Administrator` role **and** both `Bootstrap:AdminEmail` and
  `Bootstrap:AdminPassword` are set (user-secrets or environment variables `Bootstrap__AdminEmail` /
  `Bootstrap__AdminPassword`), the API creates that user, gives them the Administrator role and puts
  them in the Administration team. This is audited.
- Once an active Administrator exists, the bootstrap values are ignored. **Remove the password secret
  after the first sign-in** (`dotnet user-secrets remove "Bootstrap:AdminPassword"`) and change the
  password under *My account*.
- The same mechanism recovers access if every Administrator was deactivated.

### Optional development demo users

These are for local demos only and are never created outside the `Development` environment:

```powershell
dotnet user-secrets set "DemoUsers:Enabled"  "true"
dotnet user-secrets set "DemoUsers:Password" "<a demo password you choose>"
dotnet user-secrets set "DemoUsers:CseName"  "<a CSE name used on existing records>"   # optional
```

This creates `ops.demo@`, `cse.demo@`, `mofsl.demo@` and `mis.demo@accountflow.local` with the matching
default roles and teams. **These are not production credentials.** Disable this and deactivate the
users before any shared deployment.

---

## 3. Access model

| Concept | Purpose |
|---|---|
| **Permission** | A fine-grained capability checked by the server (catalog in `Models/Permissions.cs`). |
| **Role** | A named set of permissions defined by Admin. The seeded roles are Administrator (locked; always holds every permission), Operations Executive, CSE, MOFSL Processor and MIS Analyst. Admin can edit these or create new ones. |
| **Team** | Belongs to a workspace type (Operations, CSE, MOFSL, MIS or Admin). Membership decides which workflow steps a user owns. |
| **User override** | A per-user Grant or Deny for one permission. Deny always wins. |
| **Case scope** | `cases.view.all` sees every case; `cases.view.assigned` sees only cases whose CSE matches the user's *CSE name*; `cases.view.mofsl` sees only cases that have been submitted to MOFSL (view only: Operations records every Stage 5–6 step). |
| **Sensitive data** | Without `cases.view.sensitive`, client name, PAN, account number and free text are masked in every API response. |

A workflow action needs **both** the action's permission (for example `workflow.operations`) **and**
membership of a team with that workspace. Everything is enforced in the backend. The UI only hides
what the API would refuse anyway.

Admin guardrails:
- You cannot deactivate yourself.
- At least one active Administrator must remain.
- You cannot remove your own user-administration permission.
- **Nobody can grant permissions they do not hold**, which blocks privilege escalation.
- Deactivating a user or changing their roles rotates their security stamp, which signs them out
  everywhere within about a minute; each request also re-checks that the user is still active.

Sessions use an Identity cookie that is `HttpOnly` and `SameSite=Strict` (8 h sliding). Passwords
use Identity's hashing. Accounts lock for 15 minutes after 5 failed sign-ins. Sign-in is rate-limited
by IP.

---

## 4. Workflow (state machine in `Workflow/WorkflowEngine.cs`)

| Stage | Owner | Actions |
|---|---|---|
| 1 CSE request | CSE / Operations | CSE **sends the request** (details + documents) → Operations opens it in *Incoming requests* and **logs it in the tracker** → Start review → *Raise query to CSE* ⇄ CSE responds → Accept / Follow up → Information complete |
| 2 Form | Operations | The **application form** opens on the case page: AI pre-fills it from the CSE request and query replies, Operations checks, edits and saves it, then **sends it for signature** (only possible once the form is complete) → **Physical:** to the CSE / **Digital:** to the client. A printable version (print / save as PDF) is on the case. |
| 3 Signed form | CSE / Operations | CSE gets the client's signature and uses **Send signed form to Operations** (uploads the signed form + documents) → Operations sees the form next to the uploaded documents → Review → *Raise discrepancy* or *Return form* (CSE replies with corrected documents), or Found in order |
| 4 Resolution | CSE / Operations | The CSE's response creates a **new record** (`Discrepancy Resolution` or `Resubmission`) with the same Reference ID → Operations accepts (Found in order) or follows up |
| 5 MOFSL | Operations | Submit to MOFSL → if MOFSL raises a query, Operations *records the query* (date, type, details) → Operations *records the resolution* (date) |
| 6 Account opened | Operations | Operations *records account opened* with the account number and opening date MOFSL provides → sends communication & closes |
| Exceptions | Operations | On hold / Resume, Reject, Cancelled by client |

Every action is checked against the current status, any open queries, required fields, action dates
(not in the future, not before inward) and `WorkflowValidationHelper`. Queries carry source and
recipient teams and a status (Open → Responded → Resolved). The full message thread is kept. Hold
days, measured in working days from when a query was raised or reopened until the response, are
added to the case's cumulative *Query Hold Days*. Every change is written to `AuditEvents` as who,
team, action, from/to status and when, with no client data.

Workflow fields can only be set directly through `PUT /api/accounts/{id}` (and create-with-data),
which requires `workflow.override` and is audited.

---

### Stage 1 intake: CSE request → Operations entry

- **CSE → Send request to Operations:** the CSE writes the request details and attaches up to 5 documents (JPG, PNG, WEBP or PDF, 4 MB each; file types are checked by content). Requires `cases.submit` and a CSE team; the request is tagged with the CSE's linked name.
- **Operations → Incoming requests:** lists requests waiting to be logged. **Log in tracker** opens the *Log inward request* form with the request shown above it. The CSE and inward date (the day Operations received it) are pre-set, and **Fill the entry from this request** lets AI fill the remaining fields. Operations reviews and saves. This is the requirement's "Operations make entry in the tracker".
- On save, the request becomes *Logged* and is linked to the case; its documents appear on the case page under *Documents from the CSE request*. A request can only be logged once.
- Documents can be opened only by users who can see the case and hold `cases.view.sensitive`. The AI reads the request **text**, not the documents.

---

## 5. Calculations (deterministic, unit-tested)

- **Original Inward Date:** the earliest inward date across all records sharing the Reference ID. Never reset.
- **Resubmission count:** records for the Reference ID − 1. Derived, never entered.
- **RFT/NRFT:** NRFT if any record has a Stage 1 query, a Stage 3 discrepancy or return, or a Stage 5 MOFSL query (`RftHelper`).
- **Gross TAT:** working days (Mon–Fri) from the Original Inward Date to the account opening date, or to today while open.
- **Net TAT:** Gross TAT − cumulative query hold days, never below 0.
- **Current aging:** Original Inward Date to today for open cases.
- **SLA:** breached when Gross TAT > threshold. Default 7; Admin changes it in Settings (1–60).
- **Stage 1–6 TAT** (`StageTatHelper`): working days spent in each stage. A stage starts on the first date the requirements record for it and ends when the next stage starts: Stage 1 Original Inward Date · Stage 2 Form Prepared Date (set when Operations first saves the form) · Stage 3 Signed Form Received Date · Stage 4 Discrepancy Resolution / Resubmitted Form Received Date (only when a discrepancy was raised) · Stage 5 Submitted to MOFSL Date · Stage 6 Account Opening Date, ending on the Communication Sent date. Stages 1–5 add up to the Gross TAT; the current stage counts to today.
- **Stage-wise SLA:** a stage is breached when its TAT exceeds the same configurable threshold. Shown on the case page (Time in each stage), in the register (Over in Stage N) and in the MIS (average days in stage, stage-wise breaches per stage and per CSE/location/segment).
- TAT and aging are **recomputed on every read**, so open cases age correctly without a write.
- **MIS** (`MisCalculator`): case-level metrics (RFT %, TAT averages, SLA breaches, attribution) use one value per Reference ID, its latest record, so resubmissions add records but not cases. Filters apply to the summary, the breakdowns by CSE/location/segment, the pipeline and the AI commentary alike.

---

## 6. AI-powered automation

The AI takes over five pieces of reading and drafting work: retyping the CSE's request, checking it for gaps and
writing the query, reading the CSE's reply, filling the application form, and pre-checking the signed form that comes back.
Every result is checked by code, and Operations confirms anything that changes the case.

| # | Automation | Trigger | What happens without a person |
|---|---|---|---|
| 1 | **Quick fill** | Operations types or pastes the request details on *Log inward request* | AI extracts Reference/Request ID, client, PAN, account type, channel, location, segment, CSE, RM, inward date and notes into the form. Fields are validated (allowed values, PAN format, date range) and names/PAN must literally appear in the text. Operations reviews and saves. |
| 2 | **Auto-check + auto-query** | A new request is saved | Required-field rules (code) plus an AI completeness check for the account type (e.g. Minor → guardian details; NRI → overseas address) and the notes. **Incomplete:** the AI starts review and **raises the Stage 1 query to CSE** listing exactly what is missing. **Complete:** the case moves straight to form preparation. **Unsure:** left for Operations. |
| 3 | **Auto-review of the CSE reply** | CSE responds to a query | AI compares the reply with every point in the thread. **Stage 1, resolved:** accepts the reply and moves the case to form preparation. **Not resolved:** sends the follow-up listing what is still outstanding. **Stage 3 (discrepancy / returned form):** it may send a follow-up, but it never marks a physical form "found in order", because Operations must look at the corrected form. |
| 4 | **Application form fill** | Ops clicks *Fill with AI* on the application form (automatic on first open in Automatic mode) | Fills empty form fields (DOB, contact, address, bank, segments, nominee…) from the CSE request and query replies. Values must appear in the CSE's text and pass format checks; Operations reviews, saves and sends. |
| 5 | **Signed-form pre-check (vision)** | CSE uploads the signed form / corrected documents (or Ops clicks *Run AI check*) | A vision model (`Ai:Groq:VisionModel`, default `qwen/qwen3.8-27b`) reads up to 3 images or PDF pages (PDFs are rendered to images on the server with PDFtoImage) and reports document type, readability, whether a signature is present, and the values written. **Code** compares name, PAN, DOB, bank account and IFSC with the case/form. Issues produce a drafted discrepancy that Operations opens, edits and confirms — the AI never raises it, in any mode, and never judges whether a signature is genuine. |

Also available (read-only): an **AI case briefing** on the case page and **AI MIS commentary** on the dashboard.

**Modes** (Admin → Settings → *AI automation mode*):
- **Automatic:** the AI acts. Its actions appear in History as *AccountFlow AI*, and query messages are tagged *AI-assisted*.
- **Suggest only** (recommended for the demo): each check records a recommendation that Operations applies with **one click** (run with the user's own permissions).
- **Off:** nothing runs automatically. Operations can still click *Run intake check* or *Review CSE reply*, which produces a suggestion.

**How decisions are made:**
1. The AI returns only an assessment (JSON), which is validated.
2. `Workflow/AutomationPolicy.cs` (plain, unit-tested code) turns it into workflow steps using confidence thresholds: act on gaps at ≥ 0.60, move a case forward only at ≥ 0.75. Required-field gaps always trigger a query, even if the AI says the request is complete.
3. Each step runs through the same `WorkflowEngine` validation as a human action.

The AI cannot change TAT, SLA, RFT or MIS figures, skip a stage, or bypass permissions. If the AI is unavailable, the case simply waits for a person. Each result appears on the case page as an **AI suggestion** in the next-action bar (with its findings and confidence), and every AI run is listed under **History → AI only**.

**Provider:** [Groq](https://console.groq.com) free tier, model `openai/gpt-oss-120b` (configurable: `Ai:Groq:Model`; the status check lists the models your key can use), called from the backend through its OpenAI-compatible API. Set the key with `dotnet user-secrets set "Ai:Groq:ApiKey" "<key>"`; it never reaches the browser. Other providers are configuration-only: `Ollama`, `Anthropic`, `OpenAI` and `Disabled` (`Ai:Provider`).

**Data:** for the completeness check, reply review, briefing and MIS commentary, client name, PAN, account number, e-mails, phone numbers and staff names are redacted before sending. Quick fill has to read the request text to extract the fields, so that text is sent as-is. Use synthetic data in demos. Prompts and outputs are not stored; `AiInteractionLogs` keeps metadata only (Admin → Audit → AI usage).

---

## 7. Demonstration script

1. Sign in as the bootstrap Admin. Under **Teams** create e.g. *Operations Mumbai* (Operations). Under **Roles**, create or adjust a role. Under **Users**, create an Operations user (Operations Executive role + Operations team), a CSE user (CSE role + CSE team + *CSE name* matching the case) and a MOFSL user.
2. Sign in as the CSE → **Send request to Operations** → write the details and attach a sample document. Then sign in as Operations → **Incoming requests** → **Log in tracker** → **Fill the entry from this request** → save. (Alternatively, use **Log inward request** directly and type the request details, e.g. *"Ref MUM-104, Req 88231. Client Rohan Mehta, PAN ABCPM1234K, Minor account, physical, Mumbai, Retail, RM Priya Nair, CSE <name>. Guardian details to follow."* → **Fill form with AI** → save.) The case page shows the AI found the missing guardian details and **raised the query to CSE itself**.
3. Sign in as CSE → **CSE desk** → *Queries to respond to* → reply with only part of the information. The AI **sends a follow-up automatically**. Reply again with everything, and the AI **accepts the reply and moves the case to form preparation**.
4. Operations → *Application form* panel → **Fill with AI** → check / edit → **Save form** → **Send to CSE for signature**. CSE → *Forms to get signed* → open the case → *View / print form* → **Send signed form to Operations** (attach the signed form). Operations → *Signed forms to verify* → *Start signed-form review* → *Form found in order* → *Submit to MOFSL*.
5. Operations → on the submitted case, optionally *Record query raised by MOFSL* → *Record MOFSL query resolution* → *Record account opened* (account number and date provided by MOFSL). MOFSL users can view submitted cases on the **MOFSL desk** but do not change them.
6. Operations → *Send communication & close*. **MIS dashboard** and **Audit log** show the case and its history.
7. Unauthorized access: sign in as the CSE user and open `/admin/users` (Not available). Call `GET /api/admin/users` (403), or call any `/api/...` without signing in (401).

---

### Ready-made demo cases (synthetic data)

| Reference | Where it is | What to show |
|---|---|---|
| Request #1004 (DEMO-REQ-01) | Incoming requests, not logged yet | CSE request with PAN card + address proof → *Log in tracker* → *Fill form with AI* → save → AI intake check |
| DEMO-FORM-01 | Stage 2 · form to prepare | *Application form* tab → *Fill with AI* (details come from the CSE request) → save → *Send to CSE for signature* |
| DEMO-SIGN-01 | Stage 3 · signed form received | *Documents* tab: AI pre-check found no signature and a wrong PAN → *Review & raise discrepancy* |
| DEMO-MOFSL-01 | Stage 5 · NRI, digital · MOFSL query open | Ops → *Record MOFSL query resolution* → *Record account opened* |
| DEMO-RFT-01 | Closed · Right First Time | Full history; AI flagged a harmless detail that Ops dismissed; RFT/TAT in *Records & TAT* and MIS |

The free Groq tier allows about two document images per minute for the vision model; space out live uploads in a demo.

## 8. Known limitations

- In-app communication only. No e-mail or SMS notifications are sent.
- Holidays are not excluded from working days (Mon–Fri only, as specified).
- The CSE's case scope matches on the CSE *name* stored on records. Renaming a CSE on records requires updating the user's CSE name.
- The Groq free tier has daily token limits. When they are reached, AI features show a rate-limit message and the workflow continues without AI.
