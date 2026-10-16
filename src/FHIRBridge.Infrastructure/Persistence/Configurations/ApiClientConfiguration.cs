using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class ApiClientConfiguration : IEntityTypeConfiguration<ApiClient>
{
    public void Configure(EntityTypeBuilder<ApiClient> builder)
    {
        builder.ToTable("ApiClients");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ClientId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ClientSecretHash).HasMaxLength(500).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.LastUsedOnUtc);

        builder.HasIndex(x => x.ClientId).IsUnique().HasFilter("[IsDeleted] = 0");

        builder.HasMany(x => x.ReturnUrls)
            .WithOne()
            .HasForeignKey(x => x.ApiClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(ApiClient.ReturnUrls))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class ApiClientReturnUrlConfiguration : IEntityTypeConfiguration<ApiClientReturnUrl>
{
    public void Configure(EntityTypeBuilder<ApiClientReturnUrl> builder)
    {
        builder.ToTable("ApiClientReturnUrls");
        builder.HasKey(x => x.Id);

        // The Id is set client-side in the constructor (Guid.NewGuid()), never by the database. Without this,
        // EF's default "Guid key = database-generated" convention makes it treat a new instance discovered only
        // via ApiClient.ReturnUrls navigation fixup (never an explicit context.Add()) as an existing row being
        // Modified rather than a new one being Added — it then emits an UPDATE for a row that was never
        // inserted, which affects 0 rows and throws DbUpdateConcurrencyException.
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.ApiClientId).IsRequired();
        builder.Property(x => x.Url).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.Label).HasMaxLength(200);
        builder.Property(x => x.MatchMode).HasConversion<int>().IsRequired();

        builder.HasIndex(x => x.ApiClientId);
    }
}
