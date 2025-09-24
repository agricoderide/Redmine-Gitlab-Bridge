using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bridge.Migrations
{
    /// <inheritdoc />
    public partial class addtrackertables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrackersRedmine",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RedmineTrackerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackersRedmine", x => x.Id);
                });

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackersRedmine");
        }
    }
}
