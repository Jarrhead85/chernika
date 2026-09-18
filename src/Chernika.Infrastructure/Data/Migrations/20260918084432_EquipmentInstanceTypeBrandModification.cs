using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class EquipmentInstanceTypeBrandModification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Новые поля экземпляра (вид техники, марка, модификация).
            migrationBuilder.AddColumn<string>(
                name: "Brand",
                table: "EquipmentInstances",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EquipmentTypeId",
                table: "EquipmentInstances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Modification",
                table: "EquipmentInstances",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            // 2. Перенос данных изделия (марка/модификация/вид) в его экземпляры.
            migrationBuilder.Sql(
                """
                UPDATE "EquipmentInstances" i
                SET "EquipmentTypeId" = m."EquipmentTypeId",
                    "Brand" = m."Brand",
                    "Modification" = m."Modification"
                FROM "EquipmentModels" m
                WHERE i."EquipmentModelId" = m."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentInstances_EquipmentTypeId",
                table: "EquipmentInstances",
                column: "EquipmentTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_EquipmentInstances_EquipmentTypes_EquipmentTypeId",
                table: "EquipmentInstances",
                column: "EquipmentTypeId",
                principalTable: "EquipmentTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // 3. Удаление перенесённых (и устаревшего текстового) полей изделия.
            migrationBuilder.DropColumn(
                name: "Brand",
                table: "EquipmentModels");

            migrationBuilder.DropColumn(
                name: "Modification",
                table: "EquipmentModels");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "EquipmentModels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Brand",
                table: "EquipmentModels",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Modification",
                table: "EquipmentModels",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "EquipmentModels",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "EquipmentModels" m
                SET "Brand" = i."Brand",
                    "Modification" = i."Modification"
                FROM "EquipmentInstances" i
                WHERE i."EquipmentModelId" = m."Id";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_EquipmentInstances_EquipmentTypes_EquipmentTypeId",
                table: "EquipmentInstances");

            migrationBuilder.DropIndex(
                name: "IX_EquipmentInstances_EquipmentTypeId",
                table: "EquipmentInstances");

            migrationBuilder.DropColumn(
                name: "Brand",
                table: "EquipmentInstances");

            migrationBuilder.DropColumn(
                name: "EquipmentTypeId",
                table: "EquipmentInstances");

            migrationBuilder.DropColumn(
                name: "Modification",
                table: "EquipmentInstances");
        }
    }
}
