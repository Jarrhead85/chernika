using Chernika.Domain.Enums;

namespace Chernika.Domain.Leveling;

/// <summary>
/// Позиция собственного дерева карты.
/// <para>
/// Верхняя позиция — позиция с <see cref="ParentId"/>, равным null. Конечная
/// строка позицией не является: это строка карты с пустым узлом.
/// </para>
/// </summary>
public sealed record HKTreePosition(Guid Id, Guid? ParentId, HKObjectLevel Level);

/// <summary>
/// Неизменяемое описание собственной структуры карты.
/// <para>
/// Уровень выводится ТОЛЬКО из этого описания: название, номер, порядок и
/// справочные связи уровня не определяют. Изменение порядка позиций на
/// результат не влияет — используется только набор уровней верхних позиций.
/// </para>
/// </summary>
public sealed record HKTreeShape(
    IReadOnlyList<HKTreePosition> Positions,
    int RootTerminalRows)
{
    /// <summary>Верхние позиции — позиции с пустым родителем.</summary>
    public IReadOnlyList<HKTreePosition> TopPositions =>
        Positions.Where(p => p.ParentId is null).ToList();
}

/// <summary>Итог применения правила уровня.</summary>
public enum HKCardLevelStatus
{
    /// <summary>Уровень однозначно определён структурой.</summary>
    Determined,

    /// <summary>Структурных позиций и конечных строк под корнем нет.</summary>
    Empty,

    /// <summary>Старшая верхняя позиция — узел: нужен явный пользовательский выбор.</summary>
    NodeRootChoiceRequired,

    /// <summary>Структура недопустима.</summary>
    StructureInvalid
}

/// <summary>Результат определения уровня карты.</summary>
public sealed record HKCardLevelRuleResult(
    HKObjectLevel CardLevel,
    HKCardLevelStatus Status,
    bool NeedsNodeRootChoice,
    IReadOnlyList<string> StructureErrors)
{
    /// <summary>Уровень однозначно определён.</summary>
    public bool IsDetermined => Status is HKCardLevelStatus.Determined;

    /// <summary>Структура допустима (вне зависимости от того, определён ли уровень).</summary>
    public bool IsStructureValid => Status is not HKCardLevelStatus.StructureInvalid;
}

/// <summary>
/// Единое переиспользуемое правило определения уровня ХК по собственной структуре.
/// <para>
/// Доменное правило: не зависит от EF Core, HTTP и Blazor. Некорректное
/// описание даёт <see cref="HKCardLevelStatus.StructureInvalid"/>, а не
/// случайное исключение. Нормальные состояния черновика исключениями не
/// являются.
/// </para>
/// </summary>
public static class HKCardLevelRule
{
    /// <summary>
    /// Уровни, допустимые у структурной позиции дерева, в порядке старшинства:
    /// от старшего к младшему.
    /// <para>
    /// <see cref="HKObjectLevel.Complex"/> здесь отсутствует намеренно:
    /// Complex — уровень самой ХК-корня, а не структурной позиции. Позиция
    /// Complex отклоняется как дополнительная копия корня.
    /// </para>
    /// </summary>

    /// <summary>Допустимые значения специального выбора при верхнем узле.</summary>
    private static readonly HKObjectLevel[] NodeRootChoices =
    {
        HKObjectLevel.Aggregate,
        HKObjectLevel.EquipmentModel
    };

    /// <summary>
    /// Применяет правило.
    /// </summary>
    /// <param name="shape">Собственная структура карты.</param>
    /// <param name="nodeRootChoice">
    /// Сохранённый пользовательский выбор для верхнего узла. Допустимы
    /// <see cref="HKObjectLevel.Aggregate"/>, <see cref="HKObjectLevel.EquipmentModel"/>
    /// или null. Прочие значения считаются ошибкой.
    /// </param>
    public static HKCardLevelRuleResult Resolve(
        HKTreeShape shape, HKObjectLevel? nodeRootChoice = null)
    {
        var errors = ValidateStructure(shape);

        // Недопустимый выбор — отдельная ошибка: его не смешивают с отсутствием
        // выбора. Отсутствие выбора — нормальное состояние.
        var choiceInvalid = nodeRootChoice.HasValue
            && !NodeRootChoices.Contains(nodeRootChoice.Value);

        if (choiceInvalid)
        {
            errors.Add(
                "Выбор уровня верхнего узла принимает только значения " +
                $"{string.Join(" / ", NodeRootChoices)}.");
        }

        var top = shape.TopPositions;

        // Кандидат уровня определяется по старшему верхнему уровню и от
        // допустимости структуры не зависит: наличие старшей позиции не делает
        // допустимыми пропуски и связи остальных позиций.
        var candidate = ResolveCandidate(top, shape.RootTerminalRows, nodeRootChoice,
            out var needsChoice);

        // Непригодное значение выбора — это ошибка, а не требование выбора.
        if (choiceInvalid)
            needsChoice = false;

        // Пустой картой считается только полное отсутствие структуры: конечные
        // строки под корнем без структурных позиций дают уровень узла.
        var isEmpty = top.Count == 0 && shape.RootTerminalRows == 0;

        var status = errors.Count > 0
            ? HKCardLevelStatus.StructureInvalid
            : isEmpty
                ? HKCardLevelStatus.Empty
                : needsChoice
                    ? HKCardLevelStatus.NodeRootChoiceRequired
                    : HKCardLevelStatus.Determined;

        var level = status is HKCardLevelStatus.Determined
            ? candidate
            : HKObjectLevel.Undefined;

        return new HKCardLevelRuleResult(level, status, needsChoice, errors);
    }

    /// <summary>
    /// Кандидат уровня карты по старшей верхней позиции.
    /// <para>
    /// Уровень карты на ступень выше уровня своей старшей верхней позиции:
    /// верхнее изделие даёт ХК комплекса, верхний агрегат — ХК изделия.
    /// Исключение — верхний узел: между ним и изделием два допустимых уровня,
    /// поэтому нужен явный выбор.
    /// </para>
    /// </summary>
    private static HKObjectLevel ResolveCandidate(
        IReadOnlyList<HKTreePosition> top, int rootTerminalRows,
        HKObjectLevel? nodeRootChoice, out bool needsChoice)
    {
        needsChoice = false;

        if (top.Count == 0)
        {
            // Нет структурных позиций: конечные строки под корнем дают уровень
            // узла, полное отсутствие структуры — неопределённость.
            return rootTerminalRows > 0 ? HKObjectLevel.Node : HKObjectLevel.Undefined;
        }

        // top.Count > 0, поэтому MinBy возвращает позицию; проверка пустоты
        // оставлена явно, чтобы компилятор не требовал заглушек.
        var senior = top.MinBy(p => Seniority(p.Level))
            ?? throw new ArgumentException("Верхние позиции отсутствуют.", nameof(top));

        return senior.Level switch
        {
            // Верхнее изделие → ХК комплекса.
            HKObjectLevel.EquipmentModel => HKObjectLevel.Complex,
            // Верхний агрегат → ХК изделия.
            HKObjectLevel.Aggregate => HKObjectLevel.EquipmentModel,
            // Верхний узел: выбор между агрегатом и изделием. Автоматически
            // агрегат не выбирается.
            HKObjectLevel.Node => nodeRootChoice switch
            {
                HKObjectLevel.Aggregate => HKObjectLevel.Aggregate,
                HKObjectLevel.EquipmentModel => HKObjectLevel.EquipmentModel,
                _ => SetNeedsChoice(out needsChoice)
            },
            // Complex в дереве недопустим и отсекается проверкой структуры.
            _ => HKObjectLevel.Undefined
        };
    }

    private static HKObjectLevel SetNeedsChoice(out bool needsChoice)
    {
        needsChoice = true;
        return HKObjectLevel.Undefined;
    }

    /// <summary>
    /// Старший уровень верхних позиций дерева.
    /// <para>
    /// null, если верхних позиций нет. Нужен командам, которые проверяют
    /// фактический верхний уровень по сохранённым данным.
    /// </para>
    /// </summary>
    public static HKObjectLevel? SeniorTopLevel(HKTreeShape shape)
    {
        var top = shape.TopPositions;
        if (top.Count == 0)
            return null;

        var senior = top.MinBy(p => Seniority(p.Level));
        return senior is null ? null : senior.Level;
    }

    /// <summary>
    /// Старшинство уровня позиции: меньше — старше.
    /// <para>
    /// Неизвестный уровень НЕ считается старшим: он получает
    /// <see cref="int.MaxValue"/> и отсекается проверкой структуры. Значение по
    /// умолчанию для кортежа ранее молча делало мусор самым старшим.
    /// </para>
    /// </summary>
    private static int Seniority(HKObjectLevel level) =>
        SeniorityMap.TryGetValue(level, out var seniority)
            ? seniority
            : int.MaxValue;

    private static readonly Dictionary<HKObjectLevel, int> SeniorityMap =
        new()
        {
            [HKObjectLevel.EquipmentModel] = 0,
            [HKObjectLevel.Aggregate] = 1,
            [HKObjectLevel.Node] = 2
        };

    /// <summary>
    /// Допустимость самого дерева. Отдельно от уровня: вычисленный кандидат не
    /// делает некорректное дерево допустимым.
    /// </summary>
    private static List<string> ValidateStructure(HKTreeShape shape)
    {
        var errors = new List<string>();
        var positions = shape.Positions;

        if (positions.Count == 0)
            return errors;

        // Повторяющиеся идентификаторы — ошибка описания, а не исключение
        // словаря: ToDictionary на дублях падал бы случайно.
        var byId = new Dictionary<Guid, HKTreePosition>();
        foreach (var p in positions)
        {
            if (p.Id == Guid.Empty)
            {
                errors.Add("Позиция: идентификатор не должен быть пустым.");
                continue;
            }

            if (!byId.TryAdd(p.Id, p))
            {
                errors.Add($"Позиция {p.Id}: идентификатор встречается в наборе дважды.");
            }
        }

        foreach (var p in positions)
        {
            if (!Enum.IsDefined(p.Level))
            {
                errors.Add($"Позиция {p.Id}: неизвестное значение уровня {(int)p.Level}.");
                continue;
            }

            // Complex — уровень самой ХК-корня. Дополнительная копия корня в
            // собственном дереве не нужна и отклоняется.
            if (p.Level == HKObjectLevel.Complex)
            {
                errors.Add(
                    $"Позиция {p.Id}: уровень Complex недопустим в собственном дереве — " +
                    "это уровень самой карты, а не её структурной позиции.");
                continue;
            }

            if (p.Level == HKObjectLevel.Undefined)
            {
                errors.Add($"Позиция {p.Id}: уровень не указан (Undefined).");
                continue;
            }

            // Самоссылка.
            if (p.ParentId == p.Id)
            {
                errors.Add($"Позиция {p.Id}: позиция не может быть родителем самой себе.");
                continue;
            }

            // Родитель должен присутствовать в дереве этой карты.
            HKTreePosition? parent = null;
            if (p.ParentId.HasValue)
            {
                if (!byId.TryGetValue(p.ParentId.Value, out var found))
                {
                    errors.Add($"Позиция {p.Id}: родитель {p.ParentId.Value} отсутствует в дереве карты.");
                    continue;
                }

                parent = found;
            }

            // Движение вверх по уровням запрещено: потомок должен быть строго
            // младше родителя.
            if (parent is not null && Seniority(p.Level) <= Seniority(parent.Level))
            {
                errors.Add(
                    $"Позиция {p.Id}: уровень {p.Level} не может содержаться в позиции " +
                    $"уровня {parent.Level} — движение вверх по уровням запрещено.");
                continue;
            }

            // Циклы: поднимаемся по родителям.
            var visited = new HashSet<Guid> { p.Id };
            var current = p.ParentId;
            var guard = 0;
            while (current.HasValue && guard++ <= positions.Count)
            {
                if (current.Value == p.Id || !visited.Add(current.Value))
                {
                    errors.Add($"Позиция {p.Id}: обнаружен цикл в дереве.");
                    break;
                }

                if (!byId.TryGetValue(current.Value, out var next))
                    break;

                current = next.ParentId;
            }
        }

        return errors;
    }
}