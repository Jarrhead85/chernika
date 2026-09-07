using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class D5VersionAndArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IndividualCards_SupersedesIndividualCardId",
                table: "IndividualCards");

            migrationBuilder.CreateIndex(
                name: "UX_IndividualCards_SupersedesIndividualCardId",
                table: "IndividualCards",
                column: "SupersedesIndividualCardId",
                unique: true,
                filter: "\"SupersedesIndividualCardId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_IndividualCards_SupersedesIndividualCardId",
                table: "IndividualCards");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCards_SupersedesIndividualCardId",
                table: "IndividualCards",
                column: "SupersedesIndividualCardId");
        }
    }
}
