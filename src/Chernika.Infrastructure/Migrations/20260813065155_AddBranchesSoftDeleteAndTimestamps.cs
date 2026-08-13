using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBranchesSoftDeleteAndTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Branches",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Branches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Branches",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Branches",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.Sql("""
                UPDATE "Branches"
                SET "CreatedAt" = CURRENT_TIMESTAMP, "UpdatedAt" = CURRENT_TIMESTAMP
                WHERE "CreatedAt" < '2000-01-01';
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "Branches" ALTER COLUMN "CreatedAt" DROP DEFAULT;
                ALTER TABLE "Branches" ALTER COLUMN "UpdatedAt" DROP DEFAULT;
                """);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT LOWER(TRIM("Name"))
                        FROM "Branches"
                        WHERE "IsDeleted" = false
                        GROUP BY LOWER(TRIM("Name"))
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Не удалось создать functional unique index UX_Branches_Name_Active_CI: найдены активные дубликаты наименований без учёта регистра и пробелов. Устраните дубли вручную и повторите миграцию.';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "UX_Branches_Name_Active_CI"
                ON "Branches" (
                    LOWER(TRIM("Name"))
                )
                WHERE "IsDeleted" = false;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX "UX_Branches_Name_Active_CI";
                """);

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Branches");
        }
    }
}
