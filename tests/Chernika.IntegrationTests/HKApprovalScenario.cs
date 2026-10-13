using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Chernika.IntegrationTests;

/// <summary>
/// Общие сценарии двухэтапного согласования для тестов.
/// <para>
/// Хелперы существуют потому, что переходы теперь идут командами ЭТАПОВ, а не
/// общим переключателем статуса, и каждая команда требует версию карты,
/// прочитанную при загрузке, а в роли согласующего — другого пользователя.
/// Повторять это в каждом тесте означало бы расходиться с реальным контрактом.
/// </para>
/// </summary>
internal static class HKApprovalScenario
{
    public static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    /// <summary>Автономная карта в состоянии черновика с одной конечной строкой.</summary>
    public static async Task<Guid> CreateDraftCardAsync(
        TestScope s, string actorId, bool withRow = true)
    {
        s.User.CurrentUserId = Guid.Parse(actorId);

        var unit = new AssemblyUnit
        {
            Id = Guid.NewGuid(),
            Code = "АУ-" + Suffix(),
            Name = "СЕ тест"
        };
        s.Db.AssemblyUnits.Add(unit);
        await s.Db.SaveChangesAsync();

        var card = new HKCard
        {
            Name = "Карта теста " + Suffix(),
            IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined,
            Purpose = "Тест workflow",
            NormativeBasis = "ГОСТ",
            Items = new List<HKCardItem>()
        };

        if (withRow)
        {
            card.Items.Add(new HKCardItem
            {
                AssemblyUnitId = unit.Id,
                Quantity = 2,
                Volume = 1.5m,
                UnitOfMeasure = "кг",
                SortOrder = 1
            });
        }

        var created = await s.HK.CreateAsync(card);
        return created.Id;
    }

    public static async Task<uint> VersionAsync(TestScope s, Guid cardId)
    {
        s.Db.ChangeTracker.Clear();
        return (await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId)).RowVersion;
    }

    /// <summary>
    /// Отправляет реквизиты назначенному согласующему.
    /// <para>
    /// Версия карты читается до вызова команды: прочитать её внутри того же
    /// await-выражения нельзя — чтение должно завершиться до вызова.
    /// </para>
    /// </summary>
    public static async Task SubmitDetailsAsync(TestScope s, Guid cardId, string approverUserId)
    {
        var version = await VersionAsync(s, cardId);
        await s.Approvals.SubmitDetailsAsync(cardId, approverUserId, version);
    }

    public static async Task ReturnDetailsAsync(TestScope s, Guid cardId) =>
        await s.Approvals.ReturnDetailsAsync(cardId, null, await VersionAsync(s, cardId));

    public static async Task ApproveDetailsAsync(TestScope s, Guid cardId) =>
        await s.Approvals.ApproveDetailsAsync(cardId, null, await VersionAsync(s, cardId));

    public static async Task SubmitCompositionAsync(TestScope s, Guid cardId) =>
        await s.Approvals.SubmitCompositionAsync(cardId, await VersionAsync(s, cardId));

    public static async Task ReturnCompositionAsync(TestScope s, Guid cardId) =>
        await s.Approvals.ReturnCompositionAsync(cardId, null, await VersionAsync(s, cardId));

    public static Task<HKCardApproval> StageAsync(TestScope s, Guid cardId, HKApprovalStage stage) =>
        s.Db.HKCardApprovals.AsNoTracking()
            .FirstAsync(a => a.HKCardId == cardId && a.Stage == stage);

    /// <summary>
    /// Переводит карту на этап «реквизиты приняты»: создание, отправка, принятие.
    /// </summary>
    public static async Task<Guid> CreateCardWithApprovedDetailsAsync(
        TestScope s, string authorId, string approverId)
    {
        var cardId = await CreateDraftCardAsync(s, authorId);

        var version = await VersionAsync(s, cardId);
        await s.Approvals.SubmitDetailsAsync(cardId, approverId, version);

        s.User.CurrentUserId = Guid.Parse(approverId);
        await ApproveDetailsAsync(s, cardId);

        return cardId;
    }
}