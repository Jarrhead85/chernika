using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCompositionVersionLinksAndOptionalParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProductCompositionAggregates_PartId_AggregateId",
                table: "ProductCompositionAggregates");

            migrationBuilder.AddColumn<Guid>(
                name: "SupersedesProductCompositionId",
                table: "ProductCompositions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "PartId",
                table: "ProductCompositionAggregates",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "ProductCompositionId",
                table: "ProductCompositionAggregates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersedesComplexCompositionId",
                table: "ComplexCompositions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersedesAggregateCompositionId",
                table: "AggregateCompositions",
                type: "uuid",
                nullable: true);

            // Заполняем ProductCompositionId из родительской части, затем проверяем дубликаты,
            // прежде чем накладывать NOT NULL и уникальное ограничение.
            migrationBuilder.Sql(
                """
                UPDATE "ProductCompositionAggregates" AS a
                SET "ProductCompositionId" = p."ProductCompositionId"
                FROM "ProductCompositionParts" AS p
                WHERE a."PartId" = p."Id";
                """);

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "ProductCompositionAggregates" WHERE "ProductCompositionId" IS NULL) THEN
                        RAISE EXCEPTION 'Невозможно наложить ограничение: есть агрегаты без родительской части (ProductCompositionId = NULL).';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM "ProductCompositionAggregates"
                        GROUP BY "ProductCompositionId", "AggregateId"
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Невозможно наложить уникальное ограничение: один агрегат повторяется в составе изделия. Исправьте дубликаты вручную.';
                    END IF;
                END $$;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "ProductCompositionId",
                table: "ProductCompositionAggregates",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositions_SupersedesProductCompositionId",
                table: "ProductCompositions",
                column: "SupersedesProductCompositionId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionAggregates_PartId",
                table: "ProductCompositionAggregates",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionAggregates_ProductCompositionId_Aggregate~",
                table: "ProductCompositionAggregates",
                columns: new[] { "ProductCompositionId", "AggregateId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComplexCompositions_SupersedesComplexCompositionId",
                table: "ComplexCompositions",
                column: "SupersedesComplexCompositionId");

            migrationBuilder.CreateIndex(
                name: "IX_AggregateCompositions_SupersedesAggregateCompositionId",
                table: "AggregateCompositions",
                column: "SupersedesAggregateCompositionId");

            migrationBuilder.AddForeignKey(
                name: "FK_AggregateCompositions_AggregateCompositions_SupersedesAggre~",
                table: "AggregateCompositions",
                column: "SupersedesAggregateCompositionId",
                principalTable: "AggregateCompositions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ComplexCompositions_ComplexCompositions_SupersedesComplexCo~",
                table: "ComplexCompositions",
                column: "SupersedesComplexCompositionId",
                principalTable: "ComplexCompositions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProductCompositionAggregates_ProductCompositions_ProductCom~",
                table: "ProductCompositionAggregates",
                column: "ProductCompositionId",
                principalTable: "ProductCompositions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ProductCompositions_ProductCompositions_SupersedesProductCo~",
                table: "ProductCompositions",
                column: "SupersedesProductCompositionId",
                principalTable: "ProductCompositions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AggregateCompositions_AggregateCompositions_SupersedesAggre~",
                table: "AggregateCompositions");

            migrationBuilder.DropForeignKey(
                name: "FK_ComplexCompositions_ComplexCompositions_SupersedesComplexCo~",
                table: "ComplexCompositions");

            migrationBuilder.DropForeignKey(
                name: "FK_ProductCompositionAggregates_ProductCompositions_ProductCom~",
                table: "ProductCompositionAggregates");

            migrationBuilder.DropForeignKey(
                name: "FK_ProductCompositions_ProductCompositions_SupersedesProductCo~",
                table: "ProductCompositions");

            migrationBuilder.DropIndex(
                name: "IX_ProductCompositions_SupersedesProductCompositionId",
                table: "ProductCompositions");

            migrationBuilder.DropIndex(
                name: "IX_ProductCompositionAggregates_PartId",
                table: "ProductCompositionAggregates");

            migrationBuilder.DropIndex(
                name: "IX_ProductCompositionAggregates_ProductCompositionId_Aggregate~",
                table: "ProductCompositionAggregates");

            migrationBuilder.DropIndex(
                name: "IX_ComplexCompositions_SupersedesComplexCompositionId",
                table: "ComplexCompositions");

            migrationBuilder.DropIndex(
                name: "IX_AggregateCompositions_SupersedesAggregateCompositionId",
                table: "AggregateCompositions");

            migrationBuilder.DropColumn(
                name: "SupersedesProductCompositionId",
                table: "ProductCompositions");

            migrationBuilder.DropColumn(
                name: "ProductCompositionId",
                table: "ProductCompositionAggregates");

            migrationBuilder.DropColumn(
                name: "SupersedesComplexCompositionId",
                table: "ComplexCompositions");

            migrationBuilder.DropColumn(
                name: "SupersedesAggregateCompositionId",
                table: "AggregateCompositions");

            migrationBuilder.AlterColumn<Guid>(
                name: "PartId",
                table: "ProductCompositionAggregates",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionAggregates_PartId_AggregateId",
                table: "ProductCompositionAggregates",
                columns: new[] { "PartId", "AggregateId" },
                unique: true);
        }
    }
}
