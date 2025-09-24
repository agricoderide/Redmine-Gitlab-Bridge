using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bridge.Migrations
{
    /// <inheritdoc />
    public partial class addstatusestable23 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSyncedUtc",
                table: "IssueMappings");

            migrationBuilder.RenameColumn(
                name: "Fingerprint",
                table: "IssueMappings",
                newName: "LastGitLabEventUuid");

            migrationBuilder.AddColumn<string>(
                name: "CanonicalSnapshotJson",
                table: "IssueMappings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanonicalSnapshotJson",
                table: "IssueMappings");

            migrationBuilder.RenameColumn(
                name: "LastGitLabEventUuid",
                table: "IssueMappings",
                newName: "Fingerprint");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSyncedUtc",
                table: "IssueMappings",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));
        }
    }
}
