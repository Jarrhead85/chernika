using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameMilitaryBranchCodeToArmedForcesType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MilitaryBranches_Code",
                table: "MilitaryBranches");

            migrationBuilder.RenameColumn(
                name: "Code",
                table: "MilitaryBranches",
                newName: "ArmedForcesType");

            migrationBuilder.AlterColumn<string>(
                name: "ArmedForcesType",
                table: "MilitaryBranches",
                type: "character varying(250)",
                maxLength: 250,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "MilitaryBranches"
                        WHERE "IsDeleted" = false
                        GROUP BY LOWER(TRIM("ArmedForcesType")), LOWER(TRIM("Name"))
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Не удалось создать уникальный индекс UX_MilitaryBranches_ArmedForcesType_Name_Active: найдены дубликаты активных записей (Вид ВС РФ + Наименование). Устраните дубли вручную и повторите миграцию.';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_MilitaryBranches_ArmedForcesType_Name_Active",
                table: "MilitaryBranches",
                columns: new[] { "ArmedForcesType", "Name" },
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_MilitaryBranches_ArmedForcesType_Name_Active",
                table: "MilitaryBranches");

            migrationBuilder.AlterColumn<string>(
                name: "ArmedForcesType",
                table: "MilitaryBranches",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(250)",
                oldMaxLength: 250);

            migrationBuilder.RenameColumn(
                name: "ArmedForcesType",
                table: "MilitaryBranches",
                newName: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_MilitaryBranches_Code",
                table: "MilitaryBranches",
                column: "Code",
                unique: true,
                filter: "\"IsDeleted\" = false");
        }
    }
}
