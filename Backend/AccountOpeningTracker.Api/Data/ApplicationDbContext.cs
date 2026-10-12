using AccountOpeningTracker.Api.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AccountOpeningTracker.Api.Data;

/// <summary>
/// Uses Identity's user store (AspNetUsers, claims, logins, tokens) for
/// authentication. Roles/permissions/teams are AccountFlow tables so that
/// Admin can define custom roles with fine-grained permissions.
/// </summary>
public class ApplicationDbContext : IdentityUserContext<AppUser, int>
{
    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Account> Accounts { get; set; }

    public DbSet<Team> Teams => Set<Team>();
    public DbSet<AppRole> AppRoles => Set<AppRole>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> AppUserRoles => Set<UserRole>();
    public DbSet<UserTeam> UserTeams => Set<UserTeam>();
    public DbSet<UserPermissionOverride> UserPermissionOverrides => Set<UserPermissionOverride>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    public DbSet<CaseQuery> CaseQueries => Set<CaseQuery>();
    public DbSet<CaseQueryMessage> CaseQueryMessages => Set<CaseQueryMessage>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AiInteractionLog> AiInteractionLogs => Set<AiInteractionLog>();
    public DbSet<AiCaseInsight> AiCaseInsights => Set<AiCaseInsight>();
    public DbSet<CaseRequest> CaseRequests => Set<CaseRequest>();
    public DbSet<RequestAttachment> RequestAttachments => Set<RequestAttachment>();
    public DbSet<ApplicationForm> ApplicationForms => Set<ApplicationForm>();
    public DbSet<CaseDocument> CaseDocuments => Set<CaseDocument>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Account>(e =>
        {
            // ReferenceId is nvarchar(max) in the existing schema, so it is
            // not indexed here to avoid altering existing columns.
            e.Property(a => a.StatusBeforeHold).HasMaxLength(100);
        });

        builder.Entity<AppUser>(e =>
        {
            e.Property(u => u.FullName).HasMaxLength(150);
            e.Property(u => u.CseName).HasMaxLength(150);
        });

        builder.Entity<Team>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(100).IsRequired();
            e.Property(t => t.WorkspaceType).HasMaxLength(30).IsRequired();
            e.Property(t => t.Description).HasMaxLength(500);
            e.HasIndex(t => t.Name).IsUnique();
        });

        builder.Entity<AppRole>(e =>
        {
            e.ToTable("AppRoles");
            e.Property(r => r.Name).HasMaxLength(100).IsRequired();
            e.Property(r => r.Description).HasMaxLength(500);
            e.HasIndex(r => r.Name).IsUnique();
        });

        builder.Entity<Permission>(e =>
        {
            e.Property(p => p.Code).HasMaxLength(100).IsRequired();
            e.Property(p => p.Group).HasMaxLength(50);
            e.Property(p => p.Description).HasMaxLength(300);
            e.HasIndex(p => p.Code).IsUnique();
        });

        builder.Entity<RolePermission>(e =>
        {
            e.HasKey(rp => new { rp.RoleId, rp.PermissionId });
            e.HasOne(rp => rp.Role).WithMany(r => r.RolePermissions)
                .HasForeignKey(rp => rp.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(rp => rp.Permission).WithMany()
                .HasForeignKey(rp => rp.PermissionId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserRole>(e =>
        {
            e.ToTable("AppUserRoles");
            e.HasKey(ur => new { ur.UserId, ur.RoleId });
            e.HasOne(ur => ur.User).WithMany(u => u.UserRoles)
                .HasForeignKey(ur => ur.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(ur => ur.Role).WithMany(r => r.UserRoles)
                .HasForeignKey(ur => ur.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserTeam>(e =>
        {
            e.HasKey(ut => new { ut.UserId, ut.TeamId });
            e.HasOne(ut => ut.User).WithMany(u => u.UserTeams)
                .HasForeignKey(ut => ut.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(ut => ut.Team).WithMany(t => t.UserTeams)
                .HasForeignKey(ut => ut.TeamId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserPermissionOverride>(e =>
        {
            e.Property(o => o.Effect).HasMaxLength(10).IsRequired();
            e.HasIndex(o => new { o.UserId, o.PermissionId }).IsUnique();
            e.HasOne(o => o.User).WithMany(u => u.PermissionOverrides)
                .HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(o => o.Permission).WithMany()
                .HasForeignKey(o => o.PermissionId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SystemSetting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(100);
            e.Property(s => s.Value).HasMaxLength(500);
        });

        builder.Entity<CaseQuery>(e =>
        {
            e.Property(q => q.ReferenceId).HasMaxLength(100).IsRequired();
            e.Property(q => q.Stage).HasMaxLength(20);
            e.Property(q => q.QueryType).HasMaxLength(40);
            e.Property(q => q.Category).HasMaxLength(100);
            e.Property(q => q.FromTeam).HasMaxLength(30);
            e.Property(q => q.ToTeam).HasMaxLength(30);
            e.Property(q => q.Status).HasMaxLength(20);
            e.Property(q => q.RaisedByName).HasMaxLength(150);
            e.HasIndex(q => q.ReferenceId);
            e.HasIndex(q => new { q.ToTeam, q.Status });
            e.HasOne(q => q.Account).WithMany()
                .HasForeignKey(q => q.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CaseQueryMessage>(e =>
        {
            e.Property(m => m.AuthorName).HasMaxLength(150);
            e.Property(m => m.AuthorTeam).HasMaxLength(30);
            e.Property(m => m.MessageType).HasMaxLength(20);
            e.Property(m => m.Body).HasMaxLength(4000).IsRequired();
            e.HasOne(m => m.CaseQuery).WithMany(q => q.Messages)
                .HasForeignKey(m => m.CaseQueryId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AuditEvent>(e =>
        {
            e.Property(a => a.ReferenceId).HasMaxLength(100);
            e.Property(a => a.UserName).HasMaxLength(150);
            e.Property(a => a.Team).HasMaxLength(30);
            e.Property(a => a.Action).HasMaxLength(100);
            e.Property(a => a.FromStatus).HasMaxLength(100);
            e.Property(a => a.ToStatus).HasMaxLength(100);
            e.Property(a => a.Details).HasMaxLength(1000);
            e.HasIndex(a => a.ReferenceId);
            e.HasIndex(a => a.CreatedAt);
        });

        builder.Entity<AiInteractionLog>(e =>
        {
            e.Property(l => l.Feature).HasMaxLength(50);
            e.Property(l => l.ReferenceId).HasMaxLength(100);
            e.Property(l => l.Provider).HasMaxLength(30);
            e.Property(l => l.Model).HasMaxLength(100);
            e.Property(l => l.Error).HasMaxLength(500);
        });

        builder.Entity<AiCaseInsight>(e =>
        {
            e.Property(i => i.ReferenceId).HasMaxLength(100).IsRequired();
            e.Property(i => i.Kind).HasMaxLength(30);
            e.Property(i => i.Outcome).HasMaxLength(20);
            e.Property(i => i.Summary).HasMaxLength(2000);
            e.Property(i => i.FindingsJson).HasMaxLength(4000);
            e.Property(i => i.RecommendedAction).HasMaxLength(50);
            e.Property(i => i.RecommendedInputJson).HasMaxLength(4000);
            e.Property(i => i.Mode).HasMaxLength(20);
            e.Property(i => i.ActionStatus).HasMaxLength(20);
            e.Property(i => i.ActionResult).HasMaxLength(500);
            e.Property(i => i.AppliedByName).HasMaxLength(150);
            e.Property(i => i.Provider).HasMaxLength(30);
            e.Property(i => i.Model).HasMaxLength(100);
            e.HasIndex(i => i.ReferenceId);
        });

        builder.Entity<CaseRequest>(e =>
        {
            e.Property(r => r.CseUserName).HasMaxLength(150);
            e.Property(r => r.CseName).HasMaxLength(150);
            e.Property(r => r.Details).HasMaxLength(4000).IsRequired();
            e.Property(r => r.Status).HasMaxLength(20);
            e.Property(r => r.ReferenceId).HasMaxLength(100);
            e.Property(r => r.LoggedByName).HasMaxLength(150);
            e.HasIndex(r => r.Status);
            e.HasIndex(r => r.ReferenceId);
        });

        builder.Entity<RequestAttachment>(e =>
        {
            e.Property(a => a.FileName).HasMaxLength(255);
            e.Property(a => a.ContentType).HasMaxLength(100);
            e.HasOne(a => a.CaseRequest).WithMany(r => r.Attachments)
                .HasForeignKey(a => a.CaseRequestId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ApplicationForm>(e =>
        {
            e.Property(f => f.ReferenceId).HasMaxLength(100).IsRequired();
            e.Property(f => f.Status).HasMaxLength(20);
            e.Property(f => f.DataJson).IsRequired();
            e.Property(f => f.AiFilledJson).HasMaxLength(2000);
            e.Property(f => f.UpdatedByName).HasMaxLength(150);
            e.HasIndex(f => f.ReferenceId).IsUnique();
        });
        builder.Entity<CaseDocument>(e =>
        {
            e.Property(d => d.ReferenceId).HasMaxLength(100).IsRequired();
            e.Property(d => d.Purpose).HasMaxLength(150);
            e.Property(d => d.FileName).HasMaxLength(255);
            e.Property(d => d.ContentType).HasMaxLength(100);
            e.Property(d => d.UploadedByName).HasMaxLength(150);
            e.Property(d => d.Team).HasMaxLength(50);
            e.HasIndex(d => d.ReferenceId);
        });
    }
}
