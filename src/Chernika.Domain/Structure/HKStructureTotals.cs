namespace Chernika.Domain.Structure;

/// <summary>
/// Итог по одной физической величине.
/// <para>
/// Значение хранится в базовой единице и не округляется при сложении:
/// округление выполняется только на границе отображения.
/// </para>
/// <para>
/// Неполный итог — честный сигнал, а не ноль: нераспознанная или несовместимая
/// единица делает результат недостоверным и перечисляется в диагностике.
/// </para>
/// </summary>
public sealed record HKMeasuredTotal(
    HKPhysicalQuantity Quantity,
    decimal BaseValue,
    bool IsComplete,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>Пустой итог без диагностик.</summary>
    public static HKMeasuredTotal Empty(HKPhysicalQuantity quantity) =>
        new(quantity, 0m, true, Array.Empty<string>());
}

/// <summary>Итоги одной позиции: на один экземпляр и вклад всех экземпляров.</summary>
/// <remarks>
/// Различие существенно: количество позиции не должно попадать в собственный
/// итог дважды.
/// </remarks>
public sealed record HKPositionTotals(
    Guid? NodeId,
    Guid? ItemId,
    Guid? SectionId,
    IReadOnlyList<HKMeasuredTotal> PerOne,
    IReadOnlyList<HKMeasuredTotal> TotalInCard);

/// <summary>Итог раздела карты.</summary>
public sealed record HKSectionTotals(
    Guid SectionId,
    IReadOnlyList<HKMeasuredTotal> Totals);

/// <summary>Сводные итоги всей карты.</summary>
public sealed record HKCardTotals(
    IReadOnlyList<HKSectionTotals> Sections,
    HKSectionTotals? WithoutSection,
    IReadOnlyList<HKMeasuredTotal> Overall);

/// <summary>
/// Расчёт массы и объёма по структуре.
/// <para>
/// <c>HKCardItem.Volume</c> — масса или объём ОДНОЙ сборочной единицы. Итог
/// позиции строится только из вложенных конечных строк: одновременно
/// складывать итог родителя и уже включённые в него итоги детей нельзя.
/// </para>
/// <para>
/// Материалы не являются отдельными расходными строками: одна строка учитывается
/// один раз независимо от количества марок и категорий.
/// </para>
/// </summary>
public static class HKStructureTotals
{
    /// <summary>
    /// Считает итоги для каждой позиции, раздела и всей карты.
    /// <para>
    /// Один и тот же механизм принимает и рабочее дерево, и принятую редакцию.
    /// </para>
    /// </summary>
    public static HKCardTotals Calculate(HKStructureInput input)
    {
        var nodesById = input.Nodes.ToDictionary(n => n.Id);
        var sectionById = input.Sections.ToDictionary(s => s.Id);
        var results = CalculatePositionTotals(input);

        var sectionTotals = new List<HKSectionTotals>();
        var withoutSection = new List<HKPositionTotals>();

        foreach (var result in results)
        {
            if (result.SectionId is { } sectionId && sectionById.ContainsKey(sectionId))
            {
                var existing = sectionTotals.FirstOrDefault(s => s.SectionId == sectionId);
                var merged = existing is null
                    ? result.TotalInCard
                    : Merge(existing.Totals, result.TotalInCard);
                sectionTotals.RemoveAll(s => s.SectionId == sectionId);
                sectionTotals.Add(new HKSectionTotals(sectionId, merged));
            }
            else
            {
                withoutSection.Add(result);
            }
        }

        var sectionList = sectionTotals
            .OrderBy(s => sectionById[s.SectionId].SortOrder)
            .ToList();

        var noSectionTotals = Aggregate(withoutSection.Select(r => r.TotalInCard));

        // Союзный агрегат, а не Merge: в левом списке по одному итогу каждой
        // величины на раздел между собой не совпадают по величине — Mass при
        // этом встречается в нескольких разделах и обязан свернуться.
        var all = Aggregate(new[]
        {
            sectionList.SelectMany(s => s.Totals).ToList(), noSectionTotals
        });

        return new HKCardTotals(sectionList, new HKSectionTotals(Guid.Empty, noSectionTotals), all);
    }

    /// <summary>
    /// Итоги каждой верхней ветки: одна строка на самостоятельную позицию
    /// и одну на конечную строку под корнем.
    /// <para>
    /// Отдельный контрактовый расчёт: редактор и HTTP-слой используют его
    /// без агрегации по разделам, а <see cref="Calculate"/> строится поверх
    /// него — формулы заведомо одинаковые.
    /// </para>
    /// </summary>
    public static IReadOnlyList<HKPositionTotals> CalculatePositionTotals(
        HKStructureInput input)
    {
        var nodesById = input.Nodes.ToDictionary(n => n.Id);

        var results = new List<HKPositionTotals>();

        // Конечные строки под корнем — самостоятельные ветки.
        foreach (var item in input.Items.Where(i => i.NodeId is null))
        {
            results.Add(BuildItem(item, input, nodesById));
        }

        // Каждая структурная позиция верхнего уровня — самостоятельная ветка.
        foreach (var node in input.Nodes.Where(n => n.ParentId is null))
        {
            results.Add(BuildNode(node, input, nodesById));
        }

        return results;
    }

    /// <summary>
    /// Итог произвольной структурной позиции — верхней или вложенной: на один
    /// экземпляр позиции и вклад всех её экземпляров в карте.
    /// <para>
    /// Отдельный контракт для редактора: выбранной может быть любая позиция
    /// дерева, а не только её корень.
    /// </para>
    /// </summary>
    public static HKPositionTotals CalculateNode(HKStructureInput input, Guid nodeId)
    {
        var nodesById = input.Nodes.ToDictionary(n => n.Id);
        var node = input.Nodes.FirstOrDefault(n => n.Id == nodeId)
            ?? throw new ArgumentException("Позиция структуры не найдена.", nameof(nodeId));

        return BuildNode(node, input, nodesById);
    }

    /// <summary>
    /// Итог произвольной конечной строки — под корнем или внутри ветки: масса
    /// самой строки и её вклад с учётом количеств позиций на пути к корню.
    /// </summary>
    public static HKPositionTotals CalculateItem(HKStructureInput input, Guid itemId)
    {
        var nodesById = input.Nodes.ToDictionary(n => n.Id);
        var item = input.Items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new ArgumentException("Строка структуры не найдена.", nameof(itemId));

        return BuildItem(item, input, nodesById);
    }

    private static HKPositionTotals BuildItem(
        HKStructureItemInput item, HKStructureInput input,
        Dictionary<Guid, HKStructureNodeInput> nodesById)
    {
        // Одна сборочная единица — это её собственная масса: количество строки
        // и количества родителей в «на один» не входят.
        var perOne = Measure(item);

        // Вклад всех экземпляров: количество самой строки и количества всех её
        // структурных родителей, начиная с непосредственного.
        var multiplier = item.Quantity * MultiplierIncluding(item.NodeId, nodesById);
        var total = Scale(perOne, multiplier);

        return new HKPositionTotals(null, item.Id, item.SectionId, perOne, total);
    }

    private static HKPositionTotals BuildNode(
        HKStructureNodeInput node, HKStructureInput input,
        Dictionary<Guid, HKStructureNodeInput> nodesById)
    {
        // Итог одного экземпляра позиции строится из вложенных конечных строк.
        // Количества позиций СТРОГО ПОД ней входят в «на один», а количество самой
        // позиции — нет: иначе оно умножалось бы дважды.
        var perOne = Aggregate(DescendantItems(node, input).Select(i =>
        {
            var one = Measure(i);
            var below = MultiplierBelow(item: i, node, nodesById);
            return Scale(one, i.Quantity * below);
        }));

        // Вклад всех экземпляров: своё количество и количества строгих
        // предков сверху.
        var total = Scale(perOne, node.Quantity * MultiplierAbove(node.Id, nodesById));

        return new HKPositionTotals(node.Id, null, node.SectionId, perOne, total);
    }

    /// <summary>
    /// Произведение количеств позиций строго ПОД указанной на пути к строке:
    /// сам узел не входит, его количество применяется отдельно.
    /// </summary>
    private static decimal MultiplierBelow(
        HKStructureItemInput item, HKStructureNodeInput node,
        Dictionary<Guid, HKStructureNodeInput> nodesById)
    {
        var multiplier = 1m;
        var current = item.NodeId;

        var guard = 0;
        while (current is { } id && guard++ <= nodesById.Count)
        {
            if (id == node.Id)
                break;

            if (!nodesById.TryGetValue(id, out var step))
                break;

            multiplier *= step.Quantity;

            current = step.ParentId;
        }

        return multiplier;
    }

    /// <summary>
    /// Произведение количеств позиций начиная с указанной и выше. Используется
    /// для конечной строки: её непосредственная позиция входит.
    /// </summary>
    private static decimal MultiplierIncluding(
        Guid? nodeId, Dictionary<Guid, HKStructureNodeInput> nodesById) =>
        Walk(nodeId, nodesById);

    /// <summary>
    /// Произведение количеств строгих предков указанной позиции. Количество
    /// самой позиции применяется отдельно, поэтому она не входит.
    /// </summary>
    private static decimal MultiplierAbove(
        Guid nodeId, Dictionary<Guid, HKStructureNodeInput> nodesById)
    {
        if (!nodesById.TryGetValue(nodeId, out var node))
            return 1m;

        return Walk(node.ParentId, nodesById);
    }

    private static decimal Walk(
        Guid? startId, Dictionary<Guid, HKStructureNodeInput> nodesById)
    {
        var multiplier = 1m;
        var current = startId;
        var guard = 0;

        while (current is { } id && guard++ <= nodesById.Count)
        {
            if (!nodesById.TryGetValue(id, out var step))
                break;

            multiplier *= step.Quantity;
            current = step.ParentId;
        }

        return multiplier;
    }

    /// <summary>Все конечные строки под позицией, включая вложенные ветки.</summary>
    private static IEnumerable<HKStructureItemInput> DescendantItems(
        HKStructureNodeInput node, HKStructureInput input)
    {
        var byParent = input.Nodes
            .Where(n => n.ParentId is not null)
            .GroupBy(n => n.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var itemsByNode = input.Items
            .Where(i => i.NodeId is not null)
            .GroupBy(i => i.NodeId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var stack = new Stack<HKStructureNodeInput>();
        stack.Push(node);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (itemsByNode.TryGetValue(current.Id, out var rows))
            {
                foreach (var row in rows)
                {
                    yield return row;
                }
            }

            if (byParent.TryGetValue(current.Id, out var children))
            {
                foreach (var child in children)
                {
                    stack.Push(child);
                }
            }
        }
    }

    /// <summary>Единичное измерение строки в базовых единицах.</summary>
    private static IReadOnlyList<HKMeasuredTotal> Measure(HKStructureItemInput item)
    {
        if (!HKUnitCatalog.TryResolve(item.UnitOfMeasure, out var unit))
        {
            // Неизвестная единица: итог недостоверен и не приравнивается к нулю.
            var unknown = new HKMeasuredTotal(
                HKPhysicalQuantity.Mass, 0m, false,
                new[]
                {
                    $"Неизвестная единица измерения «{item.UnitOfMeasure}»: итог неполный."
                });

            return new[] { unknown };
        }

        var baseValue = unit.ToBase(item.Volume);

        return new[] { new HKMeasuredTotal(unit.Quantity, baseValue, true, Array.Empty<string>()) };
    }

    private static IReadOnlyList<HKMeasuredTotal> Scale(
        IReadOnlyList<HKMeasuredTotal> totals, decimal multiplier)
    {
        return totals
            .Select(t => t with { BaseValue = t.BaseValue * multiplier })
            .ToList();
    }

    private static IReadOnlyList<HKMeasuredTotal> Aggregate(
        IEnumerable<IReadOnlyList<HKMeasuredTotal>> parts)
    {
        var result = new Dictionary<HKPhysicalQuantity, HKMeasuredTotal>();

        foreach (var part in parts)
        {
            foreach (var total in part)
            {
                if (!result.TryGetValue(total.Quantity, out var existing))
                {
                    result[total.Quantity] = total;
                    continue;
                }

                result[total.Quantity] = MergeOne(existing, total);
            }
        }

        return result.Values.OrderBy(t => t.Quantity).ToList();
    }

    private static IReadOnlyList<HKMeasuredTotal> Merge(
        IReadOnlyList<HKMeasuredTotal> left, IReadOnlyList<HKMeasuredTotal> right)
    {
        // По Quantity может прийти не одна единица одной физической величины
        // (кг и г — обе Mass), поэтому словарь по этому ключу невозможен:
        // дубликат ключа дал бы исключение при объединении разделов.
        var merged = new List<HKMeasuredTotal>(left);

        foreach (var total in right)
        {
            var index = merged.FindIndex(t => t.Quantity == total.Quantity);
            if (index >= 0)
            {
                merged[index] = MergeOne(merged[index], total);
            }
            else
            {
                merged.Add(total);
            }
        }

        return merged.OrderBy(t => t.Quantity).ToList();
    }

    private static HKMeasuredTotal MergeOne(HKMeasuredTotal left, HKMeasuredTotal right)
    {
        // Частичный итог не смешивается с достоверным: результат остаётся
        // неполным, чтобы его нельзя было прочитать как полную сумму.
        if (!left.IsComplete || !right.IsComplete)
        {
            return new HKMeasuredTotal(
                left.Quantity, 0m, false,
                left.Diagnostics.Concat(right.Diagnostics).Distinct().ToList());
        }

        return left with { BaseValue = left.BaseValue + right.BaseValue };
    }
}