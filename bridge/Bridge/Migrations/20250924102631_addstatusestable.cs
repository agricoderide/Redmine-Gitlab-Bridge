using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bridge.Migrations
{
    /// <inheritdoc />
    public partial class addstatusestable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StatusesRedmine",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RedmineStatusId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusesRedmine", x => x.Id);
                });

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StatusesRedmine");
        }
    }
}
