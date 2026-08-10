using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MilitaryBranchesCaseInsensitiveUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_MilitaryBranches_ArmedForcesType_Name_Active",
                table: "MilitaryBranches");

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT LOWER(TRIM("ArmedForcesType")), LOWER(TRIM("Name"))
                        FROM "MilitaryBranches"
                        WHERE "IsDeleted" = false
                        GROUP BY LOWER(TRIM("ArmedForcesType")), LOWER(TRIM("Name"))
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Не удалось создать functional unique index UX_MilitaryBranches_ArmedForcesType_Name_Active_CI: найдены активные дубликаты пары (Вид ВС РФ + Наименование) без учёта регистра и пробелов. Устраните дубли вручную и повторите миграцию.';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "UX_MilitaryBranches_ArmedForcesType_Name_Active_CI"
                ON "MilitaryBranches" (
                    LOWER(TRIM("ArmedForcesType")),
                    LOWER(TRIM("Name"))
                )
                WHERE "IsDeleted" = false;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX "UX_MilitaryBranches_ArmedForcesType_Name_Active_CI";
                """);

            migrationBuilder.CreateIndex(
                name: "UX_MilitaryBranches_ArmedForcesType_Name_Active",
                table: "MilitaryBranches",
                columns: new[] { "ArmedForcesType", "Name" },
                unique: true,
                filter: "\"IsDeleted\" = false");
        }
    }
}
