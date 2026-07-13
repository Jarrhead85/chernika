using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductCompositionParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProductCompositionNodes_ProductCompositions_ProductComposit~",
                table: "ProductCompositionNodes");

            migrationBuilder.RenameColumn(
                name: "ProductCompositionId",
                table: "ProductCompositionNodes",
                newName: "PartId");

            migrationBuilder.RenameIndex(
                name: "IX_ProductCompositionNodes_ProductCompositionId",
                table: "ProductCompositionNodes",
                newName: "IX_ProductCompositionNodes_PartId");

            migrationBuilder.CreateTable(
                name: "ProductCompositionParts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductCompositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCompositionParts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductCompositionParts_ProductCompositions_ProductComposit~",
                        column: x => x.ProductCompositionId,
                        principalTable: "ProductCompositions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionParts_ProductCompositionId_SortOrder",
                table: "ProductCompositionParts",
                columns: new[] { "ProductCompositionId", "SortOrder" });

            // После RenameColumn ProductCompositionId -> PartId в ProductCompositionNodes,
            // в колонке PartId временно лежит старый ProductCompositionId.
            // Создаём "часть по умолчанию" с Id == Id состава, чтобы сохранить связность без потери данных.
            migrationBuilder.Sql("""
                INSERT INTO "ProductCompositionParts" ("Id", "ProductCompositionId", "Name", "Description", "SortOrder")
                SELECT "Id", "Id", 'Основная часть', NULL, 1
                FROM "ProductCompositions"
                WHERE NOT EXISTS (
                    SELECT 1 FROM "ProductCompositionParts" p WHERE p."ProductCompositionId" = "ProductCompositions"."Id"
                );
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_ProductCompositionNodes_ProductCompositionParts_PartId",
                table: "ProductCompositionNodes",
                column: "PartId",
                principalTable: "ProductCompositionParts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProductCompositionNodes_ProductCompositionParts_PartId",
                table: "ProductCompositionNodes");

            migrationBuilder.DropTable(
                name: "ProductCompositionParts");

            migrationBuilder.RenameColumn(
                name: "PartId",
                table: "ProductCompositionNodes",
                newName: "ProductCompositionId");

            migrationBuilder.RenameIndex(
                name: "IX_ProductCompositionNodes_PartId",
                table: "ProductCompositionNodes",
                newName: "IX_ProductCompositionNodes_ProductCompositionId");

            migrationBuilder.AddForeignKey(
                name: "FK_ProductCompositionNodes_ProductCompositions_ProductComposit~",
                table: "ProductCompositionNodes",
                column: "ProductCompositionId",
                principalTable: "ProductCompositions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
