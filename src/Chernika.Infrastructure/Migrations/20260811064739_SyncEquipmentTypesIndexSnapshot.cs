using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SyncEquipmentTypesIndexSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Sync-миграция: модель больше не описывает индекс TypeGroup+Name,
            // т.к. в БД создан функциональный unique index тем же SQL-оператором.
            // Рабочий индекс UX_EquipmentTypes_TypeGroup_Name_Active_CI не трогаем.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
