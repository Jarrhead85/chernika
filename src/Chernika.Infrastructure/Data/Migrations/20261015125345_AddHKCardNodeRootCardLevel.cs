using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHKCardNodeRootCardLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NodeRootCardLevel",
                table: "HKCards",
                type: "integer",
                nullable: true);

            // Массового вычисления старых значений нет: колонка nullable и старые
            // строки остаются с NULL. Уровень выводится из структуры при сохранении.
            // Допустимы только EquipmentModel (2) и Aggregate (3): ограничение
            // дополняет доменную проверку на уровне базы.
            migrationBuilder.Sql(
                "ALTER TABLE \"HKCards\" ADD CONSTRAINT \"CK_HKCards_NodeRootCardLevel\" " +
                "CHECK (\"NodeRootCardLevel\" IS NULL OR \"NodeRootCardLevel\" IN (2, 3))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NodeRootCardLevel",
                table: "HKCards");
        }
    }
}
