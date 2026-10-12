using IisControlCenter.Contracts;
using IisControlCenter.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace IisControlCenter.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<EnvironmentDef> Environments => Set<EnvironmentDef>();
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<RegistrationToken> RegistrationTokens => Set<RegistrationToken>();
    public DbSet<AgentCertificate> AgentCertificates => Set<AgentCertificate>();
    public DbSet<IisSite> IisSites => Set<IisSite>();
    public DbSet<IisBinding> IisBindings => Set<IisBinding>();
    public DbSet<IisApplication> IisApplications => Set<IisApplication>();
    public DbSet<IisVirtualDir> IisVirtualDirs => Set<IisVirtualDir>();
    public DbSet<IisAppPool> IisAppPools => Set<IisAppPool>();
    public DbSet<IisCertificate> IisCertificates => Set<IisCertificate>();
    public DbSet<ServerDotNetVersion> ServerDotNetVersions => Set<ServerDotNetVersion>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Component> Components => Set<Component>();
    public DbSet<DeploymentTarget> DeploymentTargets => Set<DeploymentTarget>();
    public DbSet<ProtectedPath> ProtectedPaths => Set<ProtectedPath>();
    public DbSet<ProjectEnvironmentSetting> ProjectEnvironmentSettings => Set<ProjectEnvironmentSetting>();
    public DbSet<LinkException> LinkExceptions => Set<LinkException>();
    public DbSet<LinkRequest> LinkRequests => Set<LinkRequest>();
    public DbSet<DriftEvent> DriftEvents => Set<DriftEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            // SQLite cannot compare or sort DateTimeOffset. Store as UTC ticks (tests and local smoke runs only).
            var conv = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
                v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
            var convN = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?>(
                v => v == null ? null : v.Value.UtcTicks, v => v == null ? null : new DateTimeOffset(v.Value, TimeSpan.Zero));
            foreach (var et in b.Model.GetEntityTypes())
                foreach (var pr in et.GetProperties())
                {
                    if (pr.ClrType == typeof(DateTimeOffset)) pr.SetValueConverter(conv);
                    else if (pr.ClrType == typeof(DateTimeOffset?)) pr.SetValueConverter(convN);
                }
        }

        b.Entity<EnvironmentDef>(e =>
        {
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(50);
            e.HasData(
                new EnvironmentDef { Name = EnvironmentNames.Dev, IsProduction = false, SortOrder = 1 },
                new EnvironmentDef { Name = EnvironmentNames.Staging, IsProduction = false, SortOrder = 2 },
                new EnvironmentDef { Name = EnvironmentNames.Prod, IsProduction = true, SortOrder = 3 });
        });

        b.Entity<Server>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Hostname).HasMaxLength(255);
            e.Property(x => x.EnvironmentName).HasMaxLength(50);
            e.Property(x => x.DeclaredEnvironment).HasMaxLength(50);
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.AgentStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.AgentVersion).HasMaxLength(50);
            e.Property(x => x.OsVersion).HasMaxLength(200);
            e.Property(x => x.AllowedRoots).HasMaxLength(2000);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.Property(x => x.ApprovedBy).HasMaxLength(200);
            e.HasOne<EnvironmentDef>().WithMany().HasForeignKey(x => x.EnvironmentName).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => !x.IsDeleted);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("[IsDeleted] = 0");
        });

        b.Entity<RegistrationToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne<Server>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AgentCertificate>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Thumbprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RevokeReason).HasMaxLength(500);
            e.HasIndex(x => x.Thumbprint).IsUnique();
            e.HasOne<Server>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<IisSite>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.State).HasMaxLength(30);
            e.Property(x => x.PhysicalPath).HasMaxLength(1000);
            e.Property(x => x.AppPoolName).HasMaxLength(300);
            e.Property(x => x.MappingStatus).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.ServerId, x.IisSiteId }).IsUnique();
            e.HasOne<Server>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Bindings).WithOne().HasForeignKey(x => x.IisSiteRowId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Applications).WithOne().HasForeignKey(x => x.IisSiteRowId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<IisBinding>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Protocol).HasMaxLength(20);
            e.Property(x => x.IpAddress).HasMaxLength(100);
            e.Property(x => x.Host).HasMaxLength(500);
            e.Property(x => x.CertThumbprint).HasMaxLength(64);
        });
        b.Entity<IisApplication>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Path).HasMaxLength(500);
            e.Property(x => x.PhysicalPath).HasMaxLength(1000);
            e.Property(x => x.AppPoolName).HasMaxLength(300);
            e.HasMany(x => x.VirtualDirs).WithOne().HasForeignKey(x => x.IisApplicationRowId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<IisVirtualDir>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Path).HasMaxLength(500);
            e.Property(x => x.PhysicalPath).HasMaxLength(1000);
        });
        b.Entity<IisAppPool>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.IdentityType).HasMaxLength(50);
            e.Property(x => x.RuntimeVersion).HasMaxLength(30);
            e.Property(x => x.PipelineMode).HasMaxLength(30);
            e.Property(x => x.State).HasMaxLength(30);
            e.HasIndex(x => new { x.ServerId, x.Name }).IsUnique();
            e.HasOne<Server>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<IisCertificate>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Thumbprint).HasMaxLength(64);
            e.Property(x => x.Subject).HasMaxLength(1000);
            e.Property(x => x.Store).HasMaxLength(100);
            e.HasOne<Server>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ServerDotNetVersion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(50);
            e.Property(x => x.Version).HasMaxLength(50);
            e.HasOne<Server>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RoleAssignment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PrincipalType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PrincipalId).HasMaxLength(100).IsRequired();
            e.Property(x => x.PrincipalName).HasMaxLength(300);
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.EnvironmentName).HasMaxLength(50);
            e.Property(x => x.GrantedBy).HasMaxLength(200);
            e.Property(x => x.RevokedBy).HasMaxLength(200);
            e.HasIndex(x => x.PrincipalId);
            e.HasOne<EnvironmentDef>().WithMany().HasForeignKey(x => x.EnvironmentName).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditLog");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.UserId).HasMaxLength(100);
            e.Property(x => x.UserName).HasMaxLength(300);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.TargetType).HasMaxLength(50);
            e.Property(x => x.TargetId).HasMaxLength(100);
            e.Property(x => x.EnvironmentName).HasMaxLength(50);
            e.Property(x => x.Result).HasMaxLength(30);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.Property(x => x.ClientIp).HasMaxLength(64);
            e.Property(x => x.PrevHash).HasMaxLength(64);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.Action);
            e.HasIndex(x => x.ServerId);
        });

        b.Entity<Project>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.OwnerGroupId).HasMaxLength(100);
            e.Property(x => x.OwnerGroupName).HasMaxLength(300);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasMany(x => x.Components).WithOne().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Component>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(100).IsRequired();
            e.Property(x => x.Type).HasMaxLength(30);
            e.Property(x => x.HealthCheckPath).HasMaxLength(500);
            e.Property(x => x.AllowedFileTypes).HasMaxLength(200);
            e.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
            e.HasMany(x => x.Targets).WithOne().HasForeignKey(x => x.ComponentId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.ProtectedPaths).WithOne().HasForeignKey(x => x.ComponentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeploymentTarget>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EnvironmentName).HasMaxLength(50).IsRequired();
            e.Property(x => x.IisSiteName).HasMaxLength(300);
            e.Property(x => x.AppPath).HasMaxLength(500);
            e.Property(x => x.PhysicalPath).HasMaxLength(1000);
            e.Property(x => x.AppPoolName).HasMaxLength(300);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.StatusDetail).HasMaxLength(1000);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            // An IIS site/application can belong to only one target.
            e.HasIndex(x => new { x.ServerId, x.IisSiteId, x.AppPath }).IsUnique();
            // One target per component, environment and server.
            e.HasIndex(x => new { x.ComponentId, x.EnvironmentName, x.ServerId }).IsUnique();
            e.HasOne<Server>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<EnvironmentDef>().WithMany().HasForeignKey(x => x.EnvironmentName).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LinkException>().WithMany().HasForeignKey(x => x.LinkExceptionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProtectedPath>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Path).HasMaxLength(500).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<ProjectEnvironmentSetting>(e =>
        {
            e.HasKey(x => new { x.ProjectId, x.EnvironmentName });
            e.Property(x => x.EnvironmentName).HasMaxLength(50);
            e.Property(x => x.UpdatedBy).HasMaxLength(200);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<EnvironmentDef>().WithMany().HasForeignKey(x => x.EnvironmentName).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<LinkException>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.Violation).HasMaxLength(1000);
            e.Property(x => x.GrantedBy).HasMaxLength(200);
        });

        b.Entity<LinkRequest>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EnvironmentName).HasMaxLength(50);
            e.Property(x => x.AppPath).HasMaxLength(500);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RequestedBy).HasMaxLength(300);
            e.Property(x => x.RequestedById).HasMaxLength(100);
            e.Property(x => x.DecidedBy).HasMaxLength(300);
            e.Property(x => x.DecisionComment).HasMaxLength(1000);
            e.HasOne<Component>().WithMany().HasForeignKey(x => x.ComponentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Server>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<DriftEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ObjectType).HasMaxLength(30);
            e.Property(x => x.ObjectRef).HasMaxLength(500);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Before).HasMaxLength(2000);
            e.Property(x => x.After).HasMaxLength(2000);
            e.Property(x => x.AcknowledgedBy).HasMaxLength(300);
            e.HasIndex(x => new { x.ServerId, x.DetectedAt });
            e.HasOne<Server>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Setting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Value).HasMaxLength(2000);
        });
    }
}
