using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

/// <summary>
/// Состояние одного этапа согласования карты.
/// <para>
/// Сущность одна на пару «карта + этап»: у карты ровно две записи — по реквизитам
/// и по составу. Объединение этапов в одной записи невозможно: они переключаются
/// независимо, и у каждого своё состояние, свой назначенный согласующий и своя
/// дата принятия.
/// </para>
/// <para>
/// Даты принятия не выводятся из <see cref="HKCard.EffectiveDate"/> и
/// <see cref="HKCard.ExpirationDate"/>: это сроки действия документа, а не
/// моменты решения. Они хранятся отдельно и не заменяют друг друга.
/// </para>
/// </summary>
public class HKCardApproval
{
    public Guid Id { get; set; }

    public Guid HKCardId { get; set; }
    public HKCard? HKCard { get; set; }

    /// <summary>Этап согласования: реквизиты или состав.</summary>
    public HKApprovalStage Stage { get; set; }

    /// <summary>Состояние этапа. Отдельное от общего статуса карты.</summary>
    public HKApprovalStageState State { get; set; }

    /// <summary>
    /// Назначенный согласующий — конкретный пользователь, а не роль.
    /// <para>
    /// Тип строки соответствует ключу Identity (<c>Users.Id</c> — text) и
    /// соглашению проекта для ссылок на пользователя: <c>WorkTask.AssignedToUserId</c>,
    /// <c>ProductComposition.ApprovedByUserId</c>. Тождественность назначения
    /// проверяется по этому значению.
    /// </para>
    /// </summary>
    public string? AssignedApproverUserId { get; set; }

    /// <summary>
    /// Дата принятия этапа. Заполняется один раз при принятии и не обнуляется
    /// при последующих возвратах другого этапа.
    /// </summary>
    public DateTime? ApprovedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>
    /// Версия строки (xmin PostgreSQL). Защищает от двух одновременных решений:
    /// без неё второе решение перезаписало бы первое молча.
    /// </summary>
    public uint RowVersion { get; set; }
}