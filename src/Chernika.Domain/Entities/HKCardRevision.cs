using Chernika.Domain.Enums;

namespace Chernika.Domain.Entities;

/// <summary>
/// Неизменяемый снимок редакции карты.
/// <para>
/// Снимок нужен для того, чтобы принятая структура читалась из СОХРАНЁННЫХ
/// значений, а не из текущих рабочих строк. Иначе переименование справочника
/// или правка черновика изменили бы уже принятую карту.
/// </para>
/// <para>
/// Снимок не связан живыми внешними ключами с <see cref="HKCardNode"/>,
/// <see cref="HKCardItem"/> и <see cref="HKCardItemMaterial"/>: исходные
/// идентификаторы сохраняются как справочные значения для происхождения, но
/// удаление или изменение рабочей позиции снимок не трогает.
/// </para>
/// <para>
/// <b>Время фиксации не является датой утверждения.</b> Это момент, когда
/// снимок сделан. Реальной даты решения здесь нет: она появляется у этапа
/// согласования, и выдумывать её здесь нельзя.
/// </para>
/// </summary>
public class HKCardRevision
{
    public Guid Id { get; set; }

    public Guid HKCardId { get; set; }
    public HKCard HKCard { get; set; } = null!;

    /// <summary>
    /// Внутренний порядковый номер редакции ВНУТРИ карты.
    /// <para>
    /// Это не <see cref="HKCard.Code"/> и не отображаемая
    /// <see cref="HKCard.Version"/>: те идентифицируют карту во внешнем мире и
    /// меняются по своим правилам.
    /// </para>
    /// </summary>
    public int RevisionNumber { get; set; }

    /// <summary>Момент фиксации в UTC. Техническое время, не дата утверждения.</summary>
    public DateTime CapturedAtUtc { get; set; }

    /// <summary>Кто зафиксировал. Ключ Identity (Users.Id — text).</summary>
    public string CapturedByUserId { get; set; } = string.Empty;

    /// <summary>
    /// Версия рабочей карты на момент фиксации. Позволяет установить, из какой
    /// версии черновика получен снимок.
    /// </summary>
    public uint SourceCardRowVersion { get; set; }

    // ── Реквизиты на момент фиксации ──

    public string? Name { get; set; }
    public string? Code { get; set; }
    public string? Version { get; set; }
    public HKObjectLevel ObjectLevel { get; set; }

    public string? Purpose { get; set; }
    public string? NormativeBasis { get; set; }
    public string? Notes { get; set; }
    public string? RequestOrganization { get; set; }
    public string? RequestSenderFullName { get; set; }
    public DateTime? RequestReceivedDate { get; set; }
    public string? RequestDetails { get; set; }
    public string? IncomingLetterNumber { get; set; }
    public string? OutgoingLetterNumber { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public DateTime? ExpirationDate { get; set; }

    // ── Основание и PDF ──
    //
    // Одного идентификатора вложения недостаточно: замена PDF удаляет исходный
    // файл, и ссылка перестала бы что-то означать. Поэтому снимок хранит
    // содержимое ссылки (ключ хранения, имя, размер, содержимое-тип и дату
    // загрузки). Проверка выдачи PDF опирается на доступ к этой карте.

    public Guid? AttachmentId { get; set; }
    public string? AttachmentOriginalFileName { get; set; }
    public string? AttachmentStorageKey { get; set; }
    public string? AttachmentContentType { get; set; }
    public long? AttachmentSizeBytes { get; set; }

    /// <summary>
    /// Контрольная сумма зафиксированного файла. По ней проверяется, что
    /// сохранённое основание ещё то самое, а не подменённый файл.
    /// </summary>
    public string? AttachmentSha256 { get; set; }

    // ── Данные решения ──
    //
    // Отдельны от CapturedAtUtc: фиксация — технический момент снятия снимка,
    // решение — момент, когда назначенный согласующий принял состав. Выдавать
    // одно за другое нельзя. Заполняются при создании редакции и с этого момента
    // неизменны, как и сама редакция.

    /// <summary>
    /// Момент принятия редакции назначенным согласующим. null у редакции,
    /// зафиксированной, но ещё не принятой.
    /// </summary>
    public DateTime? AcceptedAtUtc { get; set; }

    /// <summary>
    /// Кто принял. Ключ Identity (Users.Id — text), как и остальные ссылки на
    /// пользователя в проекте.
    /// </summary>
    public string? AcceptedByUserId { get; set; }

    public DateTime? AttachmentUploadedAtUtc { get; set; }

    public List<HKCardRevisionSection> Sections { get; set; } = new();
    public List<HKCardRevisionNode> Nodes { get; set; } = new();
    public List<HKCardRevisionItem> Items { get; set; } = new();
}

/// <summary>
/// Позиция собственной структуры в редакции.
/// <para>
/// Уровень и пропуски сохраняются ИСХОДНЫМИ значениями. Уровень не
/// пересчитывается догадкой: у редакции, чей уровень
/// <see cref="HKObjectLevel.Undefined"/>, так и остаётся Undefined.
/// </para>
/// </summary>
public class HKCardRevisionNode
{
    public Guid Id { get; set; }

    public Guid RevisionId { get; set; }
    public HKCardRevision Revision { get; set; } = null!;

    /// <summary>
    /// Раздел самостоятельной ветки в снимке.
    /// <para>
    /// Заполняется только у верхней позиции: у вложенной значение равно null,
    /// раздел наследуется от корня. Живая ссылка на рабочий раздел не читается —
    /// принятая редакция воспроизводится из снимка.
    /// </para>
    /// </summary>
    public Guid? SectionId { get; set; }
    public HKCardRevisionSection? Section { get; set; }

    /// <summary>
    /// Родитель ВНУТРИ той же редакции. null — верхняя позиция.
    /// Целостность проверяет база: пара «редакция + родитель» ссылается на
    /// позицию ТОЙ ЖЕ редакции.
    /// </summary>
    public Guid? ParentId { get; set; }
    public HKCardRevisionNode? Parent { get; set; }

    /// <summary>Идентификатор рабочей позиции. Справочное значение для происхождения.</summary>
    public Guid SourceNodeId { get; set; }

    public HKObjectLevel Level { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// Конечная строка в редакции.
/// <para>
/// Наименование и обозначение сборочной единицы копируются ЗДЕСЬ. Иначе
/// переименование справочника изменило бы уже принятую карту.
/// </para>
/// <para>
/// <see cref="NodeId"/> — ссылка на позицию этой же редакции; null для строки
/// под корнем карты. Живой FK на рабочую <see cref="HKCardNode"/> здесь нет.
/// </para>
/// </summary>
public class HKCardRevisionItem
{
    public Guid Id { get; set; }

    public Guid RevisionId { get; set; }
    public HKCardRevision Revision { get; set; } = null!;

    public Guid? NodeId { get; set; }
    public HKCardRevisionNode? Node { get; set; }

    /// <summary>
    /// Раздел самостоятельной конечной строки в снимке.
    /// <para>
    /// Заполняется только у строки непосредственно под корнем редакции.
    /// </para>
    /// </summary>
    public Guid? SectionId { get; set; }
    public HKCardRevisionSection? Section { get; set; }

    /// <summary>Идентификатор рабочей строки. Справочное значение.</summary>
    public Guid SourceItemId { get; set; }

    /// <summary>Справочные значения исходной сборочной единицы.</summary>
    public Guid AssemblyUnitId { get; set; }
    public string? AssemblyUnitCode { get; set; }
    public string? AssemblyUnitName { get; set; }

    public int Quantity { get; set; }
    public decimal Volume { get; set; }
    public string? UnitOfMeasure { get; set; }
    public string? Periodicity { get; set; }
    public string? Notes { get; set; }
    public int SortOrder { get; set; }

    public List<HKCardRevisionItemMaterial> Materials { get; set; } = new();
}

/// <summary>
/// Материал строки редакции.
/// <para>
/// <see cref="Name"/> и <see cref="Nd"/> копируются из снимка, а не читаются
/// через живую ссылку на справочник. Прежних <c>Type</c> и <c>Gost</c> в
/// модели нет — восстанавливать нечего, и подставлять справочные аналоги
/// вместо актуальных НД нельзя.
/// </para>
/// </summary>
public class HKCardRevisionItemMaterial
{
    public Guid Id { get; set; }

    public Guid RevisionItemId { get; set; }
    public HKCardRevisionItem RevisionItem { get; set; } = null!;

    /// <summary>Идентификатор марки ГСМ. Справочное значение для происхождения.</summary>
    public Guid SourceGsmMaterialId { get; set; }

    /// <summary>
    /// Категория ИЗ СТРОКИ карты, а не из справочника: автор относил марку к
    /// категории при заполнении строки.
    /// </summary>
    public GsmCategory Category { get; set; }

    public string? Name { get; set; }
    public string? Nd { get; set; }
}

/// <summary>
/// Снимок раздела принятой редакции.
/// <para>
/// Раздел принимается вместе со структурой: принятая карта показывает ту же
/// группировку, что была согласована. Читать раздел через живую ссылку на
/// рабочий HKCardSection нельзя — принятая редакция воспроизводится из снимка.
/// </para>
/// </summary>
public class HKCardRevisionSection
{
    public Guid Id { get; set; }
    public Guid RevisionId { get; set; }
    public HKCardRevision Revision { get; set; } = null!;

    /// <summary>Тип раздела: Oils, Greases или Fuels.</summary>
    public HKCardSectionType SectionType { get; set; }

    public int SortOrder { get; set; }
}
