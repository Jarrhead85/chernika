using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHKCardRowVersionAndActiveUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HKCards_NodeId",
                table: "HKCards");

            migrationBuilder.CreateIndex(
                name: "UX_HKCards_OneActivePerNode",
                table: "HKCards",
                column: "NodeId",
                unique: true,
                filter: "\"Status\" IN ('Draft', 'OnReview', 'RevisionRequired')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_HKCards_OneActivePerNode",
                table: "HKCards");

            migrationBuilder.CreateIndex(
                name: "IX_HKCards_NodeId",
                table: "HKCards",
                column: "NodeId");
        }
    }
}
