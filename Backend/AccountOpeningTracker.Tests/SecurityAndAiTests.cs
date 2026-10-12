using AccountOpeningTracker.Api.Ai;
using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Security;

namespace AccountOpeningTracker.Tests;

public class PermissionTests
{
    [Fact]
    public void Grant_override_adds_and_deny_override_wins()
    {
        var effective = AccessService.CombinePermissions(
            new[] { Permissions.CasesViewAll, Permissions.ReportsView },
            new[] { (Permissions.AiUse, OverrideEffects.Grant), (Permissions.ReportsView, OverrideEffects.Deny) });

        Assert.Contains(Permissions.AiUse, effective);
        Assert.Contains(Permissions.CasesViewAll, effective);
        Assert.DoesNotContain(Permissions.ReportsView, effective);
    }

    [Fact]
    public void Deny_beats_grant_for_the_same_permission()
    {
        var effective = AccessService.CombinePermissions(
            Array.Empty<string>(),
            new[] { (Permissions.AiUse, OverrideEffects.Grant), (Permissions.AiUse, OverrideEffects.Deny) });
        Assert.Empty(effective);
    }

    [Fact]
    public void Inactive_profile_has_no_permissions()
    {
        var p = new UserAccessProfile { IsActive = false, Permissions = new HashSet<string> { Permissions.CasesViewAll } };
        Assert.False(p.Has(Permissions.CasesViewAll));
    }

    [Fact]
    public void Workflow_permissions_require_team_membership_for_workspaces()
    {
        var p = new UserAccessProfile
        {
            IsActive = true,
            Permissions = new HashSet<string> { Permissions.WorkflowOperations, Permissions.ReportsView },
            Teams = new[] { new TeamInfo(1, "CSE", WorkspaceTypes.Cse) }
        };
        Assert.False(p.InWorkspace(WorkspaceTypes.Operations));
        Assert.Contains(WorkspaceTypes.Cse, p.Workspaces);
        Assert.Contains(WorkspaceTypes.Mis, p.Workspaces);
        Assert.DoesNotContain(WorkspaceTypes.Admin, p.Workspaces);
    }

    [Fact]
    public void Default_roles_only_reference_known_permissions_and_admin_has_all()
    {
        foreach (var (_, _, codes) in DefaultRoles.Seed)
            Assert.All(codes, c => Assert.Contains(c, Permissions.AllCodes));
        var admin = DefaultRoles.Seed.Single(r => r.Name == DefaultRoles.Administrator);
        Assert.Equal(Permissions.AllCodes.Count, admin.Permissions.Distinct().Count());
        var cse = DefaultRoles.Seed.Single(r => r.Name == DefaultRoles.Cse);
        Assert.DoesNotContain(Permissions.WorkflowOperations, cse.Permissions);
        Assert.DoesNotContain(Permissions.CasesViewAll, cse.Permissions);
    }
}

public class SensitiveDataTests
{
    [Fact]
    public void Redacts_client_name_pan_account_number_email_and_long_numbers()
    {
        var text = "Asha Verma (PAN ABCDE1234F, a/c 9988776655) emailed asha@example.com; call +91 98765 43210. Verma confirmed.";
        var r = SensitiveData.Redact(text, "Asha Verma", "ABCDE1234F", "9988776655");

        Assert.DoesNotContain("Asha", r);
        Assert.DoesNotContain("Verma", r);
        Assert.DoesNotContain("ABCDE1234F", r);
        Assert.DoesNotContain("9988776655", r);
        Assert.DoesNotContain("asha@example.com", r);
        Assert.DoesNotContain("98765", r);
        Assert.Contains("[CLIENT]", r);
        Assert.Contains("[PAN]", r);
    }

    [Fact]
    public void Redacts_any_pan_shaped_value_even_if_not_the_clients()
    {
        Assert.Equal("Joint holder [PAN]", SensitiveData.Redact("Joint holder ZZZZZ9999Z"));
    }

    [Fact]
    public void Masks_identifiers_for_restricted_roles()
    {
        Assert.Equal("••••••234F", SensitiveData.MaskPan("ABCDE1234F"));
        Assert.StartsWith("A", SensitiveData.MaskName("Asha Verma"));
        Assert.DoesNotContain("sha", SensitiveData.MaskName("Asha Verma"));
        Assert.EndsWith("655", SensitiveData.MaskAccountNumber("9988776655"));
    }
}

public class AiOutputValidationTests
{
    [Fact]
    public void Extracts_json_object_from_surrounding_text()
    {
        var json = AiJson.ParseObject("Sure! ```json\n{\"message\": \"Please share address proof.\", \"checklist\": [\"Address proof\"]}\n```");
        Assert.Equal("Please share address proof.", AiJson.RequiredString(json, "message", 5, 500));
        Assert.Equal(new[] { "Address proof" }, AiJson.StringList(json, "checklist", 8, 100));
    }

    [Fact]
    public void Rejects_non_json_output()
    {
        Assert.Throws<AiOutputException>(() => AiJson.ParseObject("I cannot help with that."));
        Assert.Throws<AiOutputException>(() => AiJson.ParseObject("{not json}"));
    }

    [Fact]
    public void Missing_or_too_short_required_fields_fail_validation()
    {
        var json = AiJson.ParseObject("{\"message\": \"Hi\"}");
        Assert.Throws<AiOutputException>(() => AiJson.RequiredString(json, "message", 15, 500));
        Assert.Throws<AiOutputException>(() => AiJson.RequiredString(json, "summary", 1, 500));
    }

    [Fact]
    public void Lists_are_capped_deduplicated_and_ignore_non_strings()
    {
        var json = AiJson.ParseObject("{\"items\": [\"a\", \"a\", 3, \"b\", \"c\", \"d\"]}");
        Assert.Equal(new[] { "a", "b", "c" }, AiJson.StringList(json, "items", 3, 50));
    }

    [Fact]
    public void Long_strings_are_truncated()
    {
        var json = AiJson.ParseObject($"{{\"s\": \"{new string('x', 50)}\"}}");
        Assert.True(AiJson.OptionalString(json, "s", 10)!.Length <= 11);
    }
}
