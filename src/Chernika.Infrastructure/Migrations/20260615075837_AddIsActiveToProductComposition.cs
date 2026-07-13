using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIsActiveToProductComposition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProductCompositions_EquipmentModelId",
                table: "ProductCompositions");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "ProductCompositions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(@"
                UPDATE ""ProductCompositions"" SET ""IsActive"" = true
                WHERE ""Id"" IN (
                    SELECT DISTINCT ON (""EquipmentModelId"") ""Id""
                    FROM ""ProductCompositions""
                    ORDER BY ""EquipmentModelId"", ""CreatedAt"" DESC
                )");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositions_EquipmentModelId_IsActive",
                table: "ProductCompositions",
                columns: new[] { "EquipmentModelId", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProductCompositions_EquipmentModelId_IsActive",
                table: "ProductCompositions");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "ProductCompositions");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositions_EquipmentModelId",
                table: "ProductCompositions",
                column: "EquipmentModelId");
        }
    }
}
