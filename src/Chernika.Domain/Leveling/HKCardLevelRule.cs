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
/// справочные связи уровня не определяют. Изменение порядка позиций на результат
/// не влияет — используется только набор уровней верхних позиций.
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
    /// <summary>Вычисленный уровень; Undefined, если не определён.</summary>
    HKObjectLevel CardLevel,

    /// <summary>Статус: определён / пусто / нужен выбор / недопустимо.</summary>
    HKCardLevelStatus Status,

    /// <summary>Требуется ли явный выбор при верхнем узле.</summary>
    bool NeedsNodeRootChoice,

    /// <summary>Ошибки допустимости структуры; пусто при допустимой структуре.</summary>
    IReadOnlyList<string> StructureErrors)
{
    /// <summary>Структура допустима и уровень однозначно определён.</summary>
    public bool IsDetermined => Status is HKCardLevelStatus.Determined;

    /// <summary>Структура допустима (вне зависимости от того, определён ли уровень).</summary>
    public bool IsStructureValid => Status is not HKCardLevelStatus.StructureInvalid;
}

/// <summary>
/// Единое переиспользуемое правило определения уровня ХК по собственной структуре.
/// <para>
/// Доменное правило: не зависит от EF Core, HTTP и Blazor. Получает неизменяемое
/// описание структуры и специальный выбор, возвращает результат со всеми
/// необходимыми различениями. Исключений для нормальных состояний пустого
/// черновика не порождает.
/// </para>
/// </summary>
public static class HKCardLevelRule
{
    /// <summary>
    /// Старшинство уровней: от старшего к младшему. Уровень карты определяется
    /// САМЫМ СТАРШИМ уровнем верхних позиций. Числовые значения enum для
    /// старшинства не используются: они обратны.
    /// </summary>
    private static readonly (HKObjectLevel Level, int Seniority)[] SeniorityMap =
    {
        (HKObjectLevel.Complex, 0),
        (HKObjectLevel.EquipmentModel, 1),
        (HKObjectLevel.Aggregate, 2),
        (HKObjectLevel.Node, 3)
    };

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
        // выбора. Отсутствие выбора — нормальное состояние, недопустимое значение
        // — ошибка.
        var choiceInvalid = nodeRootChoice.HasValue
            && !NodeRootChoices.Contains(nodeRootChoice.Value);

        if (choiceInvalid)
        {
            errors.Add(
                $"Выбор уровня верхнего узла принимает только значения " +
                $"{string.Join(" / ", NodeRootChoices)}.");
        }

        var top = shape.TopPositions;

        // Кандидат уровня определяется по старшему верхнему уровню и от допустимости
        // структуры не зависит: наличие старшей позиции не делает автоматически
        // допустимыми пропуски и связи остальных позиций.
        var candidate = ResolveCandidate(top, shape.RootTerminalRows, nodeRootChoice, out var needsChoice);

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

        // При пустой структуре уровень неопределён, и это не ошибка.
        var level = status is HKCardLevelStatus.Determined
            ? candidate
            : HKObjectLevel.Undefined;

        return new HKCardLevelRuleResult(level, status, needsChoice, errors);
    }

    /// <summary>
    /// Старший уровень верхних позиций дерева.
    /// <para>
    /// null, если верхних позиций нет (пустая структура). Нужен командам, которые
    /// должны проверить фактический верхний уровень по сохранённым данным.
    /// </para>
    /// </summary>
    public static HKObjectLevel? SeniorTopLevel(HKTreeShape shape)
    {
        var top = shape.TopPositions;
        return top.Count == 0 ? null : top.MinBy(p => Seniority(p.Level)).Level;
    }

    /// <summary>
    /// Кандидат уровня по старшему верхнему уровню.
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

        var senior = top.MinBy(p => Seniority(p.Level));

        return senior.Level switch
        {
            HKObjectLevel.Complex => HKObjectLevel.Complex,
            HKObjectLevel.EquipmentModel => HKObjectLevel.EquipmentModel,
            // Верхний агрегат — ХК изделия.
            HKObjectLevel.Aggregate => HKObjectLevel.EquipmentModel,
            // Верхний узел: выбор между агрегатом и изделием. Автоматически
            // агрегат не выбирается.
            HKObjectLevel.Node => nodeRootChoice switch
            {
                HKObjectLevel.Aggregate => HKObjectLevel.Aggregate,
                HKObjectLevel.EquipmentModel => HKObjectLevel.EquipmentModel,
                _ => SetNeedsChoice(out needsChoice)
            },
            _ => HKObjectLevel.Undefined
        };
    }

    private static HKObjectLevel SetNeedsChoice(out bool needsChoice)
    {
        needsChoice = true;
        return HKObjectLevel.Undefined;
    }

    /// <summary>
    /// Старшинство уровня: 0 — старший (комплекс), больше — младше.
    /// </summary>
    private static int Seniority(HKObjectLevel level) =>
        SeniorityMap.FirstOrDefault(m => m.Level == level).Seniority;

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

        var byId = positions.ToDictionary(p => p.Id);

        foreach (var p in positions)
        {
            if (!Enum.IsDefined(p.Level))
            {
                errors.Add($"Позиция {p.Id}: неизвестное значение уровня {(int)p.Level}.");
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
