using FHIRBridge.Domain.Entities.Terminology;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FHIRBridge.Infrastructure.Persistence.Configurations.Terminology;

public sealed class TrmCodeSystemConfiguration : IEntityTypeConfiguration<TrmCodeSystem>
{
    public void Configure(EntityTypeBuilder<TrmCodeSystem> builder)
    {
        builder.ToTable("TRM_CODESYSTEM", "terminology");
        builder.HasKey(x => x.Pid);
        builder.Property(x => x.Pid).UseIdentityColumn();
        builder.Property(x => x.CodeSystemUri).HasMaxLength(200).IsRequired();
        builder.Property(x => x.CsName).HasMaxLength(200);
        builder.HasIndex(x => x.CodeSystemUri).IsUnique();
    }
}

public sealed class TrmCodeSystemVerConfiguration : IEntityTypeConfiguration<TrmCodeSystemVer>
{
    public void Configure(EntityTypeBuilder<TrmCodeSystemVer> builder)
    {
        builder.ToTable("TRM_CODESYSTEM_VER", "terminology");
        builder.HasKey(x => x.Pid);
        builder.Property(x => x.Pid).UseIdentityColumn();
        builder.Property(x => x.CsVersionId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.CsDisplay).HasMaxLength(200);
        builder.HasIndex(x => new { x.CodeSystemPid, x.CsVersionId }).IsUnique();
    }
}

public sealed class TrmConceptConfiguration : IEntityTypeConfiguration<TrmConcept>
{
    public void Configure(EntityTypeBuilder<TrmConcept> builder)
    {
        builder.ToTable("TRM_CONCEPT", "terminology");
        builder.HasKey(x => x.Pid);
        builder.Property(x => x.Pid).UseIdentityColumn();
        // Case-sensitive collation: several code systems have genuinely distinct codes that differ
        // only by case (confirmed via a real unique-index violation — UCUM's "S" (Siemens) and "s"
        // (second) collided under the database's default case-insensitive collation even though they
        // are different, valid concepts). Without this, the unique index below would silently treat
        // case-variant codes as duplicates.
        builder.Property(x => x.CodeVal).HasMaxLength(500).IsRequired().UseCollation("SQL_Latin1_General_CP1_CS_AS");
        // Unbounded (nvarchar(max)), unlike HAPI's own trm_concept.display (varchar(400)) — HCPCS
        // descriptions are built by concatenating multiple word-wrapped source-file chunks and can
        // legitimately run well past 2000 characters (confirmed via repeated real truncation failures
        // on genuine HCPCS descriptions during "Run Now"). Display is never filtered/joined/indexed on,
        // so there's no cost to leaving it unbounded rather than guessing another fixed ceiling.
        builder.Property(x => x.Display).HasColumnType("nvarchar(max)");
        builder.HasIndex(x => new { x.CodeSystemPid, x.CodeVal }).IsUnique();
        // A standalone (non-unique) index on CodeVal alone, for HapiLocalTerminologyLookupService's
        // cross-system search (CodeableConceptBuilder's opt-in auto-detect) — the composite index above
        // is sorted by CodeSystemPid first, so it can't serve a "which system(s) have this code,
        // regardless of which one" query efficiently across ~2M+ rows.
        builder.HasIndex(x => x.CodeVal);
    }
}
