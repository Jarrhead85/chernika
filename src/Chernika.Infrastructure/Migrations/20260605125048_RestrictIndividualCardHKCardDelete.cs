using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestrictIndividualCardHKCardDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_IndividualCards_HKCards_HKCardId",
                table: "IndividualCards");

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualCards_HKCards_HKCardId",
                table: "IndividualCards",
                column: "HKCardId",
                principalTable: "HKCards",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_IndividualCards_HKCards_HKCardId",
                table: "IndividualCards");

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualCards_HKCards_HKCardId",
                table: "IndividualCards",
                column: "HKCardId",
                principalTable: "HKCards",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
