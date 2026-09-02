using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class C2CoefficientNormalizedAndUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "IsDeleted",
                table: "Coefficients",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Coefficients",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "NormativeBasis",
                table: "Coefficients",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Coefficients",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Backfill legacy rows with valid UTC timestamps
            migrationBuilder.Sql(
                """
                UPDATE "Coefficients"
                SET
                    "CreatedAt" = CURRENT_TIMESTAMP,
                    "UpdatedAt" = CURRENT_TIMESTAMP
                WHERE
                    "CreatedAt" < TIMESTAMPTZ '1970-01-01 00:00:00+00'
                    OR "UpdatedAt" < TIMESTAMPTZ '1970-01-01 00:00:00+00';
                """);

            // CHECK constraint: Value > 0
            migrationBuilder.Sql(
                """
                ALTER TABLE "Coefficients"
                ADD CONSTRAINT "CK_Coefficients_Value_Positive"
                CHECK ("Value" > 0);
                """);

            // Functional filtered unique index on (CoefficientTypeId, LOWER(BTRIM(Name))) WHERE IsDeleted = false
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "UX_Coefficients_Type_NormalizedName_NotDeleted"
                ON "Coefficients" ("CoefficientTypeId", LOWER(BTRIM("Name")))
                WHERE "IsDeleted" = false;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"DROP INDEX IF EXISTS ""UX_Coefficients_Type_NormalizedName_NotDeleted"";");

            migrationBuilder.Sql(
                @"ALTER TABLE ""Coefficients"" DROP CONSTRAINT IF EXISTS ""CK_Coefficients_Value_Positive"";");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Coefficients");

            migrationBuilder.DropColumn(
                name: "NormativeBasis",
                table: "Coefficients");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Coefficients");

            migrationBuilder.AlterColumn<bool>(
                name: "IsDeleted",
                table: "Coefficients",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);
        }
    }
}
