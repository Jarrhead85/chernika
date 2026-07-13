using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameMaterialCategoryToGsmCategory : Migration
    {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Переименование значения "Main" → "Primary" в enum MaterialCategory → GsmCategory.
        // Все существующие строки со значением "Main" приводятся к "Primary".
        // Нестандартные значения (если есть) остаются без изменений — требуют ручной проверки.
        // TODO: при необходимости вручную проверить и исправить нестандартные значения в HKCardItemMaterials.Category.
        migrationBuilder.Sql(
            @"UPDATE ""HKCardItemMaterials"" SET ""Category"" = 'Primary' WHERE ""Category"" = 'Main'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Откат: "Primary" → "Main"
        migrationBuilder.Sql(
            @"UPDATE ""HKCardItemMaterials"" SET ""Category"" = 'Main' WHERE ""Category"" = 'Primary'");
    }
    }
}
