using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bridge.Migrations
{
    /// <inheritdoc />
    public partial class Init_OneToOne : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "GitLabProjectId",
                table: "GitLabProjects",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_RedmineProjectId",
                table: "Projects",
                column: "RedmineProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueMappings_GitLabIssueId",
                table: "IssueMappings",
                column: "GitLabIssueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueMappings_RedmineIssueId",
                table: "IssueMappings",
                column: "RedmineIssueId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Projects_RedmineProjectId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_IssueMappings_GitLabIssueId",
                table: "IssueMappings");

            migrationBuilder.DropIndex(
                name: "IX_IssueMappings_RedmineIssueId",
                table: "IssueMappings");

            migrationBuilder.AlterColumn<long>(
                name: "GitLabProjectId",
                table: "GitLabProjects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
