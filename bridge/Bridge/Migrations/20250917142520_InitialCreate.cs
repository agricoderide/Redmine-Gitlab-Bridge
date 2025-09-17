using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bridge.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RedmineProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    RedmineIdentifier = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    LastSyncUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitLabProjects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GitLabProjectId = table.Column<long>(type: "INTEGER", nullable: false),
                    PathWithNamespace = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectSyncId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitLabProjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitLabProjects_Projects_ProjectSyncId",
                        column: x => x.ProjectSyncId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IssueMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RedmineIssueId = table.Column<int>(type: "INTEGER", nullable: false),
                    GitLabIssueId = table.Column<long>(type: "INTEGER", nullable: false),
                    ProjectSyncId = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssueMappings_Projects_ProjectSyncId",
                        column: x => x.ProjectSyncId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GitLabProjects_ProjectSyncId",
                table: "GitLabProjects",
                column: "ProjectSyncId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueMappings_ProjectSyncId",
                table: "IssueMappings",
                column: "ProjectSyncId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GitLabProjects");

            migrationBuilder.DropTable(
                name: "IssueMappings");

            migrationBuilder.DropTable(
                name: "Projects");
        }
    }
}
