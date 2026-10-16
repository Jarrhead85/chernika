using Chernika.Domain.Enums;
using Chernika.Domain.Leveling;

namespace Chernika.UnitTests;

/// <summary>
/// Правило определения уровня ХК по собственной структуре.
/// <para>
/// Проверяется чистое доменное правило: без EF, HTTP и БД. Каждый тест
/// фиксирует одну согласованную сторону — что уровень выводится из
/// структуры, а не из названия, номера, порядка или справочных связей.
/// </para>
/// </summary>
public class HKCardLevelRuleTests
{
    private static HKTreePosition Pos(
        HKObjectLevel level, Guid? parent = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), parent, level);

    private static HKTreeShape Shape(
        IReadOnlyList<HKTreePosition> positions, int rootTerminalRows = 0) =>
        new(positions, rootTerminalRows);

    // ── Базовые случаи определения ──

    [Fact]
    public void EmptyCard_IsUndefinedAndNotAnError()
    {
        var result = HKCardLevelRule.Resolve(Shape(Array.Empty<HKTreePosition>()));

        Assert.Equal(HKCardLevelStatus.Empty, result.Status);
        Assert.Equal(HKObjectLevel.Undefined, result.CardLevel);
        Assert.Empty(result.StructureErrors);
        Assert.False(result.NeedsNodeRootChoice);
        Assert.True(result.IsStructureValid);
    }

    [Fact]
    public void OnlyTerminalRowsUnderRoot_IsNodeCard()
    {
        var result = HKCardLevelRule.Resolve(
            Shape(Array.Empty<HKTreePosition>(), rootTerminalRows: 3));

        Assert.Equal(HKCardLevelStatus.Determined, result.Status);
        Assert.Equal(HKObjectLevel.Node, result.CardLevel);
    }

    [Fact]
    public void TopAggregate_IsEquipmentModelCard()
    {
        var result = HKCardLevelRule.Resolve(Shape(new[] { Pos(HKObjectLevel.Aggregate) }));

        Assert.Equal(HKObjectLevel.EquipmentModel, result.CardLevel);
        Assert.Equal(HKCardLevelStatus.Determined, result.Status);
    }

    [Fact]
    public void TopEquipmentModel_IsEquipmentModelCard()
    {
        var result = HKCardLevelRule.Resolve(Shape(new[] { Pos(HKObjectLevel.EquipmentModel) }));

        Assert.Equal(HKObjectLevel.EquipmentModel, result.CardLevel);
    }

    [Fact]
    public void TopComplex_IsComplexCard()
    {
        var result = HKCardLevelRule.Resolve(Shape(new[] { Pos(HKObjectLevel.Complex) }));

        Assert.Equal(HKObjectLevel.Complex, result.CardLevel);
    }

    // ── Верхний узел: явный выбор ──

    [Fact]
    public void TopNode_WithoutChoice_RequiresChoice()
    {
        var result = HKCardLevelRule.Resolve(Shape(new[] { Pos(HKObjectLevel.Node) }));

        Assert.Equal(HKCardLevelStatus.NodeRootChoiceRequired, result.Status);
        Assert.Equal(HKObjectLevel.Undefined, result.CardLevel);
        Assert.True(result.NeedsNodeRootChoice);
        Assert.Empty(result.StructureErrors);
    }

    [Fact]
    public void TopNode_WithAggregateChoice_IsAggregateCard()
    {
        var result = HKCardLevelRule.Resolve(
            Shape(new[] { Pos(HKObjectLevel.Node) }), HKObjectLevel.Aggregate);

        Assert.Equal(HKObjectLevel.Aggregate, result.CardLevel);
        Assert.False(result.NeedsNodeRootChoice);
    }

    [Fact]
    public void TopNode_WithEquipmentModelChoice_IsEquipmentModelCard()
    {
        var result = HKCardLevelRule.Resolve(
            Shape(new[] { Pos(HKObjectLevel.Node) }), HKObjectLevel.EquipmentModel);

        Assert.Equal(HKObjectLevel.EquipmentModel, result.CardLevel);
    }

    /// <summary>Отсутствие выбора и недопустимое значение выбора — разные вещи.</summary>
    [Fact]
    public void InvalidChoiceValue_IsRejectedAndNotMixedWithMissingChoice()
    {
        var result = HKCardLevelRule.Resolve(
            Shape(new[] { Pos(HKObjectLevel.Node) }), HKObjectLevel.Complex);

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
        Assert.NotEmpty(result.StructureErrors);
        Assert.False(result.NeedsNodeRootChoice);
    }

    [Fact]
    public void UnknownChoiceValue_IsRejected()
    {
        var result = HKCardLevelRule.Resolve(
            Shape(new[] { Pos(HKObjectLevel.Node) }), (HKObjectLevel)99);

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
    }

    // ── Порядок и набор позиций ──

    /// <summary>
    /// Порядок позиций не влияет на уровень. Вход правила не содержит поля
    /// порядка вовсе, поэтому проверить его влияние структурно невозможно;
    /// перестановка набора — тоже.
    /// </summary>
    [Fact]
    public void OrderOfPositions_DoesNotChangeLevel()
    {
        var complex = Pos(HKObjectLevel.Complex);
        var aggregate = Pos(HKObjectLevel.Aggregate);
        var node = Pos(HKObjectLevel.Node, parent: aggregate.Id);

        var forward = HKCardLevelRule.Resolve(Shape(new[] { complex, aggregate, node }));
        var reversed = HKCardLevelRule.Resolve(Shape(new[] { node, aggregate, complex }));

        Assert.Equal(forward.CardLevel, reversed.CardLevel);
        Assert.Equal(HKObjectLevel.Complex, forward.CardLevel);
    }

    [Fact]
    public void SeveralTopPositions_AreAnalysedAsWholeSet()
    {
        // Узел и комплекс: старший определяет кандидата, узел не теряется.
        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Node),
            Pos(HKObjectLevel.Complex)
        }));

        Assert.Equal(HKObjectLevel.Complex, result.CardLevel);
        Assert.Equal(HKCardLevelStatus.Determined, result.Status);
    }

    /// <summary>
    /// Старший уровень берётся из всего набора верхних позиций, а не из первой
    /// запрошенной: агрегат и узел вместе дают уровень узла с требованием выбора,
    /// но Complex в наборе перебивает обоих.
    /// </summary>
    [Fact]
    public void MixedTopLevels_SeniorPositionWins()
    {
        var withAggregateAndNode = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Aggregate),
            Pos(HKObjectLevel.Node)
        }));
        Assert.Equal(HKObjectLevel.EquipmentModel, withAggregateAndNode.CardLevel);

        var withNodeAggregateEquipment = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Node),
            Pos(HKObjectLevel.Aggregate),
            Pos(HKObjectLevel.EquipmentModel)
        }));
        Assert.Equal(HKObjectLevel.EquipmentModel, withNodeAggregateEquipment.CardLevel);
    }

    /// <summary>Одноимённые позиции не объединяются: каждая учитывается сама по себе.</summary>
    [Fact]
    public void SeveralPositions_AreNotCollapsed()
    {
        var single = HKCardLevelRule.Resolve(Shape(new[] { Pos(HKObjectLevel.Aggregate) }));
        var twoPositions = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Aggregate),
            Pos(HKObjectLevel.Aggregate)
        }));

        Assert.Equal(single.CardLevel, twoPositions.CardLevel);
        Assert.Equal(HKCardLevelStatus.Determined, twoPositions.Status);
    }

    // ── Допустимые пропуски ──

    [Fact]
    public void AllowedSkips_AreAccepted()
    {
        // Изделие содержит узел без агрегата.
        var complexToNode = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Complex),
            Pos(HKObjectLevel.Node, parent: null)
        }));
        Assert.True(complexToNode.IsStructureValid);

        // Агрегат содержит узел.
        var aggregateToNode = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Aggregate),
            Pos(HKObjectLevel.Node)
        }));
        Assert.True(aggregateToNode.IsStructureValid);

        // Изделие содержит агрегат.
        var equipmentToAggregate = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.EquipmentModel),
            Pos(HKObjectLevel.Aggregate)
        }));
        Assert.True(equipmentToAggregate.IsStructureValid);
    }

    [Fact]
    public void AggregateMayContainNode_AndEquipmentMayContainNodeWithoutAggregate()
    {
        // Агрегат может содержать узел.
        var aggregate = Guid.NewGuid();
        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Aggregate, id: aggregate),
            Pos(HKObjectLevel.Node, parent: aggregate)
        }));
        Assert.True(result.IsStructureValid);
        Assert.Equal(HKObjectLevel.EquipmentModel, result.CardLevel);

        // Изделие может содержать узел без агрегата.
        var equipment = Guid.NewGuid();
        var skip = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.EquipmentModel, id: equipment),
            Pos(HKObjectLevel.Node, parent: equipment)
        }));
        Assert.True(skip.IsStructureValid);
    }

    // ── Запрещённые связи ──

    [Fact]
    public void MovingUpLevels_IsForbidden()
    {
        var nodeParent = Guid.NewGuid();

        // Узел не может содержать агрегат: это движение вверх.
        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Node, id: nodeParent),
            Pos(HKObjectLevel.Aggregate, parent: nodeParent)
        }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
        Assert.Contains(result.StructureErrors, e => e.Contains("вверх"));
    }

    [Fact]
    public void SameLevelParentAndChild_IsForbidden()
    {
        var parent = Guid.NewGuid();
        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Aggregate, id: parent),
            Pos(HKObjectLevel.Aggregate, parent: parent)
        }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
    }

    [Fact]
    public void SelfReference_IsForbidden()
    {
        var id = Guid.NewGuid();
        var result = HKCardLevelRule.Resolve(Shape(new[] { Pos(HKObjectLevel.Node, parent: id, id: id) }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
    }

    [Fact]
    public void Cycle_IsForbidden()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Complex, parent: b, id: a),
            Pos(HKObjectLevel.Aggregate, parent: a, id: b)
        }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
    }

    [Fact]
    public void ParentOutsideTree_IsForbidden()
    {
        // Родитель принадлежит другой карте — в наборе этой карты его нет.
        var foreignParent = Guid.NewGuid();
        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Complex),
            Pos(HKObjectLevel.Aggregate, parent: foreignParent)
        }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
        Assert.Contains(result.StructureErrors, e => e.Contains("отсутствует"));
    }

    [Fact]
    public void UndefinedLevelAtPosition_IsForbidden()
    {
        var result = HKCardLevelRule.Resolve(Shape(new[] { Pos(HKObjectLevel.Undefined) }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
        Assert.Contains(result.StructureErrors, e => e.Contains("Undefined"));
    }

    [Fact]
    public void UnknownLevelAtPosition_IsForbidden()
    {
        var result = HKCardLevelRule.Resolve(Shape(new[] { Pos((HKObjectLevel)77) }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
        Assert.Contains(result.StructureErrors, e => e.Contains("неизвестное"));
    }

    // ── Устаревший выбор ──

    /// <summary>
    /// Сохранённый выбор от прошлого корня-узла не влияет, когда старший
    /// верхний уровень уже другой.
    /// </summary>
    [Fact]
    public void StaleChoice_DoesNotAffectAnotherRoot()
    {
        // Старший верхний уровень — агрегат, выбор от узла остался в базе.
        var result = HKCardLevelRule.Resolve(
            Shape(new[] { Pos(HKObjectLevel.Aggregate) }), HKObjectLevel.Aggregate);

        Assert.Equal(HKObjectLevel.EquipmentModel, result.CardLevel);
        Assert.False(result.NeedsNodeRootChoice);
    }

    [Fact]
    public void StaleChoice_DoesNotTurnNodeIntoAggregateAutomatically()
    {
        // Выбор EquipmentModel от прошлого узла и новый узел без нового выбора:
        // выбор применяется, но Aggregate автоматически не подставляется.
        var result = HKCardLevelRule.Resolve(
            Shape(new[] { Pos(HKObjectLevel.Node) }), HKObjectLevel.EquipmentModel);

        Assert.Equal(HKObjectLevel.EquipmentModel, result.CardLevel);
    }

    // ── Разделение уровня и допустимости ──

    /// <summary>
    /// Вычисленный кандидат уровня не делает некорректное дерево допустимым.
    /// </summary>
    [Fact]
    public void ValidLevelCandidate_DoesNotMakeInvalidTreeValid()
    {
        var nodeParent = Guid.NewGuid();
        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Complex),
            Pos(HKObjectLevel.Complex, parent: nodeParent, id: nodeParent)
        }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
        Assert.Equal(HKObjectLevel.Undefined, result.CardLevel);
    }

    /// <summary>Кандидат уровня и допустимость — разные результаты.</summary>
    [Fact]
    public void LevelCandidateAndStructureValidity_AreSeparateResults()
    {
        // Старшая позиция — узел: кандидат уровня есть, но без выбора он не определён.
        var needsChoice = HKCardLevelRule.Resolve(Shape(new[] { Pos(HKObjectLevel.Node) }));
        Assert.True(needsChoice.IsStructureValid);
        Assert.True(needsChoice.NeedsNodeRootChoice);

        // Та же структура с выбором: уровень определён.
        var determined = HKCardLevelRule.Resolve(
            Shape(new[] { Pos(HKObjectLevel.Node) }), HKObjectLevel.Aggregate);
        Assert.True(determined.IsDetermined);
    }

    /// <summary>Пустая структура — нормальное состояние черновика, не исключение.</summary>
    [Fact]
    public void EmptyStructure_DoesNotThrow()
    {
        var result = HKCardLevelRule.Resolve(Shape(Array.Empty<HKTreePosition>(), 0));
        Assert.Equal(HKCardLevelStatus.Empty, result.Status);
    }
}