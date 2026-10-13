namespace Chernika.Api.Contracts;

/// <summary>
/// Отправка реквизитов и основания на согласование.
/// <para>
/// Согласующий указывается адресно: решение по карте принимает конкретный
/// пользователь, а не любая роль с подходящим правом.
/// </para>
/// <para>
/// <c>RowVersion</c> — версия карты, прочитанная клиентом при загрузке.
/// Без неё два одновременных решения перезаписали бы друг друга.
/// </para>
/// </summary>
public record SubmitApprovalStageRequest(
    string Stage,
    string? ApproverUserId,
    uint RowVersion);

/// <summary>
/// Решение по этапу согласования: принять или вернуть.
/// <para>
/// Статус карты не передаётся: он вычисляется из состояния этапов и не
/// устанавливается клиентом. Общий переключатель статуса обошёл бы оба
/// согласования, поэтому решений ровно два.
/// </para>
/// </summary>
public record ApprovalStageDecisionRequest(
    string Stage,
    string Decision,
    string? Comment,
    uint RowVersion);

/// <summary>
/// Решение по этапу. Отдельный перебор: «утвердить» и «вернуть» — разные
/// права и разные последствия.
/// </summary>
public enum ApprovalDecision
{
    Approve = 1,
    Return = 2
}

/// <summary>
/// Состояние этапов карты для интерфейса.
/// </summary>
public record HKApprovalStageDto(
    string Stage,
    string StageTitle,
    string State,
    string StateTitle,
    string? AssignedApproverUserId,
    DateTime? ApprovedAtUtc);

/// <summary>
/// Возможности правки карты по частям.
/// <para>
/// Три способности отдельными полями, потому что блокируются они по разным
/// причинам: во время рассмотрения реквизитов реквизиты заморожены, а
/// структура доступна.
/// </para>
/// </summary>
public record HKCardEditingCapabilitiesDto(
    bool CanEditDetails,
    bool CanEditComposition,
    bool CanEditPdf);