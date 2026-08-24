using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FHIRBridge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshTokenRememberMe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: the auto-generated diff also proposed re-adding MagicLinkTokenExpiresOnUtc/
            // MagicLinkTokenHash — those already exist on Users (added by the already-applied
            // 20260818113324_AddSamlAndMagicLinkAuth migration; confirmed directly against the live
            // database's __EFMigrationsHistory and INFORMATION_SCHEMA.COLUMNS). The model snapshot
            // this project's migrations were tracking had simply drifted out of sync with that
            // already-applied migration — a pre-existing inconsistency, unrelated to Remember Me.
            // Re-adding them here would fail against any database that already has
            // AddSamlAndMagicLinkAuth applied, so this migration is trimmed to its own actual delta.
            migrationBuilder.AddColumn<bool>(
                name: "RefreshTokenRememberMe",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RefreshTokenRememberMe",
                table: "Users");
        }
    }
}
