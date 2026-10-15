using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Chernika.Infrastructure.Services;

public class HKCardItemService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPermissionService _permissions;

    public HKCardItemService(
        AppDbContext db,
        ICurrentUserService currentUser,
        UserManager<ApplicationUser> userManager,
        IPermissionService permissions)
    {
        _db = db;
        _currentUser = currentUser;
        _userManager = userManager;
        _permissions = permissions;
    }

    /// <summary>
    /// Замена норм и материалов строки.
    /// <para>
    /// Раньше проверок не было вовсе: команда работала с любой картой и любым
    /// пользователем, минуя права доступа и блокировки этапов. Теперь используются
    /// те же механизмы, что и в остальных командах карты, новой системы
    /// авторизации не вводится.
    /// </para>
    /// </summary>
    public async Task ReplaceMaterialsAsync(Guid hkCardItemId,
        IEnumerable<(Guid GsmMaterialId, GsmCategory Category)> materials,
        CancellationToken ct = default)
    {
        var actorId = _currentUser.GetRequiredUserId();

        var actor = await _userManager.FindByIdAsync(actorId.ToString())
            ?? throw new UnauthorizedAccessException("Пользователь не найден.");
        if (!actor.IsActive)
            throw new UnauthorizedAccessException("Пользователь отключён.");

        if (!await _permissions.HasPermissionAsync(actorId.ToString(), PermissionCodes.HKWork))
            throw new UnauthorizedAccessException("Недостаточно прав для изменения состава ХК.");

        var item = await _db.HKCardItems
            .AsNoTracking()
            .Where(i => i.Id == hkCardItemId)
            .Select(i => new { i.HKCardId })
            .FirstOrDefaultAsync()
            ?? throw new ArgumentException("Строка ХК не найдена.");

        var card = await _db.HKCards
            .AsNoTracking()
            .Where(c => c.Id == item.HKCardId)
            .Select(c => new
            {
                c.Status,
                c.BranchId,
                c.CurrentAcceptedRevisionId,
                c.RowVersion
            })
            .FirstOrDefaultAsync()
            ?? throw new ArgumentException("ХК не найдена.");

        if (actor.BranchId != card.BranchId && !await _permissions.IsSystemAdminAsync(actorId.ToString()))
            throw new UnauthorizedAccessException("Нет доступа к карточке другой организации.");

        await EnsureCardWritableAsync(card.Status, card.CurrentAcceptedRevisionId, item.HKCardId, ct);

        // Назначаемые марки должны существовать и быть доступны: принятая или
        // проверяемая карта не должна получать ссылку на удалённый справочник.
        var requested = materials.Select(m => m.GsmMaterialId).Distinct().ToList();
        var available = await _db.GsmMaterials
            .Where(g => requested.Contains(g.Id))
            .Select(g => g.Id)
            .ToListAsync();

        var missing = requested.Except(available).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                "Выбрана марка ГСМ, недоступная для назначения. Обновите список и повторите.");

        // Конкурентный переход карты в принятое состояние отсекается версией:
        // строка должна остаться той же, на которую опирались проверки.
        var current = await _db.HKCards
            .AsNoTracking()
            .Where(c => c.Id == item.HKCardId)
            .Select(c => c.RowVersion)
            .FirstAsync();
        if (current != card.RowVersion)
            throw new InvalidOperationException(
                "Карточка была изменена другим пользователем. Обновите страницу и повторите попытку.");

        var existing = await _db.HKCardItemMaterials
            .Where(m => m.HKCardItemId == hkCardItemId)
            .ToListAsync();
        _db.HKCardItemMaterials.RemoveRange(existing);

        var newMaterials = materials.Select(m => new HKCardItemMaterial
        {
            Id = Guid.NewGuid(),
            HKCardItemId = hkCardItemId,
            GsmMaterialId = m.GsmMaterialId,
            Category = m.Category
        }).ToList();

        await _db.HKCardItemMaterials.AddRangeAsync(newMaterials);
    }

    /// <summary>
    /// Состав карты записывается только в разрешённом состоянии.
    /// <para>
    /// Заблокирован при Composition.OnReview, при Composition.Approved, при
    /// принятом состоянии карты, при наличии принятой структуры и при
    /// Archived/Deleted. Во время рассмотрения РЕКВИЗИТОВ автор по-прежнему может
    /// заполнять строки и материалы: блокирует этап состава, а не общий статус.
    /// </para>
    /// </summary>
    private async Task EnsureCardWritableAsync(
        HKCardStatus status, Guid? acceptedRevisionId, Guid cardId, CancellationToken ct)
    {
        if (status is HKCardStatus.Archived or HKCardStatus.Deleted)
            throw new InvalidOperationException(
                "Карточка не редактируется в текущем состоянии.");

        if (status == HKCardStatus.Approved)
            throw new InvalidOperationException(
                "ХК принята: опубликованное содержание недоступно для изменения.");

        if (acceptedRevisionId.HasValue)
            throw new InvalidOperationException(
                "У карты есть принятая структура: изменение заблокировано до начала доработки.");

        var composition = await _db.HKCardApprovals
            .AsNoTracking()
            .Where(a => a.HKCardId == cardId && a.Stage == HKApprovalStage.Composition)
            .Select(a => (HKApprovalStageState?)a.State)
            .FirstOrDefaultAsync(ct);

        // Карты без этапов согласования — до появления модели этапов.
        if (composition is null)
            return;

        if (composition == HKApprovalStageState.OnReview)
            throw new InvalidOperationException(
                "Состав и нормы находятся на согласовании: изменение заблокировано.");

        if (composition == HKApprovalStageState.Approved)
            throw new InvalidOperationException(
                "Состав и нормы приняты: изменение заблокировано.");
    }
}
