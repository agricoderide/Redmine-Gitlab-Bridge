using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bridge.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackersAndUserSyncs_AndCompositeIssueMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Trackers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RedmineTrackerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trackers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserSyncs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RedmineUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    RedmineLogin = table.Column<string>(type: "TEXT", nullable: false),
                    GitLabUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    GitLabUsername = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSyncs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Trackers_Name",
                table: "Trackers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trackers_RedmineTrackerId",
                table: "Trackers",
                column: "RedmineTrackerId",
                unique: true);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Trackers");

            migrationBuilder.DropTable(
                name: "UserSyncs");
        }
    }
}
