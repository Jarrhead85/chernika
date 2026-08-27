using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class A4ProductCompositionAggregatesCollection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProductCompositionAggregates_PartId",
                table: "ProductCompositionAggregates");

            migrationBuilder.DropIndex(
                name: "IX_ProductCompositionAggregates_ProductCompositionId_Aggregate~",
                table: "ProductCompositionAggregates");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionAggregates_PartId_AggregateId",
                table: "ProductCompositionAggregates",
                columns: new[] { "PartId", "AggregateId" },
                unique: true,
                filter: "\"PartId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionAggregates_ProductCompositionId_AggregateId_NoPart",
                table: "ProductCompositionAggregates",
                columns: new[] { "ProductCompositionId", "AggregateId" },
                unique: true,
                filter: "\"PartId\" IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductCompositionAggregates_Quantity",
                table: "ProductCompositionAggregates",
                sql: "\"Quantity\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProductCompositionAggregates_PartId_AggregateId",
                table: "ProductCompositionAggregates");

            migrationBuilder.DropIndex(
                name: "IX_ProductCompositionAggregates_ProductCompositionId_AggregateId_NoPart",
                table: "ProductCompositionAggregates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProductCompositionAggregates_Quantity",
                table: "ProductCompositionAggregates");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionAggregates_PartId",
                table: "ProductCompositionAggregates",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionAggregates_ProductCompositionId_Aggregate~",
                table: "ProductCompositionAggregates",
                columns: new[] { "ProductCompositionId", "AggregateId" },
                unique: true);
        }
    }
}
