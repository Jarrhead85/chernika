using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

public class WorkTask
{
    public Guid Id { get; set; }

    public string Title { get; set; } = null!;
    public string? Description { get; set; }

    public WorkTaskType Type { get; set; }
    public WorkTaskStatus Status { get; set; }
    public WorkTaskPriority Priority { get; set; }

    public string? CreatedByUserId { get; set; }
    public string? AssignedToUserId { get; set; }
    public string? AssignedRole { get; set; }

    public Guid? BranchId { get; set; }

    public Guid? WorkTaskGroupId { get; set; }
    public WorkTaskGroup? WorkTaskGroup { get; set; }

    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public string? EntityCodeSnapshot { get; set; }
    public string? EntityTitleSnapshot { get; set; }

    /// <summary>
    /// Этап согласования ХК, к которому относится задача.
    /// <para>
    /// Нужен, чтобы повторная отправка закрывала задачу доработки ИМЕННО
    /// своего этапа и не трогала чужую. Определять этап по тексту заголовка
    /// ненадёжно: заголовок — это текст, а не данные.
    /// </para>
    /// <para>
    /// null у задач, не относящихся к этапам согласования ХК.
    /// </para>
    /// </summary>
    public HKApprovalStage? HKStage { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DueDateUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? CompletedByUserId { get; set; }
    public string? CompletionComment { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
}
