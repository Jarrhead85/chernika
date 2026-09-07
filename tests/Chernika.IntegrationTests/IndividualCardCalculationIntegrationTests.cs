using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class IndividualCardCalculationIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public IndividualCardCalculationIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private TestScope Scope() => _fixture.CreateScope();

    private void SetUser(TestScope s, ApplicationUser user) =>
        s.User.CurrentUserId = Guid.Parse(user.Id);

    private async Task GrantAsync(TestScope s, ApplicationUser user, string code)
    {
        var existing = await s.Db.UserPermissionOverrides
            .Where(o => o.UserId == user.Id && o.PermissionCode == code)
            .ToListAsync();
        s.Db.UserPermissionOverrides.RemoveRange(existing);
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(), UserId = user.Id, PermissionCode = code,
            IsGranted = true, Reason = "Test", GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(user.Id);
    }

    private async Task DenyAsync(TestScope s, ApplicationUser user, string code)
    {
        var existing = await s.Db.UserPermissionOverrides
            .Where(o => o.UserId == user.Id && o.PermissionCode == code)
            .ToListAsync();
        s.Db.UserPermissionOverrides.RemoveRange(existing);
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(), UserId = user.Id, PermissionCode = code,
            IsGranted = false, Reason = "Test deny", GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(user.Id);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    private static async Task<ApplicationUser> CreateUserAsync(
        TestScope s, string role, Guid branchId)
    {
        var login = role.ToLowerInvariant() + "_" + Guid.NewGuid().ToString("N")[..8];
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = login,
            FullName = "Тест " + login,
            BranchId = branchId,
            IsActive = true,
        };
        var result = await s.Users.CreateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                "Создание тестового пользователя не удалось: " + string.Join("; ", result.Errors.Select(e => e.Description)));
        await s.Users.AddToRoleAsync(user, role);
        return user;
    }

    private async Task<int> CountAuditsAsync(TestScope s, Guid entityId, string action) =>
        await s.Db.AuditLogs.CountAsync(a =>
            a.EntityType == "IndividualCard" && a.EntityId == entityId.ToString() && a.Action == action);

    // ── Reference data helpers ────────────────────────────────────────────

    private async Task<Guid> CreateNodeAsync(TestScope s, string? code = null)
    {
        var node = new Node { Id = Guid.NewGuid(), Code = code ?? "N-" + Suffix(), Name = "Узел " + Suffix(), IsDeleted = false };
        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();
        return node.Id;
    }

    private async Task<Guid> CreateAggregateAsync(TestScope s)
    {
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Suffix(), Name = "Агрегат " + Suffix(), IsDeleted = false };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();
        return aggregate.Id;
    }

    private async Task<(Guid ModelId, Guid InstanceId)> CreateEquipmentAsync(TestScope s)
    {
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "EM-" + Suffix(), Name = "Изделие " + Suffix(), IsDeleted = false };
        s.Db.EquipmentModels.Add(model);
        var instance = new EquipmentInstance { Id = Guid.NewGuid(), SerialNumber = "SN-" + Suffix(), Index = model.Index, Name = "Экземпляр", EquipmentModelId = model.Id, IsDeleted = false };
        s.Db.EquipmentInstances.Add(instance);
        await s.Db.SaveChangesAsync();
        return (model.Id, instance.Id);
    }

    private async Task<Guid> CreateComplexAsync(TestScope s)
    {
        var complex = new Complex { Id = Guid.NewGuid(), Code = "C-" + Suffix(), Name = "Комплекс " + Suffix(), IsDeleted = false };
        s.Db.Complexes.Add(complex);
        await s.Db.SaveChangesAsync();
        return complex.Id;
    }

    private async Task<Guid> CreateAssemblyUnitAsync(TestScope s)
    {
        var au = new AssemblyUnit { Id = Guid.NewGuid(), Code = "AU-" + Suffix(), Name = "СЕ " + Suffix(), IsDeleted = false };
        s.Db.AssemblyUnits.Add(au);
        await s.Db.SaveChangesAsync();
        return au.Id;
    }

    private async Task<Guid> CreateGsmMaterialAsync(TestScope s, string? name = null, string? gost = null)
    {
        var material = new GsmMaterial
        {
            Id = Guid.NewGuid(),
            Name = name ?? "ГСМ " + Suffix(),
            Type = "Тип " + Suffix(),
            Gost = gost,
            IsDeleted = false,
            IsDraft = false,
        };
        s.Db.GsmMaterials.Add(material);
        await s.Db.SaveChangesAsync();
        return material.Id;
    }

    private async Task<HKCard> CreateHKAsync(
        TestScope s, IndividualCardObjectLevel level, Guid objectId, Guid branchId,
        HKCardStatus status = HKCardStatus.Approved, string? code = null, string? version = null)
    {
        var hk = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = code ?? ("HK-" + level.ToString()[..3] + "-" + Suffix()),
            Version = version ?? ("v" + Suffix()[..4]),
            Status = status,
            ObjectLevel = level switch
            {
                IndividualCardObjectLevel.Complex => HKObjectLevel.Complex,
                IndividualCardObjectLevel.EquipmentModel => HKObjectLevel.EquipmentModel,
                IndividualCardObjectLevel.Aggregate => HKObjectLevel.Aggregate,
                IndividualCardObjectLevel.Node => HKObjectLevel.Node,
                _ => throw new ArgumentOutOfRangeException(nameof(level)),
            },
            BranchId = branchId,
            ApprovedDate = status == HKCardStatus.Approved ? DateTime.UtcNow : null,
        };
        switch (level)
        {
            case IndividualCardObjectLevel.Complex: hk.ComplexId = objectId; break;
            case IndividualCardObjectLevel.EquipmentModel: hk.EquipmentModelId = objectId; break;
            case IndividualCardObjectLevel.Aggregate: hk.AggregateId = objectId; break;
            case IndividualCardObjectLevel.Node: hk.NodeId = objectId; break;
        }
        s.Db.HKCards.Add(hk);
        await s.Db.SaveChangesAsync();
        return hk;
    }

    private async Task AddComponentAsync(TestScope s, HKCard parent, HKCard child, int sortOrder = 1)
    {
        s.Db.HKCardComponents.Add(new HKCardComponent
        {
            Id = Guid.NewGuid(),
            ParentHKCardId = parent.Id,
            ChildHKCardId = child.Id,
            SortOrder = sortOrder,
            AddedAt = DateTime.UtcNow,
            AddedByUserId = _fixture.SystemAdminUser.Id,
            ChildCode = child.Code,
            ChildVersion = child.Version,
            ChildApprovedAt = child.ApprovedDate,
        });
        await s.Db.SaveChangesAsync();
    }

    /// <summary>Adds HKCardItem rows (with GSM material alternatives) to a node HK.</summary>
    private async Task AddNodeItemsAsync(
        TestScope s, Guid nodeHKCardId,
        params (Guid AssemblyUnitId, int Quantity, decimal Volume, string Unit, (Guid GsmId, GsmCategory Category)[] Materials)[] items)
    {
        foreach (var (auId, quantity, volume, unit, materials) in items)
        {
            var item = new HKCardItem
            {
                Id = Guid.NewGuid(),
                HKCardId = nodeHKCardId,
                AssemblyUnitId = auId,
                Quantity = quantity,
                Volume = volume,
                UnitOfMeasure = unit,
                SortOrder = 1,
            };
            foreach (var (gsmId, category) in materials)
            {
                item.Materials.Add(new HKCardItemMaterial
                {
                    Id = Guid.NewGuid(),
                    HKCardItemId = item.Id,
                    GsmMaterialId = gsmId,
                    Category = category,
                });
            }
            s.Db.HKCardItems.Add(item);
        }
        await s.Db.SaveChangesAsync();
    }

    private async Task<Guid> CreateProductCompositionAsync(TestScope s, Guid modelId, params (Guid AggregateId, int Quantity)[] aggregates)
    {
        var pc = new ProductComposition
        {
            Id = Guid.NewGuid(),
            EquipmentModelId = modelId,
            Version = "v" + Suffix()[..4],
            Status = ProductCompositionStatus.Approved,
            IsActive = true,
            ApprovedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        s.Db.ProductCompositions.Add(pc);
        for (var i = 0; i < aggregates.Length; i++)
        {
            s.Db.ProductCompositionAggregates.Add(new ProductCompositionAggregate
            {
                Id = Guid.NewGuid(),
                ProductCompositionId = pc.Id,
                AggregateId = aggregates[i].AggregateId,
                Quantity = aggregates[i].Quantity,
                SortOrder = i + 1,
            });
        }
        await s.Db.SaveChangesAsync();
        return pc.Id;
    }

    private async Task<Guid> CreateAggregateCompositionAsync(TestScope s, Guid aggregateId, params (Guid NodeId, int Quantity)[] nodes)
    {
        var ac = new AggregateComposition
        {
            Id = Guid.NewGuid(),
            AggregateId = aggregateId,
            Version = "v" + Suffix()[..4],
            Status = ProductCompositionStatus.Approved,
            IsActive = true,
            ApprovedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        s.Db.AggregateCompositions.Add(ac);
        for (var i = 0; i < nodes.Length; i++)
        {
            s.Db.AggregateCompositionNodes.Add(new AggregateCompositionNode
            {
                Id = Guid.NewGuid(),
                AggregateCompositionId = ac.Id,
                NodeId = nodes[i].NodeId,
                Quantity = nodes[i].Quantity,
                SortOrder = i + 1,
            });
        }
        await s.Db.SaveChangesAsync();
        return ac.Id;
    }

    private async Task<Guid> CreateComplexCompositionAsync(TestScope s, Guid complexId, params (Guid EquipmentModelId, int Quantity)[] items)
    {
        var cc = new ComplexComposition
        {
            Id = Guid.NewGuid(),
            ComplexId = complexId,
            Version = "v" + Suffix()[..4],
            Status = ProductCompositionStatus.Approved,
            IsActive = true,
            ApprovedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        s.Db.ComplexCompositions.Add(cc);
        for (var i = 0; i < items.Length; i++)
        {
            s.Db.ComplexCompositionItems.Add(new ComplexCompositionItem
            {
                Id = Guid.NewGuid(),
                ComplexCompositionId = cc.Id,
                EquipmentModelId = items[i].EquipmentModelId,
                Quantity = items[i].Quantity,
                SortOrder = i + 1,
            });
        }
        await s.Db.SaveChangesAsync();
        return cc.Id;
    }

    /// <summary>Full Изделие chain: model → aggregate → node with HK items.</summary>
    private async Task<(Guid ModelId, Guid AggregateId, Guid NodeId, Guid NodeHKCardId, Guid AssemblyUnitId)>
        CreateModelChainAsync(
            TestScope s, string unit = "г", decimal volume = 100m, int hkItemQuantity = 1,
            int pcAggregateQuantity = 1, int acNodeQuantity = 1,
            params (Guid GsmId, GsmCategory Category)[] materials)
    {
        var (modelId, _) = await CreateEquipmentAsync(s);
        var aggregateId = await CreateAggregateAsync(s);
        var nodeId = await CreateNodeAsync(s);
        var assemblyUnitId = await CreateAssemblyUnitAsync(s);
        await CreateProductCompositionAsync(s, modelId, (aggregateId, pcAggregateQuantity));
        await CreateAggregateCompositionAsync(s, aggregateId, (nodeId, acNodeQuantity));
        var modelHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        await AddComponentAsync(s, modelHK, aggregateHK);
        await AddComponentAsync(s, aggregateHK, nodeHK);

        // A complete chain needs a Primary material; when the caller does not
        // specify materials, a working Primary GSM is created implicitly.
        var effectiveMaterials = materials.Length > 0
            ? materials
            : new[] { (await CreateGsmMaterialAsync(s, gost: "ГОСТ-" + Suffix()), GsmCategory.Primary) };
        await AddNodeItemsAsync(s, nodeHK.Id,
            (assemblyUnitId, hkItemQuantity, volume, unit, effectiveMaterials));
        return (modelId, aggregateId, nodeId, nodeHK.Id, assemblyUnitId);
    }

    private async Task<IndividualCardCalculationDto> CreateAndCalculateNodeDraftAsync(
        TestScope s, decimal volume, params Guid[] coefficientIds)
    {
        var nodeId = await CreateNodeAsync(s);
        var auId = await CreateAssemblyUnitAsync(s);
        var primary = await CreateGsmMaterialAsync(s, gost: "ГОСТ-" + Suffix());
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        await AddNodeItemsAsync(s, nodeHK.Id, (auId, 1, volume, "г", new[] { (primary, GsmCategory.Primary) }));

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));
        return await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, coefficientIds));
    }

    // ── 1–2: coefficients ─────────────────────────────────────────────────

    [Fact]
    public async Task Recalculate_EmptySelection_TotalCoefficientOne()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var calculation = await CreateAndCalculateNodeDraftAsync(s, 100m);

        Assert.Equal(1.000000m, calculation.TotalCoefficient);
        var row = Assert.Single(calculation.Rows);
        Assert.Equal(100m, row.BaseVolume);
        Assert.Equal(100m, row.CalculatedVolume);
        Assert.Equal(100m, calculation.TotalNorm);
        Assert.True(calculation.IsReadyToForm);
    }

    [Fact]
    public async Task Recalculate_CoefficientsMultiplyWithDecimalPrecision()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var typeId = await CreateCoefficientTypeAsync(s, "Климатический-" + Suffix());
        var k1 = await CreateCoefficientAsync(s, typeId, "Зимняя эксплуатация", 1.100000m, "Температура ниже −20 °C");

        var typeId2 = await CreateCoefficientTypeAsync(s, "Сезонный-" + Suffix());
        var k2 = await CreateCoefficientAsync(s, typeId2, "Холодный регион", 1.080000m, "Среднегодовая температура ниже 0 °C");

        var calculation = await CreateAndCalculateNodeDraftAsync(s, 100.001m, k1, k2);

        // 100.001 × 1.1 × 1.08 = 118.801188 → ceil = 119, no intermediate rounding.
        Assert.Equal(1.188000m, calculation.TotalCoefficient);
        var row = Assert.Single(calculation.Rows);
        Assert.Equal(100.001m, row.BaseVolume);
        Assert.Equal(119m, row.CalculatedVolume);
    }

    [Fact]
    public async Task Recalculate_SameCoefficientTypeTwice_Rejected()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var typeId = await CreateCoefficientTypeAsync(s, "Сезонный-" + Suffix());
        var k1 = await CreateCoefficientAsync(s, typeId, "Зимняя", 1.1m, null);
        var k2 = await CreateCoefficientAsync(s, typeId, "Летняя", 1.2m, null);

        var nodeId = await CreateNodeAsync(s);
        var auId = await CreateAssemblyUnitAsync(s);
        var primary = await CreateGsmMaterialAsync(s);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        await AddNodeItemsAsync(s, nodeHK.Id, (auId, 1, 100m, "г", new[] { (primary, GsmCategory.Primary) }));
        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.RecalculateDraftAsync(
                new RecalculateIndividualCardDraftRequest(draft.Id, new[] { k1, k2 })));
        Assert.Contains("несколько коэффициентов", ex.Message);
    }

    [Fact]
    public async Task Recalculate_ArchivedOrMissingCoefficient_Rejected()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var typeId = await CreateCoefficientTypeAsync(s, "Сезонный-" + Suffix());
        var coefficientId = await CreateCoefficientAsync(s, typeId, "Зимняя", 1.1m, null);

        var nodeId = await CreateNodeAsync(s);
        var auId = await CreateAssemblyUnitAsync(s);
        var primary = await CreateGsmMaterialAsync(s);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        await AddNodeItemsAsync(s, nodeHK.Id, (auId, 1, 100m, "г", new[] { (primary, GsmCategory.Primary) }));
        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        // Archive the coefficient, then recalc: controlled error.
        var tracked = await s.Db.Coefficients.FirstAsync(c => c.Id == coefficientId);
        tracked.IsDeleted = true;
        tracked.DeletedAt = DateTime.UtcNow;
        await s.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.RecalculateDraftAsync(
                new RecalculateIndividualCardDraftRequest(draft.Id, new[] { coefficientId })));
        Assert.Contains("архивирован", ex.Message);

        // Unknown id: controlled error.
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.RecalculateDraftAsync(
                new RecalculateIndividualCardDraftRequest(draft.Id, new[] { Guid.NewGuid() })));
        Assert.Contains("не найдены", ex2.Message);
    }

    // ── 5–6: working coefficient selector ─────────────────────────────────

    [Fact]
    public async Task Selector_ExcludesArchivedCoefficientsAndTypes()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        var workingTypeId = await CreateCoefficientTypeAsync(s, "Рабочий-" + Suffix());
        var workingId = await CreateCoefficientAsync(s, workingTypeId, "Рабочий коэффициент", 1.05m, null);

        var archivedCoefficientTypeId = await CreateCoefficientTypeAsync(s, "Архивный тип-" + Suffix());
        var archivedTypeCoefficientId = await CreateCoefficientAsync(s, archivedCoefficientTypeId, "Архивный тип коэффициент", 1.06m, null);
        var typeTracked = await s.Db.CoefficientTypes.FirstAsync(t => t.Id == archivedCoefficientTypeId);
        typeTracked.IsDeleted = true;
        typeTracked.DeletedAt = DateTime.UtcNow;
        await s.Db.SaveChangesAsync();

        var archivedTypeId = await CreateCoefficientAsync(s, workingTypeId, "Архивный коэффициент", 1.07m, null);
        var coeffTracked = await s.Db.Coefficients.FirstAsync(c => c.Id == archivedTypeId);
        coeffTracked.IsDeleted = true;
        coeffTracked.DeletedAt = DateTime.UtcNow;
        await s.Db.SaveChangesAsync();

        var list = await s.IndividualCards.GetWorkingCoefficientsForDraftSelectAsync(draft.Id);
        var ids = list.Select(c => c.Id).ToList();

        Assert.Contains(workingId, ids);
        Assert.DoesNotContain(archivedTypeId, ids);
        // Coefficients of the archived type are excluded too.
        Assert.DoesNotContain(archivedTypeCoefficientId, ids);
    }

    [Fact]
    public async Task Selector_Search_FindsType_Name_Condition_Basis()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));

        var type1 = await CreateCoefficientTypeAsync(s, "ПоискТип-" + Suffix());
        var byType = await CreateCoefficientAsync(s, type1, "Обычное имя", 1.01m, null);
        var type2 = await CreateCoefficientTypeAsync(s, "Тип-" + Suffix());
        var byName = await CreateCoefficientAsync(s, type2, "ОсобоеНазваниеКоэффициента", 1.02m, null);
        var byCondition = await CreateCoefficientAsync(s, type2, "Обычное имя 2", 1.03m, "УникальноеУсловиеПоиска");
        var byBasis = await CreateCoefficientAsync(s, type2, "Обычное имя 3", 1.04m, null, "УникальноеОснованиеПоиска");

        var byTypeList = await s.IndividualCards.GetWorkingCoefficientsForDraftSelectAsync(draft.Id, "ПоискТип");
        Assert.Contains(byType, byTypeList.Select(x => x.Id).ToList());

        var byNameList = await s.IndividualCards.GetWorkingCoefficientsForDraftSelectAsync(draft.Id, "ОсобоеНазваниеКоэффициента");
        Assert.Contains(byName, byNameList.Select(x => x.Id).ToList());

        var byConditionList = await s.IndividualCards.GetWorkingCoefficientsForDraftSelectAsync(draft.Id, "УникальноеУсловиеПоиска");
        Assert.Contains(byCondition, byConditionList.Select(x => x.Id).ToList());

        var byBasisList = await s.IndividualCards.GetWorkingCoefficientsForDraftSelectAsync(draft.Id, "УникальноеОснованиеПоиска");
        Assert.Contains(byBasis, byBasisList.Select(x => x.Id).ToList());
    }

    // ── 7–10: formula by target level ─────────────────────────────────────

    [Fact]
    public async Task NodeCalculation_UsesHKCardItemVolumeAndQuantityOnly()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var auId = await CreateAssemblyUnitAsync(s);
        var primary = await CreateGsmMaterialAsync(s, gost: "ГОСТ");
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        await AddNodeItemsAsync(s, nodeHK.Id, (auId, 2, 100m, "г", new[] { (primary, GsmCategory.Primary) }));

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));
        var calculation = await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));

        var row = Assert.Single(calculation.Rows);
        Assert.Equal(2, row.AssemblyUnitQuantity);
        Assert.Equal(200m, row.BaseVolume);
        Assert.Equal(200m, row.CalculatedVolume);
    }

    [Fact]
    public async Task AggregateCalculation_AppliesNodeQuantity()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (_, aggregateId, _, _, _) = await CreateModelChainAsync(
            s, "г", 100m, 1, acNodeQuantity: 3);
        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Aggregate, aggregateId));
        var calculation = await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));

        // 100 × 1 (СЕ) × 3 (узлы в агрегате) = 300; Qproduct = 1.
        var row = Assert.Single(calculation.Rows);
        Assert.Equal(3, row.NodeQuantity);
        Assert.Equal(300m, row.BaseVolume);
        Assert.Equal(300m, row.CalculatedVolume);
    }

    [Fact]
    public async Task ModelCalculation_AppliesAggregateAndNodeQuantities()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _, _, _, _) = await CreateModelChainAsync(
            s, "г", 100m, 1, pcAggregateQuantity: 3, acNodeQuantity: 2);
        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        var calculation = await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));

        // 100 × 1 × 2 × 3 = 600.
        var row = Assert.Single(calculation.Rows);
        Assert.Equal(2, row.NodeQuantity);
        Assert.Equal(3, row.AggregateQuantity);
        Assert.Equal(600m, row.CalculatedVolume);
        Assert.Equal(600m, calculation.TotalNorm);
    }

    [Fact]
    public async Task ComplexCalculation_AppliesCompositionQuantity()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var complexId = await CreateComplexAsync(s);
        var (modelId, _, _, _, _) = await CreateModelChainAsync(
            s, "г", 100m, 1, pcAggregateQuantity: 3, acNodeQuantity: 2);
        await CreateComplexCompositionAsync(s, complexId, (modelId, 2));
        var complexHK = await CreateHKAsync(s, IndividualCardObjectLevel.Complex, complexId, _fixture.BranchA);
        var modelHK = await s.Db.HKCards.AsNoTracking().FirstAsync(h => h.EquipmentModelId == modelId);
        await AddComponentAsync(s, complexHK, modelHK);

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Complex, complexId));
        var calculation = await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));

        // 100 × 1 × 2 × 3 × 2 (комплекс) = 1200.
        var row = Assert.Single(calculation.Rows);
        Assert.Equal(2, row.ProductQuantity);
        Assert.Equal(1200m, row.CalculatedVolume);
        Assert.Equal(1200m, calculation.TotalNorm);
    }

    // ── 11: occurrence-aware rows ─────────────────────────────────────────

    [Fact]
    public async Task RepeatedAggregate_TwoBranches_TwoRowsWithIndependentFactors()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var complexId = await CreateComplexAsync(s);
        var (modelAId, _) = await CreateEquipmentAsync(s);
        var modelB = new EquipmentModel { Id = Guid.NewGuid(), Index = "EM-" + Suffix(), Name = "Изделие " + Suffix(), IsDeleted = false };
        s.Db.EquipmentModels.Add(modelB);
        await s.Db.SaveChangesAsync();

        var sharedAggregate = await CreateAggregateAsync(s);
        var nodeId = await CreateNodeAsync(s);
        var auId = await CreateAssemblyUnitAsync(s);
        var primary = await CreateGsmMaterialAsync(s, gost: "ГОСТ");

        await CreateComplexCompositionAsync(s, complexId, (modelAId, 1), (modelB.Id, 2));
        await CreateProductCompositionAsync(s, modelAId, (sharedAggregate, 3));
        await CreateProductCompositionAsync(s, modelB.Id, (sharedAggregate, 3));
        await CreateAggregateCompositionAsync(s, sharedAggregate, (nodeId, 2));

        var complexHK = await CreateHKAsync(s, IndividualCardObjectLevel.Complex, complexId, _fixture.BranchA);
        var modelAHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelAId, _fixture.BranchA);
        var modelBHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelB.Id, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, sharedAggregate, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        await AddComponentAsync(s, complexHK, modelAHK);
        await AddComponentAsync(s, complexHK, modelBHK);
        await AddComponentAsync(s, modelAHK, aggregateHK);
        await AddComponentAsync(s, modelBHK, aggregateHK);
        await AddComponentAsync(s, aggregateHK, nodeHK);
        await AddNodeItemsAsync(s, nodeHK.Id, (auId, 1, 100m, "г", new[] { (primary, GsmCategory.Primary) }));

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Complex, complexId));
        var calculation = await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));

        // The same node HK under two Изделие branches → two rows, factors
        // resolved per occurrence (Qproduct 1 vs 2), no deduplication.
        Assert.Equal(2, calculation.Rows.Count);
        Assert.All(calculation.Rows, r => Assert.Equal(2, r.NodeQuantity));
        Assert.All(calculation.Rows, r => Assert.Equal(3, r.AggregateQuantity));
        Assert.Contains(calculation.Rows, r => r.ProductQuantity == 1 && r.CalculatedVolume == 600m);
        Assert.Contains(calculation.Rows, r => r.ProductQuantity == 2 && r.CalculatedVolume == 1200m);
        Assert.Equal(1800m, calculation.TotalNorm);
    }

    // ── 12–13: ceiling and precision ──────────────────────────────────────

    [Theory]
    [InlineData(100.000000, 100)]
    [InlineData(100.000001, 101)]
    [InlineData(100.300000, 101)]
    [InlineData(499.250000, 500)]
    public async Task Calculation_CeilingToWholeGram(decimal volume, decimal expected)
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var calculation = await CreateAndCalculateNodeDraftAsync(s, volume);
        Assert.Equal(expected, Assert.Single(calculation.Rows).CalculatedVolume);
    }

    [Fact]
    public async Task Calculation_NoIntermediateRounding()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var typeId = await CreateCoefficientTypeAsync(s, "Сезонный-" + Suffix());
        var coefficientId = await CreateCoefficientAsync(s, typeId, "Полтора", 1.5m, null);

        // 100.001 × 1.5 = 150.0015 → 151; rounding the base first would give 152.
        var calculation = await CreateAndCalculateNodeDraftAsync(s, 100.001m, coefficientId);
        Assert.Equal(151m, Assert.Single(calculation.Rows).CalculatedVolume);
    }

    // ── 14–16: materials and totals ───────────────────────────────────────

    [Fact]
    public async Task Materials_AlternativesGetSameVolume_PrimaryOnlyInTotals()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var primary = await CreateGsmMaterialAsync(s, "Основная", "ГОСТ-1");
        var duplicate = await CreateGsmMaterialAsync(s, "Дублирующая", "ГОСТ-2");
        var reserve = await CreateGsmMaterialAsync(s, "Резервная", "ГОСТ-3");
        var foreign = await CreateGsmMaterialAsync(s, "Зарубежная", "ГОСТ-4");
        var (modelId, _, _, _, _) = await CreateModelChainAsync(
            s, "г", 100m, 1, pcAggregateQuantity: 1, acNodeQuantity: 1,
            (primary, GsmCategory.Primary),
            (duplicate, GsmCategory.Duplicate),
            (reserve, GsmCategory.Reserve),
            (foreign, GsmCategory.Foreign));

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        var calculation = await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));

        var row = Assert.Single(calculation.Rows);
        Assert.Equal(4, row.Materials.Count);
        Assert.All(row.Materials, m => Assert.Equal(row.CalculatedVolume, m.CalculatedVolume));

        // Totals contain only the Primary material, counted once.
        var total = Assert.Single(calculation.PrimaryTotals);
        Assert.Equal("Основная", total.MaterialName);
        Assert.Equal("ГОСТ-1", total.Gost);
        Assert.Equal(row.CalculatedVolume, total.TotalVolume);
        Assert.Equal(1, total.ItemCount);
        Assert.Equal(row.CalculatedVolume, calculation.TotalNorm);
    }

    // ── 17–18: validation problems ────────────────────────────────────────

    [Fact]
    public async Task MissingPrimaryMaterial_CreatesProblemAndBlocksForm()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var duplicate = await CreateGsmMaterialAsync(s, "Дублирующая");
        var (modelId, _, _, _, _) = await CreateModelChainAsync(
            s, "г", 100m, 1, pcAggregateQuantity: 1, acNodeQuantity: 1,
            (duplicate, GsmCategory.Duplicate));

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        var calculation = await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));

        Assert.Contains(calculation.Problems, p => p.Code == "MissingPrimaryMaterial");
        Assert.False(calculation.IsReadyToForm);
        Assert.Equal(0m, calculation.TotalNorm);
        Assert.Empty(calculation.PrimaryTotals);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id)));
        Assert.Contains("основного материала", ex.Message);
        Assert.Equal(0, await CountAuditsAsync(s, draft.Id, "IndividualCard.Formed"));
    }

    [Fact]
    public async Task InvalidUnitOfMeasure_CreatesProblemAndBlocksForm()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _, _, _, _) = await CreateModelChainAsync(s, "кг", 100m, 1);

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        var calculation = await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));

        Assert.Contains(calculation.Problems, p => p.Code == "InvalidUnitOfMeasure");
        Assert.False(calculation.IsReadyToForm);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id)));
        Assert.Contains("только в граммах", ex.Message);
    }

    // ── 19: partial draft recalculation ───────────────────────────────────

    [Fact]
    public async Task PartialDraft_RecalculatesCompleteBranches_ButIsNotReadyToForm()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _, _, _, _) = await CreateModelChainAsync(s, "г", 100m, 1);

        // Second aggregate under the same Изделие without a node link: its
        // branch is incomplete and must not produce rows.
        var brokenAggregateId = await CreateAggregateAsync(s);
        var node2 = await CreateNodeAsync(s);
        await CreateAggregateCompositionAsync(s, brokenAggregateId, (node2, 1));
        var aggregateHK2 = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, brokenAggregateId, _fixture.BranchA);
        var modelHK = await s.Db.HKCards.AsNoTracking().FirstAsync(h => h.EquipmentModelId == modelId);
        await AddComponentAsync(s, modelHK, aggregateHK2);

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        Assert.True(draft.HasNormativeGaps);

        var calculation = await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));

        // The complete branch is calculated; the incomplete one is skipped.
        var row = Assert.Single(calculation.Rows);
        Assert.Equal(100m, row.CalculatedVolume);
        Assert.Contains(calculation.Problems, p => p.Code == "IncompleteNormativeChain");
        Assert.False(calculation.IsReadyToForm);
    }

    // ── 20–21: atomic replace and rollback ────────────────────────────────

    [Fact]
    public async Task Recalculate_ReplacesOldCalculation_NoMerge()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _, _, nodeHKCardId, _) = await CreateModelChainAsync(s, "г", 100m, 1);

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        var first = await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));
        Assert.Single(first.Rows);

        // New source row appears in the node HK → recalculation replaces the
        // whole item set: 2 rows after, old item ids gone.
        var au2 = await CreateAssemblyUnitAsync(s);
        var primary = await CreateGsmMaterialAsync(s);
        await AddNodeItemsAsync(s, nodeHKCardId, (au2, 1, 50m, "г", new[] { (primary, GsmCategory.Primary) }));

        var second = await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));

        Assert.Equal(2, second.Rows.Count);
        Assert.Equal(150m, second.TotalNorm);
        Assert.Equal(2, await s.Db.IndividualCardItems.CountAsync(i => i.IndividualCardId == draft.Id));
        Assert.DoesNotContain(second.Rows, r => r.Id == first.Rows[0].Id);
    }

    [Fact]
    public async Task Recalculate_FailureRollsBackAndWritesNoAudit()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _, _, _, _) = await CreateModelChainAsync(s, "г", 100m, 1);
        var typeId = await CreateCoefficientTypeAsync(s, "Сезонный-" + Suffix());
        var coefficientId = await CreateCoefficientAsync(s, typeId, "Зимняя", 1.1m, null);

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        var first = await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, new[] { coefficientId }));
        Assert.Equal(1, await CountAuditsAsync(s, draft.Id, "IndividualCard.Recalculated"));

        // Archive the coefficient and inject a failure mid-replace: the old
        // calculation, coefficient snapshots and TotalNorm must survive.
        var tracked = await s.Db.Coefficients.FirstAsync(c => c.Id == coefficientId);
        tracked.IsDeleted = true;
        tracked.DeletedAt = DateTime.UtcNow;
        await s.Db.SaveChangesAsync();

        try
        {
            FailingCommandInterceptor.ArmAt("IndividualCardCoefficientSnapshots");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                s.IndividualCards.RecalculateDraftAsync(
                    new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>())));
            Assert.True(FailingCommandInterceptor.Fired, "Interceptor did not fire.");
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        var reloaded = await s.IndividualCards.GetDraftCalculationAsync(draft.Id);
        Assert.NotNull(reloaded);
        Assert.Single(reloaded!.Rows);
        Assert.Equal(1.100000m, reloaded.TotalCoefficient);
        Assert.Equal(110m, reloaded.TotalNorm);
        Assert.Single(reloaded.Coefficients);
        Assert.Equal(1, await CountAuditsAsync(s, draft.Id, "IndividualCard.Recalculated"));
    }

    // ── 22–23: authorization ──────────────────────────────────────────────

    [Fact]
    public async Task Recalculate_PermissionsEnforced()
    {
        await using var s = Scope();
        var author = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchA);
        SetUser(s, author);
        var (modelId, _, _, _, _) = await CreateModelChainAsync(s, "г", 100m, 1);
        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));

        // Author without RecalculateDraft: rejected even as author.
        await DenyAsync(s, author, PermissionCodes.IndividualCardRecalculateDraft);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.RecalculateDraftAsync(
                new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>())));

        // Outsider with RecalculateDraft but neither author nor EditDraft: rejected.
        var outsider = await CreateUserAsync(s, nameof(UserRole.HeadOfDepartment), _fixture.BranchA);
        SetUser(s, outsider);
        await GrantAsync(s, outsider, PermissionCodes.IndividualCardRecalculateDraft);
        await DenyAsync(s, outsider, PermissionCodes.IndividualCardEditDraft);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.RecalculateDraftAsync(
                new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>())));

        // Editor with EditDraft + RecalculateDraft: allowed.
        var editor = await CreateUserAsync(s, nameof(UserRole.HeadOfDepartment), _fixture.BranchA);
        SetUser(s, editor);
        await GrantAsync(s, editor, PermissionCodes.IndividualCardRecalculateDraft);
        await GrantAsync(s, editor, PermissionCodes.IndividualCardEditDraft);
        var result = await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));
        Assert.Single(result.Rows);
    }

    [Fact]
    public async Task Form_RequiresFormPermissionAndBranchScope()
    {
        await using var s = Scope();
        var author = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchA);
        SetUser(s, author);
        var (modelId, _, _, _, _) = await CreateModelChainAsync(s, "г", 100m, 1);
        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));

        // Author without Form: rejected.
        await DenyAsync(s, author, PermissionCodes.IndividualCardForm);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id)));

        // Foreign branch user with Form: rejected (branch scope).
        var foreign = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchB);
        SetUser(s, foreign);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id)));
    }

    // ── 24–27: Form workflow ──────────────────────────────────────────────

    [Fact]
    public async Task Form_RejectsDraftWithoutCalculation()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _, _, _, _) = await CreateModelChainAsync(s, "г", 100m, 1);
        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id)));
        Assert.Contains("Расчёт не выполнялся", ex.Message);
        Assert.Equal(0, await CountAuditsAsync(s, draft.Id, "IndividualCard.Formed"));
    }

    [Fact]
    public async Task Form_SucceedsForCompleteCalculatedDraft()
    {
        await using var s = Scope();
        var author = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchA);
        SetUser(s, author);
        var typeId = await CreateCoefficientTypeAsync(s, "Сезонный-" + Suffix());
        var coefficientId = await CreateCoefficientAsync(s, typeId, "Зимняя", 1.1m, null);
        var (modelId, _, _, _, _) = await CreateModelChainAsync(s, "г", 100m, 1);

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, new[] { coefficientId }));

        var formed = await s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id));

        Assert.Equal(IndividualCardStatus.Formed, formed.Status);
        Assert.Single(formed.Rows);
        Assert.Equal(1, await CountAuditsAsync(s, draft.Id, "IndividualCard.Formed"));

        var card = await s.Db.IndividualCards.AsNoTracking().FirstAsync(c => c.Id == draft.Id);
        Assert.Equal(IndividualCardStatus.Formed, card.Status);
        Assert.NotNull(card.FormedAt);
        Assert.Equal(author.Id, card.FormedByUserId);
        Assert.Equal(DateTime.UtcNow, card.FormedAt!.Value, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FormedCard_CannotRecalculateOrFormAgain()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (modelId, _, _, _, _) = await CreateModelChainAsync(s, "г", 100m, 1);
        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));
        await s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.RecalculateDraftAsync(
                new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>())));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id)));

        Assert.Equal(1, await CountAuditsAsync(s, draft.Id, "IndividualCard.Recalculated"));
        Assert.Equal(1, await CountAuditsAsync(s, draft.Id, "IndividualCard.Formed"));
    }

    // ── coefficient helpers ───────────────────────────────────────────────

    private async Task<Guid> CreateCoefficientTypeAsync(TestScope s, string name)
    {
        var type = new CoefficientType
        {
            Id = Guid.NewGuid(),
            Name = name,
            SortOrder = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        s.Db.CoefficientTypes.Add(type);
        await s.Db.SaveChangesAsync();
        return type.Id;
    }

    private async Task<Guid> CreateCoefficientAsync(
        TestScope s, Guid typeId, string? name, decimal value, string? condition, string? basis = null)
    {
        var coefficient = new Coefficient
        {
            Id = Guid.NewGuid(),
            CoefficientTypeId = typeId,
            Name = name ?? "Коэффициент " + Suffix(),
            Value = value,
            ConditionDescription = condition,
            NormativeBasis = basis,
            SortOrder = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        s.Db.Coefficients.Add(coefficient);
        await s.Db.SaveChangesAsync();
        return coefficient.Id;
    }
}
