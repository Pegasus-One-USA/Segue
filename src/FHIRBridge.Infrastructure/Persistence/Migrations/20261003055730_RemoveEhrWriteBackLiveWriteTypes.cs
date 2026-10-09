using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveEhrWriteBackLiveWriteTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The EhrWriteBack:LiveWriteTypes release list is retired: going live is now a destination's own choice
            // (Dry run unticked), controlled by the EHR Write-Back permissions. The row is deleted outright, not
            // soft-deleted, so nothing is left in System Settings or the table. Only EhrWriteBack:CloneModeEnabled
            // remains.
            migrationBuilder.Sql("DELETE FROM [SystemSettings] WHERE [Key] = N'EhrWriteBack:LiveWriteTypes';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore: the code that read the setting is gone, and its value was install-specific.
        }
    }
}
