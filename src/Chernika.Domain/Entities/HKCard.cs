using Chernika.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace Chernika.Domain.Entities;

public class HKCard
{
    public Guid Id { get; set; }

    public const int NameMaxLength = 200;

    /// <summary>Предел длины номера: префикс «ХК-», название, год и суффикс.</summary>
    public const int CodeMaxLength = 260;

    /// <summary>
    /// Номер ХК.
    /// <para>
    /// Номер формирует сервер при создании карты, поэтому в форме он только
    /// показывается и вводить его не требуется. Обязательность номера на
    /// уровне модели была бы ложным требованием: она запрещала сохранить
    /// черновик, пока поле ещё пусто, — и делала ошибку ввода там, где ввода
    /// нет.
    /// </para>
    /// </summary>
    [StringLength(CodeMaxLength, ErrorMessage = "Длина поля «Номер ХК» не может превышать 260 символов.")]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Название объекта, введённое автором карты. Самостоятельное поле: название
    /// и номер хранятся раздельно, номер формируется сервером по форме
    /// ХК-{название}-{год}.
    /// <para>
    /// У старых карт поле отсутствует — это допустимо. Заполнять его номером
    /// автоматически нельзя: название и номер — разные значения. Уникальности
    /// нет: несколько карт могут иметь одинаковое название.
    /// </para>
    /// </summary>
    [StringLength(NameMaxLength, ErrorMessage = "Длина названия не может превышать 200 символов.")]
    public string? Name { get; set; }

    [Required(ErrorMessage = "Поле «Версия» обязательно для заполнения.")]
    [StringLength(10, ErrorMessage = "Длина поля «Версия» не может превышать 10 символов.")]
    public string Version { get; set; } = string.Empty;

    public HKCardStatus Status { get; set; } = HKCardStatus.Draft;

    public HKObjectLevel ObjectLevel { get; set; }

    /// <summary>
    /// Специальный пользовательский выбор для карты, старшая верхняя позиция
    /// которой — узел.
    /// <para>
    /// Допустимы только <see cref="HKObjectLevel.Aggregate"/> и
    /// <see cref="HKObjectLevel.EquipmentModel"/>; null — выбор не сделан.
    /// Хранится отдельно от вычисляемого <see cref="ObjectLevel"/>: когда старшая
    /// верхняя позиция перестаёт быть узлом, прежний выбор сбрасывается и перестаёт
    /// влиять на вычисление.
    /// </para>
    /// </summary>
    public HKObjectLevel? NodeRootCardLevel { get; set; }

    /// <summary>
    /// Признак автономной карты нового сценария. Для таких карт не требуется
    /// привязка к справочному объекту, правило «одна активная ХК на объект»
    /// не применяется, отсутствие дерева допустимо для сохранения черновика.
    /// </summary>
    public bool IsAutonomous { get; set; }

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

    [StringLength(500)]
    public string? RequestOrganization { get; set; }

    [StringLength(500)]
    public string? RequestSenderFullName { get; set; }

    public DateTime? RequestReceivedDate { get; set; }

    [StringLength(2000)]
    public string? RequestDetails { get; set; }

    [StringLength(100)]
    public string? IncomingLetterNumber { get; set; }

    [StringLength(100)]
    public string? OutgoingLetterNumber { get; set; }

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
    public ICollection<HKCardNode> TreeNodes { get; set; } = new List<HKCardNode>();

    /// <summary>
    /// Разделы карты. Пустая коллекция — «разделов нет», а не запись базы:
    /// разделы не создаются автоматически при открытии карты.
    /// </summary>
    public ICollection<HKCardSection> Sections { get; set; } = new List<HKCardSection>();

    /// Состояния этапов согласования: реквизитов и состава.
    /// <para>
    /// У карты ровно две записи. Они переключаются независимо: принятые
    /// реквизиты сохраняются при возврате состава.
    /// </para>
    /// </summary>
    public List<HKCardApproval> Approvals { get; set; } = new();

    /// <summary>
    /// Указатель на последнюю принятую редакцию карты.
    /// <para>
    /// У новой карты его нет, и он не создаётся при создании черновика:
    /// фиктивная принятая редакция означала бы решение, которого не было.
    /// </para>
    /// <para>
    /// Источник истины при чтении принятой структуры — ЭТОТ указатель. Выбор по
    /// максимальной дате, максимальному номеру или последней созданной записи
    /// дал бы другую карту при любом расхождении истории.
    /// </para>
    /// <para>
    /// Ссылка замкнута на свою карту составным внешним ключом: указать на
    /// редакцию соседней карты невозможно на уровне базы.
    /// </para>
    /// </summary>
    public Guid? CurrentAcceptedRevisionId { get; set; }
    public HKCardRevision? CurrentAcceptedRevision { get; set; }
    public ICollection<HKCardStatusLog> StatusLog { get; set; } = new List<HKCardStatusLog>();
    public ICollection<HKCardComponent> ParentComponents { get; set; } = new List<HKCardComponent>();
    public ICollection<HKCardComponent> ChildComponents { get; set; } = new List<HKCardComponent>();
    public ICollection<HKCardMilitaryBranch> MilitaryBranches { get; set; } = new List<HKCardMilitaryBranch>();
    public HKCardAttachment? Attachment { get; set; }
    public ICollection<ReferenceProposal> Proposals { get; set; } = new List<ReferenceProposal>();
}