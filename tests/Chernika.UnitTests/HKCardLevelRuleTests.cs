using Chernika.Domain.Enums;
using Chernika.Domain.Leveling;

namespace Chernika.UnitTests;

/// <summary>
/// Правило определения уровня ХК по собственной структуре.
/// <para>
/// Проверяется чистое доменное правило: без EF, HTTP и БД. Кандидат уровня и
/// допустимость дерева — разные результаты; некорректное описание даёт
/// <c>StructureInvalid</c>, а не исключение.
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

    // ── Таблица определения уровня ──

    [Fact]
    public void EmptyCard_IsUndefinedAndNotAnError()
    {
        var result = HKCardLevelRule.Resolve(Shape(Array.Empty<HKTreePosition>()));

        Assert.Equal(HKCardLevelStatus.Empty, result.Status);
        Assert.Equal(HKObjectLevel.Undefined, result.CardLevel);
        Assert.Empty(result.StructureErrors);
    }

    /// <summary>Только конечные строки под корнем — ХК узла.</summary>
    [Fact]
    public void OnlyTerminalRowsUnderRoot_IsNodeCard()
    {
        var result = HKCardLevelRule.Resolve(
            Shape(Array.Empty<HKTreePosition>(), rootTerminalRows: 3));

        Assert.Equal(HKCardLevelStatus.Determined, result.Status);
        Assert.Equal(HKObjectLevel.Node, result.CardLevel);
    }

    /// <summary>Верхний агрегат — ХК изделия.</summary>
    [Fact]
    public void TopAggregate_IsEquipmentModelCard()
    {
        var result = HKCardLevelRule.Resolve(Shape(new[] { Pos(HKObjectLevel.Aggregate) }));

        Assert.Equal(HKObjectLevel.EquipmentModel, result.CardLevel);
        Assert.Equal(HKCardLevelStatus.Determined, result.Status);
    }

    /// <summary>
    /// Верхнее изделие — ХК комплекса. Это единственный способ получить
    /// Complex: отдельная позиция Complex для этого не нужна.
    /// </summary>
    [Fact]
    public void TopEquipmentModel_IsComplexCard()
    {
        var result = HKCardLevelRule.Resolve(Shape(new[] { Pos(HKObjectLevel.EquipmentModel) }));

        Assert.Equal(HKObjectLevel.Complex, result.CardLevel);
        Assert.Equal(HKCardLevelStatus.Determined, result.Status);
    }

    /// <summary>Изделие с вложенными агрегатами и узлами — тоже ХК комплекса.</summary>
    [Fact]
    public void TopEquipmentModel_WithNestedAggregateAndNode_IsComplexCard()
    {
        var equipment = Guid.NewGuid();
        var aggregate = Guid.NewGuid();

        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.EquipmentModel, id: equipment),
            Pos(HKObjectLevel.Aggregate, parent: equipment, id: aggregate),
            Pos(HKObjectLevel.Node, parent: aggregate),
            Pos(HKObjectLevel.Node, parent: equipment)
        }));

        Assert.True(result.IsStructureValid);
        Assert.Equal(HKObjectLevel.Complex, result.CardLevel);
    }

    /// <summary>
    /// Позиция Complex — дополнительная копия корня и отклоняется понятной
    /// доменной ошибкой.
    /// </summary>
    [Fact]
    public void ComplexPosition_IsRejectedAsRootCopy()
    {
        var result = HKCardLevelRule.Resolve(Shape(new[] { Pos(HKObjectLevel.Complex) }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
        Assert.Contains(result.StructureErrors, e => e.Contains("Complex"));
        Assert.Equal(HKObjectLevel.Undefined, result.CardLevel);
    }

    [Fact]
    public void ComplexPosition_AnywhereInTree_IsRejected()
    {
        var parent = Guid.NewGuid();
        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.EquipmentModel, id: parent),
            Pos(HKObjectLevel.Complex, parent: parent)
        }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
        Assert.Contains(result.StructureErrors, e => e.Contains("Complex"));
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

    /// <summary>
    /// Действующий выбор при верхнем узле: NeedsNodeRootChoice ложно, выбор
    /// применим и даёт уровень.
    /// </summary>
    [Fact]
    public void TopNode_WithChoice_DoesNotRequireChoice()
    {
        var result = HKCardLevelRule.Resolve(
            Shape(new[] { Pos(HKObjectLevel.Node) }), HKObjectLevel.Aggregate);

        Assert.True(result.IsStructureValid);
        Assert.False(result.NeedsNodeRootChoice);
        Assert.Equal(HKCardLevelStatus.Determined, result.Status);
    }

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
    /// порядка вовсе; перестановка набора — тоже.
    /// </summary>
    [Fact]
    public void OrderOfPositions_DoesNotChangeLevel()
    {
        var equipment = Pos(HKObjectLevel.EquipmentModel);
        var aggregate = Pos(HKObjectLevel.Aggregate, parent: equipment.Id);
        var node = Pos(HKObjectLevel.Node, parent: aggregate.Id);

        var forward = HKCardLevelRule.Resolve(Shape(new[] { equipment, aggregate, node }));
        var reversed = HKCardLevelRule.Resolve(Shape(new[] { node, aggregate, equipment }));

        Assert.Equal(forward.CardLevel, reversed.CardLevel);
        Assert.Equal(HKObjectLevel.Complex, forward.CardLevel);
    }

    [Fact]
    public void SeveralTopPositions_AreAnalysedAsWholeSet()
    {
        // Узел и агрегат: старший определяет кандидата, узел не теряется.
        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Node),
            Pos(HKObjectLevel.Aggregate)
        }));

        Assert.Equal(HKObjectLevel.EquipmentModel, result.CardLevel);
        Assert.Equal(HKCardLevelStatus.Determined, result.Status);
    }

    [Fact]
    public void MixedTopLevels_SeniorPositionWins()
    {
        var aggregateAndNode = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Aggregate),
            Pos(HKObjectLevel.Node)
        }));
        Assert.Equal(HKObjectLevel.EquipmentModel, aggregateAndNode.CardLevel);

        // Изделие перебивает и агрегата, и узел.
        var all = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Node),
            Pos(HKObjectLevel.Aggregate),
            Pos(HKObjectLevel.EquipmentModel)
        }));
        Assert.Equal(HKObjectLevel.Complex, all.CardLevel);
    }

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
        var equipment = Guid.NewGuid();
        var equipmentToNode = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.EquipmentModel, id: equipment),
            Pos(HKObjectLevel.Node, parent: equipment)
        }));
        Assert.True(equipmentToNode.IsStructureValid);

        // Агрегат содержит узел.
        var aggregate = Guid.NewGuid();
        var aggregateToNode = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Aggregate, id: aggregate),
            Pos(HKObjectLevel.Node, parent: aggregate)
        }));
        Assert.True(aggregateToNode.IsStructureValid);

        // Изделие содержит агрегат.
        var equipment2 = Guid.NewGuid();
        var equipmentToAggregate = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.EquipmentModel, id: equipment2),
            Pos(HKObjectLevel.Aggregate, parent: equipment2)
        }));
        Assert.True(equipmentToAggregate.IsStructureValid);
    }

    [Fact]
    public void AggregateMayContainNode_AndEquipmentMayContainNodeWithoutAggregate()
    {
        var aggregate = Guid.NewGuid();
        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Aggregate, id: aggregate),
            Pos(HKObjectLevel.Node, parent: aggregate)
        }));
        Assert.True(result.IsStructureValid);
        Assert.Equal(HKObjectLevel.EquipmentModel, result.CardLevel);

        var equipment = Guid.NewGuid();
        var skip = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.EquipmentModel, id: equipment),
            Pos(HKObjectLevel.Node, parent: equipment)
        }));
        Assert.True(skip.IsStructureValid);
        Assert.Equal(HKObjectLevel.Complex, skip.CardLevel);
    }

    // ── Запрещённые связи ──

    [Fact]
    public void MovingUpLevels_IsForbidden()
    {
        var nodeParent = Guid.NewGuid();

        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Node, id: nodeParent),
            Pos(HKObjectLevel.Aggregate, parent: nodeParent)
        }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
        Assert.Contains(result.StructureErrors, e => e.Contains("вверх"));
    }

    [Fact]
    public void MovingFromAggregateToEquipment_IsForbidden()
    {
        var aggregate = Guid.NewGuid();
        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Aggregate, id: aggregate),
            Pos(HKObjectLevel.EquipmentModel, parent: aggregate)
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
        var result = HKCardLevelRule.Resolve(
            Shape(new[] { Pos(HKObjectLevel.Node, parent: id, id: id) }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
    }

    [Fact]
    public void Cycle_IsForbidden()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.EquipmentModel, parent: b, id: a),
            Pos(HKObjectLevel.Aggregate, parent: a, id: b)
        }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
    }

    [Fact]
    public void ParentOutsideTree_IsForbidden()
    {
        var foreignParent = Guid.NewGuid();
        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.EquipmentModel),
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

    // ── Устойчивость описания ──

    /// <summary>
    /// Повторяющиеся идентификаторы — некорректное описание, а не повод падать
    /// исключением словаря.
    /// </summary>
    [Fact]
    public void DuplicateIds_ReturnInvalidInsteadOfThrowing()
    {
        var id = Guid.NewGuid();
        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.Aggregate, id: id),
            Pos(HKObjectLevel.Node, id: id)
        }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
        Assert.Contains(result.StructureErrors, e => e.Contains("дважды"));
    }

    [Fact]
    public void EmptyGuid_IsRejected()
    {
        var result = HKCardLevelRule.Resolve(
            Shape(new[] { Pos(HKObjectLevel.Aggregate, id: Guid.Empty) }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
        Assert.Contains(result.StructureErrors, e => e.Contains("пустым"));
    }

    /// <summary>
    /// Неизвестный уровень не считается старшим: иначе мусор перебил бы
    /// настоящую старшую позицию.
    /// </summary>
    [Fact]
    public void UnknownLevel_DoesNotOutrankEquipmentModel()
    {
        var equipment = Guid.NewGuid();
        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.EquipmentModel, id: equipment),
            Pos((HKObjectLevel)77, parent: equipment)
        }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
    }

    [Fact]
    public void SeniorTopLevel_ReportsSeniorPositionLevel()
    {
        var shape = Shape(new[]
        {
            Pos(HKObjectLevel.Node),
            Pos(HKObjectLevel.EquipmentModel)
        });

        Assert.Equal(HKObjectLevel.EquipmentModel, HKCardLevelRule.SeniorTopLevel(shape));
        Assert.Null(HKCardLevelRule.SeniorTopLevel(
            Shape(Array.Empty<HKTreePosition>())));
    }

    // ── Устаревший выбор ──

    /// <summary>
    /// Сохранённый выбор от прошлого корня-узла не влияет, когда старший
    /// верхний уровень уже другой.
    /// </summary>
    [Fact]
    public void StaleChoice_DoesNotAffectAnotherRoot()
    {
        var result = HKCardLevelRule.Resolve(
            Shape(new[] { Pos(HKObjectLevel.Aggregate) }), HKObjectLevel.Aggregate);

        Assert.Equal(HKObjectLevel.EquipmentModel, result.CardLevel);
        Assert.False(result.NeedsNodeRootChoice);
    }

    // ── Разделение уровня и допустимости ──

    [Fact]
    public void ValidLevelCandidate_DoesNotMakeInvalidTreeValid()
    {
        var parent = Guid.NewGuid();
        var result = HKCardLevelRule.Resolve(Shape(new[]
        {
            Pos(HKObjectLevel.EquipmentModel),
            Pos(HKObjectLevel.Aggregate, id: parent),
            Pos(HKObjectLevel.Aggregate, parent: parent)
        }));

        Assert.Equal(HKCardLevelStatus.StructureInvalid, result.Status);
        Assert.Equal(HKObjectLevel.Undefined, result.CardLevel);
    }

    [Fact]
    public void LevelCandidateAndStructureValidity_AreSeparateResults()
    {
        var needsChoice = HKCardLevelRule.Resolve(Shape(new[] { Pos(HKObjectLevel.Node) }));
        Assert.True(needsChoice.IsStructureValid);
        Assert.True(needsChoice.NeedsNodeRootChoice);
        Assert.False(needsChoice.IsDetermined);

        var determined = HKCardLevelRule.Resolve(
            Shape(new[] { Pos(HKObjectLevel.Node) }), HKObjectLevel.Aggregate);
        Assert.True(determined.IsDetermined);
        Assert.True(determined.IsStructureValid);
    }

    [Fact]
    public void EmptyStructure_DoesNotThrow()
    {
        var result = HKCardLevelRule.Resolve(Shape(Array.Empty<HKTreePosition>(), 0));
        Assert.Equal(HKCardLevelStatus.Empty, result.Status);
    }
}