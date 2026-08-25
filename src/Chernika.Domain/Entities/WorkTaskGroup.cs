using System.ComponentModel.DataAnnotations;

namespace Chernika.Domain.Entities;

/// <summary>
/// Групповая задача: одна логическая задача, назначенная нескольким исполнителям
/// (например, согласование состава всем NormAdmin филиала). Выполнение задачи одним
/// исполнителем закрывает задачу у всех получателей.
/// </summary>
public class WorkTaskGroup
{
    public Guid Id { get; set; }

    /// <summary>Тип задачи (значение <see cref="Enums.WorkTaskType"/> в виде строки).</summary>
    public string TaskType { get; set; } = null!;

    /// <summary>Тип сущности, к которой относится групповая задача (например, ProductComposition).</summary>
    public string EntityType { get; set; } = null!;

    public Guid EntityId { get; set; }

    public Guid BranchId { get; set; }

    public string Title { get; set; } = null!;

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? CompletedByUserId { get; set; }

    [Timestamp]
    public uint RowVersion { get; set; }

    public ICollection<WorkTask> WorkTasks { get; set; } = new List<WorkTask>();
}
