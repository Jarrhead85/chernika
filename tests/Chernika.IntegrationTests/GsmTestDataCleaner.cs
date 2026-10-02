using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Уборка данных, созданных интеграционным тестом, — ТОЛЬКО по перечисленным Guid.
/// <para>
/// Общая тестовая БД накапливает данные, а порядок выполнения тестов не
/// задан. Тест, который оставляет после себя строку ХК со ссылкой на
/// soft-deleted марку, способен сломать чужой тест, считающий строки
/// реестра, и наоборот.
/// </para>
/// <para>
/// <b>Удаляются только переданные идентификаторы.</b> Никаких
/// <c>TRUNCATE</c>, «очистить всё» или отбора по имени: чужие строки общей
/// фикстуры трогать нельзя, иначе тесты перестанут быть независимыми, а
/// падение одного удалит данные другого.
/// </para>
/// <para>
/// Порядок — от потомков к предкам, иначе FK RESTRICT не даст удалить.
/// </para>
/// </summary>
public static class GsmTestDataCleaner
{
    /// <summary>
    /// Физически удаляет перечисленные объекты в порядке, безопасном для FK.
    /// Отсутствующие строки пропускаются: тест мог не дойти до их создания.
    /// </summary>
    public static async Task CleanupAsync(AppDbContext db, IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return;

        // Строки материалов — их нельзя бросать: на них висят проверки
        // идемпотентности в других тестах.
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            DELETE FROM ""HKCardItemMaterials""
            WHERE ""Id"" = ANY({guids(ids)})");

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            DELETE FROM ""HKCardComponents""
            WHERE ""ParentHKCardId"" = ANY({guids(ids)})
               OR ""ChildHKCardId""  = ANY({guids(ids)})");

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            DELETE FROM ""HKCardItems""
            WHERE ""Id"" = ANY({guids(ids)})");

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            DELETE FROM ""HKCards""
            WHERE ""Id"" = ANY({guids(ids)})");

        // Мягко удалённые марки не видны глобальному фильтру — нужен IgnoreQueryFilters.
        var materials = await db.GsmMaterials.IgnoreQueryFilters()
            .Where(m => ids.Contains(m.Id))
            .Select(m => m.Id)
            .ToListAsync();
        if (materials.Count > 0)
        {
            var materialIds = materials.ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE FROM ""GsmMaterialRelations""
                WHERE ""PrimaryGsmMaterialId"" = ANY({materialIds})
                   OR ""RelatedGsmMaterialId""  = ANY({materialIds})");
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE FROM ""GsmMaterialClassifications""
                WHERE ""GsmMaterialId"" = ANY({materialIds})");
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE FROM ""GsmMaterials""
                WHERE ""Id"" = ANY({materialIds})");
        }

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            DELETE FROM ""AssemblyUnits"" WHERE ""Id"" = ANY({guids(ids)})");
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            DELETE FROM ""Nodes"" WHERE ""Id"" = ANY({guids(ids)})");
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            DELETE FROM ""EquipmentModels"" WHERE ""Id"" = ANY({guids(ids)})");
    }

    /// <summary>
    /// Считает, сколько строк тестовые объекты оставили в интересующих таблицах.
    /// Используется для проверки «до/после»: пустой результат означает, что
    /// уборка сработала, а не что тест ничего не создавал.
    /// </summary>
    public static async Task<Dictionary<string, int>> CountResidueAsync(
        AppDbContext db, IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return new Dictionary<string, int>();

        var materials = await db.GsmMaterials.IgnoreQueryFilters()
            .Where(m => ids.Contains(m.Id))
            .Select(m => m.Id)
            .ToListAsync();

        var items = await db.HKCardItems
            .Where(i => ids.Contains(i.Id))
            .Select(i => i.Id)
            .ToListAsync();

        return new Dictionary<string, int>
        {
            ["GsmMaterials"] = materials.Count,
            ["GsmMaterialClassifications"] = await db.GsmMaterialClassifications
                .CountAsync(c => materials.Contains(c.GsmMaterialId)),
            ["GsmMaterialRelations"] = await db.GsmMaterialRelations
                .CountAsync(r => materials.Contains(r.PrimaryGsmMaterialId)
                              || materials.Contains(r.RelatedGsmMaterialId)),
            ["HKCards"] = await db.HKCards.IgnoreQueryFilters()
                .CountAsync(c => ids.Contains(c.Id)),
            ["HKCardItems"] = items.Count,
            ["HKCardItemMaterials"] = await db.HKCardItemMaterials
                .CountAsync(m => items.Contains(m.HKCardItemId)),
            ["HKCardComponents"] = await db.HKCardComponents
                .CountAsync(c => ids.Contains(c.ParentHKCardId) || ids.Contains(c.ChildHKCardId)),
            ["Nodes"] = await db.Nodes.IgnoreQueryFilters().CountAsync(n => ids.Contains(n.Id)),
            ["AssemblyUnits"] = await db.AssemblyUnits.IgnoreQueryFilters().CountAsync(a => ids.Contains(a.Id)),
            ["EquipmentModels"] = await db.EquipmentModels.IgnoreQueryFilters().CountAsync(m => ids.Contains(m.Id)),
        };
    }

    /// <summary>
    /// Печатает остаток по таблицам так, чтобы упавший тест показал, ЧТО именно
    /// осталось. Пустое сообщение при непустом остатке — худший вид молчания.
    /// </summary>
    public static string DescribeResidue(Dictionary<string, int> residue)
    {
        var left = residue.Where(kv => kv.Value > 0).ToList();
        if (left.Count == 0) return "остатка нет";
        return string.Join(", ", left.Select(kv => kv.Key + "=" + kv.Value));
    }

    /// <summary>
    /// Параметр-массив Guid. Тип указан явно, иначе Npgsql не может вывести его
    /// из пустого массива и падает на первом же DELETE.
    /// </summary>
    private static Npgsql.NpgsqlParameter guids(IReadOnlyCollection<Guid> ids)
    {
        var p = new Npgsql.NpgsqlParameter { Value = ids.ToArray() };
        p.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid;
        return p;
    }
}
