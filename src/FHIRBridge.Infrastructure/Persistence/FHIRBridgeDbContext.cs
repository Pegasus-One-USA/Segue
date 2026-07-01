using FHIRBridge.Domain.Aggregates;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Messaging;
using FHIRBridge.SharedKernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class FHIRBridgeDbContext : DbContext
{
    public FHIRBridgeDbContext(DbContextOptions<FHIRBridgeDbContext> options)
        : base(options)
    {
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<SourceConnection> SourceConnections => Set<SourceConnection>();
    public DbSet<WebhookConfiguration> WebhookConfigurations => Set<WebhookConfiguration>();
    public DbSet<DestinationConfiguration> DestinationConfigurations => Set<DestinationConfiguration>();
    public DbSet<MappingProfile> MappingProfiles => Set<MappingProfile>();
    public DbSet<ResourcePipelineRoute> ResourcePipelineRoutes => Set<ResourcePipelineRoute>();
    public DbSet<SourceCapabilityProfile> SourceCapabilityProfiles => Set<SourceCapabilityProfile>();
    public DbSet<ConfiguredPipelineRunRecord> ConfiguredPipelineRuns => Set<ConfiguredPipelineRunRecord>();
    public DbSet<OperationalAuditLog> OperationalAuditLogs => Set<OperationalAuditLog>();
    public DbSet<ResourceLineageEntry> ResourceLineageEntries => Set<ResourceLineageEntry>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<UserActivityAuditLog> UserActivityAuditLogs => Set<UserActivityAuditLog>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<TenantUser> TenantUsers => Set<TenantUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FHIRBridgeDbContext).Assembly);

        // Cross-cutting conventions applied after the per-entity configurations:
        //  • soft-deletable entities get a global "hide deleted rows" query filter
        //  • every entity carrying a RowVersion gets it mapped as an optimistic-concurrency token
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (typeof(ISoftDeletable).IsAssignableFrom(clrType))
            {
                SoftDeleteFilterMethod
                    .MakeGenericMethod(clrType)
                    .Invoke(null, [modelBuilder]);
            }

            if (!entityType.IsOwned() && entityType.FindProperty(nameof(AuditableEntity<int>.RowVersion)) is not null)
            {
                modelBuilder.Entity(clrType)
                    .Property(nameof(AuditableEntity<int>.RowVersion))
                    .IsRowVersion();
            }
        }
    }

    private static readonly System.Reflection.MethodInfo SoftDeleteFilterMethod =
        typeof(FHIRBridgeDbContext).GetMethod(
            nameof(ApplySoftDeleteFilter),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static void ApplySoftDeleteFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ISoftDeletable
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(e => !e.IsDeleted);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAppendOnlyLogs();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        GuardAppendOnlyLogs();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Enforces the HIPAA append-only retention requirement: audit rows (operational + user-activity) may be
    /// inserted but never updated or deleted through the application. Attempts to do so fail fast rather than
    /// silently mutating history.
    /// </summary>
    private void GuardAppendOnlyLogs()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is OperationalAuditLog or UserActivityAuditLog &&
                entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    "Audit logs are append-only and cannot be modified or deleted (HIPAA retention requirement).");
            }
        }
    }
}
