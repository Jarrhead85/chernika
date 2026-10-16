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
    private readonly AuditService _audit;

    public HKCardItemService(
        AppDbContext db,
        ICurrentUserService currentUser,
        UserManager<ApplicationUser> userManager,
        IPermissionService permissions,
        AuditService audit)
    {
        _db = db;
        _currentUser = currentUser;
        _userManager = userManager;
        _permissions = permissions;
        _audit = audit;
    }

    /// <summary>
    /// Замена норм и материалов строки — ЗАЩИЩЁННАЯ ЕДИНИЦА РАБОТЫ.
    /// <para>
    /// Раньше метод проверял доступ и состояние, менял отслеживаемый граф и
    /// возвращался, а сохранение выполнял произвольный последующий
    /// <c>SaveChanges</c> уже без проверок. Такая граница не защищает запись.
    /// </para>
    /// <para>
    /// Теперь проверки и фактическая запись выполняются в одной транзакции с
    /// КОНФЛИКТУЮЩИМ УСЛОВИЕМ по <see cref="HKCard.RowVersion"/>: проверка версии
    /// и сама запись в одном операторе. Повторный SELECT перед сохранением гонку
    /// не закрывал — закрывает условие UPDATE.
    /// </para>
    /// </summary>
    public async Task ReplaceMaterialsAsync(Guid hkCardItemId,
        IEnumerable<(Guid GsmMaterialId, GsmCategory Category)> materials,
        CancellationToken ct = default)
    {
        // Вход материализуется один раз: перечисление дважды могло бы дать разные
        // наборы при изменении источника между проверкой и записью.
        var requested = (materials ?? Array.Empty<(Guid, GsmCategory)>()).ToList();

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
            .FirstOrDefaultAsync(ct)
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
            .FirstOrDefaultAsync(ct)
            ?? throw new ArgumentException("ХК не найдена.");

        if (actor.BranchId != card.BranchId && !await _permissions.IsSystemAdminAsync(actorId.ToString()))
            throw new UnauthorizedAccessException("Нет доступа к карточке другой организации.");

        await EnsureCardWritableAsync(card.Status, card.CurrentAcceptedRevisionId, item.HKCardId, ct);
        await HKCardMaterialAssignment.EnsureAssignableAsync(_db, requested, ct);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Повторное чтение внутри транзакции: блокировка могла наступить между
        // проверкой и записью.
        var current = await _db.HKCards
            .AsNoTracking()
            .Where(c => c.Id == item.HKCardId)
            .Select(c => new { c.Status, c.CurrentAcceptedRevisionId, c.RowVersion })
            .FirstAsync(ct);

        if (current.RowVersion != card.RowVersion)
        {
            await tx.RollbackAsync(ct);
            throw new HkCardVersionConflictException(
                "Карточка была изменена другим пользователем. Обновите страницу и повторите попытку.");
        }

        await EnsureCardWritableAsync(current.Status, current.CurrentAcceptedRevisionId, item.HKCardId, ct);

        // КОНФЛИКТУЮЩЕЕ УСЛОВИЕ ЗАПИСИ. Если версия карты успела измениться
        // (защищённое состояние наступило, отправка ушла, принятие состоялось),
        // UPDATE не затронет ни одной строки и EF вернёт конкурентное
        // исключение. Проверки до этого места гонку не закрывали.
        // Колонка версии объявлена как "xmin" типа xid, а параметр uint Npgsql
        // выводит как bigint: прямое сравнение даёт "оператор не существует:
        // xid = bigint". Приведение выполняется явным текстовым маршрутом.
        var updated = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "HKCards" SET "UpdatedAt" = "UpdatedAt"
            WHERE "Id" = {item.HKCardId}
              AND "xmin"::text::bigint = {card.RowVersion}
            """, ct);

        if (updated != 1)
        {
            await tx.RollbackAsync(ct);
            throw new HkCardVersionConflictException(
                "Карточка была изменена другим пользователем. Обновите страницу и повторите попытку.");
        }

        // Набор заменяется целиком и только теперь. Частично удалённый набор
        // невозможен: удаление и вставка идут в одной транзакции.
        var existing = await _db.HKCardItemMaterials
            .Where(m => m.HKCardItemId == hkCardItemId)
            .ToListAsync(ct);
        _db.HKCardItemMaterials.RemoveRange(existing);

        _db.HKCardItemMaterials.AddRange(requested.Select(m => new HKCardItemMaterial
        {
            Id = Guid.NewGuid(),
            HKCardItemId = hkCardItemId,
            GsmMaterialId = m.GsmMaterialId,
            Category = m.Category
        }));

        await _audit.LogAsync(new AuditWriteRequest(
            "HKCardItem", hkCardItemId.ToString(), "MaterialsReplaced", actorId,
            EntityDisplayName: $"Строка ХК {hkCardItemId}",
            Details: $"Нормы и материалы заменены: {requested.Count} позиций."), ct);

        try
        {
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            throw new HkCardVersionConflictException(
                "Карточка была изменена другим пользователем. Обновите страницу и повторите попытку.");
        }
    }

    /// <summary>
    /// Действующие правила назначения ГСМ из основного пути ХК, а не проверка
    /// одного лишь наличия идентификатора.
    /// <para>
    /// Категория задаётся явно и не выводится из справочных связей аналогов.
    /// Прежние <c>Type</c>/<c>Gost</c> не восстанавливаются: их в модели нет.
    /// </para>
    /// <para>
    /// Сохранённая историческая ссылка, которой нет в проверяемом наборе,
    /// ничего не требует: правило относится к НОВОМУ назначению.
    /// </para>
    /// </summary>
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
            throw new InvalidOperationException("Карточка не редактируется в текущем состоянии.");

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
            throw new InvalidOperationException("Состав и нормы приняты: изменение заблокировано.");
    }
}
