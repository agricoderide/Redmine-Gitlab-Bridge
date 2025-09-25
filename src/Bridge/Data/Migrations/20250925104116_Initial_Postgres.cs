using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Bridge.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial_Postgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RedmineProjectId = table.Column<int>(type: "integer", nullable: false),
                    RedmineIdentifier = table.Column<string>(type: "text", nullable: false),
                    LastSyncUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StatusesRedmine",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RedmineStatusId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusesRedmine", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrackersRedmine",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RedmineTrackerId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackersRedmine", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RedmineUserId = table.Column<int>(type: "integer", nullable: true),
                    GitLabUserId = table.Column<int>(type: "integer", nullable: true),
                    Username = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitLabProjects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GitLabProjectId = table.Column<long>(type: "bigint", nullable: true),
                    PathWithNamespace = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    ProjectSyncId = table.Column<int>(type: "integer", nullable: false)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RedmineIssueId = table.Column<int>(type: "integer", nullable: false),
                    GitLabIssueId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectSyncId = table.Column<int>(type: "integer", nullable: false),
                    CanonicalSnapshot = table.Column<string>(type: "text", nullable: true),
                    LastGitLabEventUuid = table.Column<string>(type: "text", nullable: true)
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
                name: "IX_IssueMappings_GitLabIssueId",
                table: "IssueMappings",
                column: "GitLabIssueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueMappings_ProjectSyncId",
                table: "IssueMappings",
                column: "ProjectSyncId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueMappings_RedmineIssueId",
                table: "IssueMappings",
                column: "RedmineIssueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_RedmineProjectId",
                table: "Projects",
                column: "RedmineProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusesRedmine_Name",
                table: "StatusesRedmine",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusesRedmine_RedmineStatusId",
                table: "StatusesRedmine",
                column: "RedmineStatusId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrackersRedmine_Name",
                table: "TrackersRedmine",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrackersRedmine_RedmineTrackerId",
                table: "TrackersRedmine",
                column: "RedmineTrackerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_GitLabUserId",
                table: "Users",
                column: "GitLabUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_RedmineUserId",
                table: "Users",
                column: "RedmineUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GitLabProjects");

            migrationBuilder.DropTable(
                name: "IssueMappings");

            migrationBuilder.DropTable(
                name: "StatusesRedmine");

            migrationBuilder.DropTable(
                name: "TrackersRedmine");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Projects");
        }
    }
}
