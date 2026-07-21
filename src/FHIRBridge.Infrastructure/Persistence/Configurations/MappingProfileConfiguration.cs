using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations;

public sealed class MappingProfileConfiguration : IEntityTypeConfiguration<MappingProfile>
{
    public void Configure(EntityTypeBuilder<MappingProfile> builder)
    {
        builder.ToTable("MappingProfiles");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ResourceType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.SourceConnectionId).IsRequired();
        builder.Property(x => x.DestinationId).IsRequired();
        builder.Property(x => x.DestinationObject).HasMaxLength(300).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();

        builder.HasIndex(x => x.SourceConnectionId);
        builder.HasIndex(x => x.DestinationId);

        builder.HasOne<SourceConnection>()
            .WithMany()
            .HasForeignKey(x => x.SourceConnectionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.OwnsMany(x => x.Fields, field =>
        {
            field.ToTable("MappingFields");
            field.WithOwner().HasForeignKey("MappingProfileId");
            field.Property<Guid>("Id");
            field.HasKey("Id");
            field.Property(x => x.TargetField).HasMaxLength(200).IsRequired();
            field.Property(x => x.JsonPath).HasMaxLength(500).IsRequired();
            field.Property(x => x.ValueType).HasConversion<string>().HasMaxLength(50).IsRequired();
            field.Property(x => x.IsRequired).IsRequired();
            field.Property(x => x.DefaultValue).HasMaxLength(1000);
            field.Property(x => x.Format).HasMaxLength(100);
            field.Property(x => x.ResourceType).HasMaxLength(100);
            field.Property(x => x.DestinationObject).HasMaxLength(300);
            field.Property(x => x.NormalizationType).HasMaxLength(100);
            field.Property(x => x.TerminologySystemJsonPath).HasMaxLength(500);
            field.Property(x => x.TerminologyCodeJsonPath).HasMaxLength(500);
            field.Property(x => x.IsEnabled).IsRequired();
            field.Property(x => x.ArrayPolicy).HasConversion<string>().HasMaxLength(50).IsRequired().HasDefaultValue(Domain.Enums.ArrayPolicy.Scalar);
            field.Property(x => x.Cardinality).HasMaxLength(100);
            field.Property(x => x.ArrayAncestors).HasMaxLength(2000);
            field.Property(x => x.IsUpsertKey).IsRequired().HasDefaultValue(false);
        });

        builder.Navigation(x => x.Fields)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
