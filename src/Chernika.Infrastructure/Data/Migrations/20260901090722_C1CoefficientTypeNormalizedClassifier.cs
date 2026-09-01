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
            // Legacy Group/Description columns are retained for historical data compatibility.
            // Physical cleanup requires a separate approved data migration after production review.

            migrationBuilder.DropForeignKey(
                name: "FK_Coefficients_CoefficientTypes_CoefficientTypeId",
                table: "Coefficients");

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

            // Backfill legacy rows with valid UTC timestamps.
            // New rows always get UTC time from TimeProvider in CoefficientService.
            // Use a range check to handle potential timezone representation differences.
            migrationBuilder.Sql(
                """
                UPDATE "CoefficientTypes"
                SET
                    "CreatedAt" = CURRENT_TIMESTAMP,
                    "UpdatedAt" = CURRENT_TIMESTAMP
                WHERE
                    "CreatedAt" < TIMESTAMPTZ '1970-01-01 00:00:00+00'
                    OR "UpdatedAt" < TIMESTAMPTZ '1970-01-01 00:00:00+00';
                """);

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

            // Legacy Group/Description are not restored here because they were never dropped in Up.

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