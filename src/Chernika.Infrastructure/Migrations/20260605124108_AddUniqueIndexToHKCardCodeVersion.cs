using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueIndexToHKCardCodeVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HKCards_Code",
                table: "HKCards");

            migrationBuilder.CreateIndex(
                name: "IX_HKCards_Code_Version",
                table: "HKCards",
                columns: new[] { "Code", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HKCards_Code_Version",
                table: "HKCards");

            migrationBuilder.CreateIndex(
                name: "IX_HKCards_Code",
                table: "HKCards",
                column: "Code");
        }
    }
}
