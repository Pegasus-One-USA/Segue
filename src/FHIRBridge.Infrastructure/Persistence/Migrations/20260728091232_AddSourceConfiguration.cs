using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceConfigurationId",
                table: "MappingProfiles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SourceConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RetrievalMethod = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RetrievalResourceTypes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RetrievalSearchCriteria = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RetrievalIncrementalSyncEnabled = table.Column<bool>(type: "bit", nullable: true),
                    RetrievalPageSize = table.Column<int>(type: "int", nullable: true),
                    RetrievalSortOrder = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RetrievalIncludeParameters = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RetrievalRevIncludeParameters = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RetrievalRetryPolicy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RetrievalTimeoutSeconds = table.Column<int>(type: "int", nullable: true),
                    RetrievalMaxRecordsPerRun = table.Column<int>(type: "int", nullable: true),
                    RetrievalExportScope = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RetrievalGroupId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RetrievalPatientIds = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RetrievalOutputFormat = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RetrievalLastSuccessfulSyncUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedOnUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceConfigurations_SourceConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "SourceConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Slice 1 backfill (docs/backend/13-source-connection-configuration-split-plan.md): every existing
            // SourceConnection gets exactly one SourceConfiguration carrying over its Retrieval/Scopes verbatim, and
            // every MappingProfile pointing at that connection is repointed at the new configuration. Nothing reads
            // SourceConfigurationId or the new table yet, so this is a pure data copy with no behavior change.
            migrationBuilder.Sql(@"
                INSERT INTO [SourceConfigurations]
                    ([Id], [ConnectionId], [Name], [Scopes],
                     [RetrievalMethod], [RetrievalResourceTypes], [RetrievalSearchCriteria], [RetrievalIncrementalSyncEnabled],
                     [RetrievalPageSize], [RetrievalSortOrder], [RetrievalIncludeParameters], [RetrievalRevIncludeParameters],
                     [RetrievalRetryPolicy], [RetrievalTimeoutSeconds], [RetrievalMaxRecordsPerRun],
                     [RetrievalExportScope], [RetrievalGroupId], [RetrievalPatientIds], [RetrievalOutputFormat],
                     [RetrievalLastSuccessfulSyncUtc],
                     [CreatedOnUtc], [CreatedBy], [ModifiedOnUtc], [ModifiedBy], [IsDeleted], [DeletedOnUtc], [DeletedBy])
                SELECT
                    NEWID(), sc.[Id], sc.[Name], sc.[Scopes],
                    sc.[RetrievalMethod], sc.[RetrievalResourceTypes], sc.[RetrievalSearchCriteria], sc.[RetrievalIncrementalSyncEnabled],
                    sc.[RetrievalPageSize], sc.[RetrievalSortOrder], sc.[RetrievalIncludeParameters], sc.[RetrievalRevIncludeParameters],
                    sc.[RetrievalRetryPolicy], sc.[RetrievalTimeoutSeconds], sc.[RetrievalMaxRecordsPerRun],
                    sc.[RetrievalExportScope], sc.[RetrievalGroupId], sc.[RetrievalPatientIds], sc.[RetrievalOutputFormat],
                    sc.[RetrievalLastSuccessfulSyncUtc],
                    sc.[CreatedOnUtc], sc.[CreatedBy], sc.[ModifiedOnUtc], sc.[ModifiedBy], sc.[IsDeleted], sc.[DeletedOnUtc], sc.[DeletedBy]
                FROM [SourceConnections] sc;

                UPDATE mp
                SET mp.[SourceConfigurationId] = cfg.[Id]
                FROM [MappingProfiles] mp
                INNER JOIN [SourceConfigurations] cfg ON cfg.[ConnectionId] = mp.[SourceConnectionId];
            ");

            migrationBuilder.CreateIndex(
                name: "IX_MappingProfiles_SourceConfigurationId",
                table: "MappingProfiles",
                column: "SourceConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceConfigurations_ConnectionId",
                table: "SourceConfigurations",
                column: "ConnectionId");

            migrationBuilder.AddForeignKey(
                name: "FK_MappingProfiles_SourceConfigurations_SourceConfigurationId",
                table: "MappingProfiles",
                column: "SourceConfigurationId",
                principalTable: "SourceConfigurations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MappingProfiles_SourceConfigurations_SourceConfigurationId",
                table: "MappingProfiles");

            migrationBuilder.DropTable(
                name: "SourceConfigurations");

            migrationBuilder.DropIndex(
                name: "IX_MappingProfiles_SourceConfigurationId",
                table: "MappingProfiles");

            migrationBuilder.DropColumn(
                name: "SourceConfigurationId",
                table: "MappingProfiles");
        }
    }
}
