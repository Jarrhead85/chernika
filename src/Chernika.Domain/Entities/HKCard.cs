using Chernika.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace Chernika.Domain.Entities;

public class HKCard
{
    public Guid Id { get; set; }

    [Required(ErrorMessage = "Поле «Номер ХК» обязательно для заполнения.")]
    [StringLength(50, ErrorMessage = "Длина поля «Номер ХК» не может превышать 50 символов.")]
    public string Code { get; set; } = string.Empty;

    [Required(ErrorMessage = "Поле «Версия» обязательно для заполнения.")]
    [StringLength(10, ErrorMessage = "Длина поля «Версия» не может превышать 10 символов.")]
    public string Version { get; set; } = string.Empty;

    public HKCardStatus Status { get; set; } = HKCardStatus.Draft;

    public HKObjectLevel ObjectLevel { get; set; }

    public Guid? ComplexId { get; set; }
    public Complex? Complex { get; set; }

    public Guid? EquipmentModelId { get; set; }
    public EquipmentModel? EquipmentModel { get; set; }

    public Guid? AggregateId { get; set; }
    public Aggregate? Aggregate { get; set; }

    public Guid? NodeId { get; set; }
    public Node? Node { get; set; }

    public Guid BranchId { get; set; }
    public Branch Branch { get; set; } = null!;

    public Guid? SupersedesHKCardId { get; set; }
    public HKCard? SupersedesHKCard { get; set; }
    public ICollection<HKCard> SupersededBy { get; set; } = new List<HKCard>();

    [StringLength(2000)]
    public string? Purpose { get; set; }

    [StringLength(2000)]
    public string? NormativeBasis { get; set; }

    [StringLength(4000)]
    public string? Notes { get; set; }

    public Guid? AuthorId { get; set; }
    public Guid? ReviewerId { get; set; }
    public DateTime? ApprovedDate { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public DateTime? ExpirationDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [Timestamp]
    public uint RowVersion { get; set; }

    public ICollection<HKCardItem> Items { get; set; } = new List<HKCardItem>();
    public ICollection<HKCardStatusLog> StatusLog { get; set; } = new List<HKCardStatusLog>();
    public ICollection<HKCardComponent> ParentComponents { get; set; } = new List<HKCardComponent>();
    public ICollection<HKCardComponent> ChildComponents { get; set; } = new List<HKCardComponent>();
}