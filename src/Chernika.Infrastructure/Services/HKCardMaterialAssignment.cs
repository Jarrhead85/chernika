using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

/// <summary>
/// Действующие правила назначения марок ГСМ строке карты.
/// <para>
/// Правило одно и общее для защищённой записи материалов и для атомарного
/// сохранения структуры: дублировать его в двух командах означало бы рано или
/// поздно разойтись в правилах.
/// </para>
/// <para>
/// Правило относится к НОВОМУ назначению. Сохранённая историческая ссылка
/// задним числом не запрещается.
/// </para>
/// </summary>
internal static class HKCardMaterialAssignment
{
    /// <summary>
    /// Проверяет весь набор назначений одной строки до записи.
    /// </summary>
    public static async Task EnsureAssignableAsync(
        AppDbContext db,
        IReadOnlyCollection<(Guid GsmMaterialId, GsmCategory Category)> requested,
        CancellationToken ct)
    {
        if (requested.Count == 0)
            return;

        // Категория определена: неопределённое значение означало бы запись,
        // которую нельзя ни прочитать, ни согласовать.
        if (requested.Any(m => !Enum.IsDefined(m.Category)))
            throw new InvalidOperationException("Категория материала не определена.");

        // Дубликаты в пределах категории: одна и та же марка дважды в одной
        // категории означает задвоение строки.
        var duplicates = requested
            .GroupBy(m => (m.GsmMaterialId, m.Category))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.GsmMaterialId)
            .ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                "В одной категории повторяется одна и та же марка ГСМ.");

        var ids = requested.Select(m => m.GsmMaterialId).Distinct().ToList();
        var available = await db.GsmMaterials
            .Where(g => ids.Contains(g.Id))
            .Select(g => new { g.Id, g.IsDeleted, g.IsDraft })
            .ToListAsync(ct);

        var missing = ids.Except(available.Select(a => a.Id)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                "Выбрана марка ГСМ, отсутствующая в справочнике. Обновите список и повторите.");

        var notAssignable = available
            .Where(a => a.IsDeleted || a.IsDraft)
            .Select(a => a.Id)
            .ToList();

        if (notAssignable.Count > 0)
            throw new InvalidOperationException(
                "Выбрана марка ГСМ, недоступная для назначения: удалена или является черновиком.");
    }
}