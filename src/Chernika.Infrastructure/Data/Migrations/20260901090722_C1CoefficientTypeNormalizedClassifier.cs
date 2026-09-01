using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class C1CoefficientTypeNormalizedClassifier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Coefficients_CoefficientTypes_CoefficientTypeId",
                table: "Coefficients");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "CoefficientTypes");

            migrationBuilder.DropColumn(
                name: "Group",
                table: "CoefficientTypes");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "CoefficientTypes",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "CoefficientTypes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "CoefficientTypes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "CoefficientTypes",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "IX_CoefficientTypes_IsDeleted",
                table: "CoefficientTypes",
                column: "IsDeleted");

            migrationBuilder.CreateIndex(
                name: "IX_CoefficientTypes_Name",
                table: "CoefficientTypes",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_CoefficientTypes_SortOrder",
                table: "CoefficientTypes",
                column: "SortOrder");

            migrationBuilder.AddForeignKey(
                name: "FK_Coefficients_CoefficientTypes_CoefficientTypeId",
                table: "Coefficients",
                column: "CoefficientTypeId",
                principalTable: "CoefficientTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(
                @"CREATE UNIQUE INDEX ""UX_CoefficientTypes_NormalizedName_NotDeleted""
                ON ""CoefficientTypes"" (LOWER(BTRIM(""Name"")))
                WHERE ""IsDeleted"" = false;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"DROP INDEX IF EXISTS ""UX_CoefficientTypes_NormalizedName_NotDeleted"";");

            migrationBuilder.DropForeignKey(
                name: "FK_Coefficients_CoefficientTypes_CoefficientTypeId",
                table: "Coefficients");

            migrationBuilder.DropIndex(
                name: "IX_CoefficientTypes_IsDeleted",
                table: "CoefficientTypes");

            migrationBuilder.DropIndex(
                name: "IX_CoefficientTypes_Name",
                table: "CoefficientTypes");

            migrationBuilder.DropIndex(
                name: "IX_CoefficientTypes_SortOrder",
                table: "CoefficientTypes");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "CoefficientTypes");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "CoefficientTypes");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "CoefficientTypes");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "CoefficientTypes");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "CoefficientTypes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Group",
                table: "CoefficientTypes",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddForeignKey(
                name: "FK_Coefficients_CoefficientTypes_CoefficientTypeId",
                table: "Coefficients",
                column: "CoefficientTypeId",
                principalTable: "CoefficientTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
