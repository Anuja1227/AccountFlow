namespace AccountOpeningTracker.Api.Models;

/// <summary>
/// Catalog of permission codes enforced by the backend. The catalog is
/// seeded into the Permissions table; Admin decides which roles hold them.
/// </summary>
public static class Permissions
{
    // Case visibility
    public const string CasesViewAll = "cases.view.all";
    public const string CasesViewAssigned = "cases.view.assigned";
    public const string CasesViewMofsl = "cases.view.mofsl";
    public const string CasesViewSensitive = "cases.view.sensitive";

    // Case data
    public const string CasesCreate = "cases.create";
    public const string CasesSubmit = "cases.submit";
    public const string CasesEdit = "cases.edit";

    // Workflow
    public const string WorkflowOperations = "workflow.operations";
    public const string WorkflowCse = "workflow.cse";
    public const string WorkflowMofsl = "workflow.mofsl";
    public const string WorkflowOverride = "workflow.override";

    // Reporting, AI, audit
    public const string ReportsView = "reports.view";
    public const string AiUse = "ai.use";
    public const string AuditView = "audit.view";

    // Administration
    public const string AdminUsers = "admin.users";
    public const string AdminTeams = "admin.teams";
    public const string AdminRoles = "admin.roles";
    public const string AdminSettings = "admin.settings";

    public sealed record Definition(string Code, string Group, string Description);

    public static readonly Definition[] Catalog =
    {
        new(CasesViewAll, "Cases", "View every account-opening case"),
        new(CasesViewAssigned, "Cases", "View cases where the user is the assigned CSE"),
        new(CasesViewMofsl, "Cases", "View cases that have been submitted to MOFSL"),
        new(CasesViewSensitive, "Cases", "See unmasked client name, PAN and account number"),
        new(CasesCreate, "Cases", "Log new inward requests and resubmission records (Operations)"),
        new(CasesSubmit, "Cases", "Send new account-opening requests with attachments to Operations (CSE)"),
        new(CasesEdit, "Cases", "Edit general case information (client, segment, staff, remarks)"),

        new(WorkflowOperations, "Workflow", "Perform Operations workflow actions (review, queries, forms, MOFSL submission)"),
        new(WorkflowCse, "Workflow", "Respond to Operations queries and submit resolutions as CSE"),
        new(WorkflowMofsl, "Workflow", "Raise MOFSL queries and review Operations responses"),
        new(WorkflowOverride, "Workflow", "Correct workflow fields directly, bypassing the state machine (audited)"),

        new(ReportsView, "Reports", "View MIS dashboards and reports"),
        new(AiUse, "AI", "Use AI assistance (drafting, response checks, summaries, MIS insights)"),
        new(AuditView, "Audit", "View the system-wide audit log"),

        new(AdminUsers, "Administration", "Create, edit, activate and deactivate users"),
        new(AdminTeams, "Administration", "Create and manage teams"),
        new(AdminRoles, "Administration", "Create roles and define their permissions"),
        new(AdminSettings, "Administration", "Change system settings such as the SLA threshold"),
    };

    public static readonly HashSet<string> AllCodes =
        Catalog.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);

    public static readonly string[] AdminPermissions =
        { AdminUsers, AdminTeams, AdminRoles, AdminSettings };
}

/// <summary>Default roles seeded on first run. Admin can edit or add roles.</summary>
public static class DefaultRoles
{
    public const string Administrator = "Administrator";
    public const string OperationsExecutive = "Operations Executive";
    public const string Cse = "CSE";
    public const string MofslProcessor = "MOFSL Processor";
    public const string MisAnalyst = "MIS Analyst";

    public static readonly (string Name, string Description, string[] Permissions)[] Seed =
    {
        (Administrator, "Full system administration. Always holds every permission.",
            Permissions.Catalog.Select(c => c.Code).ToArray()),
        (OperationsExecutive, "Logs inward requests and runs the Operations workflow.", new[]
        {
            Permissions.CasesViewAll, Permissions.CasesViewSensitive,
            Permissions.CasesCreate, Permissions.CasesEdit,
            Permissions.WorkflowOperations, Permissions.ReportsView,
            Permissions.AiUse
        }),
        (Cse, "Customer service executive: responds to queries on assigned cases.", new[]
        {
            Permissions.CasesViewAssigned, Permissions.CasesViewSensitive,
            Permissions.CasesSubmit, Permissions.WorkflowCse, Permissions.AiUse
        }),
        (MofslProcessor, "Reviews MOFSL submissions, raises MOFSL queries and reviews Operations responses.", new[]
        {
            Permissions.CasesViewMofsl, Permissions.CasesViewSensitive,
            Permissions.WorkflowMofsl, Permissions.AiUse
        }),
        (MisAnalyst, "Read-only reporting. Client identifiers are masked.", new[]
        {
            Permissions.CasesViewAll, Permissions.ReportsView,
            Permissions.AiUse
        }),
    };
}

public static class DefaultTeams
{
    public static readonly (string Name, string WorkspaceType, string Description)[] Seed =
    {
        ("Operations", WorkspaceTypes.Operations, "Operations team that logs, reviews and progresses cases"),
        ("CSE", WorkspaceTypes.Cse, "Customer service executives who raise requests and resolve queries"),
        ("MOFSL", WorkspaceTypes.Mofsl, "MOFSL account-opening processing team"),
        ("MIS & Reporting", WorkspaceTypes.Mis, "Management information and reporting"),
        ("Administration", WorkspaceTypes.Admin, "System administrators"),
    };
}
