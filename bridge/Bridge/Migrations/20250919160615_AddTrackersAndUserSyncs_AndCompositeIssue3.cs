using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bridge.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackersAndUserSyncs_AndCompositeIssue3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserSyncs");

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RedmineUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    GitLabUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    RedmineLogin = table.Column<string>(type: "TEXT", nullable: true),
                    GitLabUsername = table.Column<string>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectMemberships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectSyncId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectMemberships_Projects_ProjectSyncId",
                        column: x => x.ProjectSyncId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMemberships_ProjectSyncId_UserId_Source",
                table: "ProjectMemberships",
                columns: new[] { "ProjectSyncId", "UserId", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMemberships_UserId",
                table: "ProjectMemberships",
                column: "UserId");

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
                name: "ProjectMemberships");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.CreateTable(
                name: "UserSyncs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GitLabUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    GitLabUsername = table.Column<string>(type: "TEXT", nullable: false),
                    RedmineLogin = table.Column<string>(type: "TEXT", nullable: false),
                    RedmineUserId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSyncs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSyncs_GitLabUserId",
                table: "UserSyncs",
                column: "GitLabUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSyncs_GitLabUsername",
                table: "UserSyncs",
                column: "GitLabUsername");

            migrationBuilder.CreateIndex(
                name: "IX_UserSyncs_RedmineLogin",
                table: "UserSyncs",
                column: "RedmineLogin",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSyncs_RedmineUserId",
                table: "UserSyncs",
                column: "RedmineUserId",
                unique: true);
        }
    }
}
