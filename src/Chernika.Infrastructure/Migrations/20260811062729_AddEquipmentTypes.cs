using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEquipmentTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EquipmentTypeId",
                table: "EquipmentModels",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EquipmentTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TypeGroup = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    Name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquipmentTypes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentModels_EquipmentTypeId",
                table: "EquipmentModels",
                column: "EquipmentTypeId");

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT LOWER(TRIM(COALESCE("TypeGroup", ''))), LOWER(TRIM("Name"))
                        FROM "EquipmentTypes"
                        WHERE "IsDeleted" = false
                        GROUP BY LOWER(TRIM(COALESCE("TypeGroup", ''))), LOWER(TRIM("Name"))
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Не удалось создать functional unique index UX_EquipmentTypes_TypeGroup_Name_Active_CI: найдены активные дубликаты пары (Вид техники + Наименование) без учёта регистра и пробелов. Устраните дубли вручную и повторите миграцию.';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "UX_EquipmentTypes_TypeGroup_Name_Active_CI"
                ON "EquipmentTypes" (
                    LOWER(TRIM(COALESCE("TypeGroup", ''))),
                    LOWER(TRIM("Name"))
                )
                WHERE "IsDeleted" = false;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_EquipmentModels_EquipmentTypes_EquipmentTypeId",
                table: "EquipmentModels",
                column: "EquipmentTypeId",
                principalTable: "EquipmentTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EquipmentModels_EquipmentTypes_EquipmentTypeId",
                table: "EquipmentModels");

            migrationBuilder.Sql("""
                DROP INDEX "UX_EquipmentTypes_TypeGroup_Name_Active_CI";
                """);

            migrationBuilder.DropTable(
                name: "EquipmentTypes");

            migrationBuilder.DropIndex(
                name: "IX_EquipmentModels_EquipmentTypeId",
                table: "EquipmentModels");

            migrationBuilder.DropColumn(
                name: "EquipmentTypeId",
                table: "EquipmentModels");
        }
    }
}
