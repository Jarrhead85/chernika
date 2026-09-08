using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class IndividualCardExportIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public IndividualCardExportIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

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

    private async Task<int> CountAllAuditsAsync(TestScope s, Guid entityId) =>
        await s.Db.AuditLogs.CountAsync(a =>
            a.EntityType == "IndividualCard" && a.EntityId == entityId.ToString());

    // ── reference helpers ─────────────────────────────────────────────────

    private async Task<Guid> CreateNodeAsync(TestScope s)
    {
        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Suffix(), Name = "Узел " + Suffix(), IsDeleted = false };
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

    private async Task<Guid> CreateEquipmentAsync(TestScope s)
    {
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "EM-" + Suffix(), Name = "Изделие " + Suffix(), IsDeleted = false };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();
        return model.Id;
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
        HKCardStatus status = HKCardStatus.Approved)
    {
        var hk = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-" + level.ToString()[..3] + "-" + Suffix(),
            Version = "v" + Suffix()[..4],
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

    private async Task AddComponentAsync(TestScope s, Guid parentHKCardId, Guid childHKCardId, int sortOrder = 1)
    {
        var parent = await s.Db.HKCards.AsNoTracking().FirstAsync(h => h.Id == parentHKCardId);
        var child = await s.Db.HKCards.AsNoTracking().FirstAsync(h => h.Id == childHKCardId);
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

    private async Task<(Guid DraftId, Guid ModelId, Guid AggregateId, Guid NodeId, Guid NodeHKCardId, Guid AggregateHKCardId)>
        CreateFormedCardAsync(TestScope s, decimal volume = 100m, bool form = true, Guid? coefficientId = null)
    {
        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateEquipmentAsync(s);
        var aggregateId = await CreateAggregateAsync(s);
        var nodeId = await CreateNodeAsync(s);
        var auId = await CreateAssemblyUnitAsync(s);
        var primary = await CreateGsmMaterialAsync(s, gost: "ГОСТ-" + Suffix());
        await CreateProductCompositionAsync(s, modelId, (aggregateId, 1));
        await CreateAggregateCompositionAsync(s, aggregateId, (nodeId, 1));
        var modelHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        await AddComponentAsync(s, modelHK.Id, aggregateHK.Id);
        await AddComponentAsync(s, aggregateHK.Id, nodeHK.Id);
        await AddNodeItemsAsync(s, nodeHK.Id, (auId, 1, volume, "г", new[] { (primary, GsmCategory.Primary) }));

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        if (form)
        {
            await s.IndividualCards.RecalculateDraftAsync(
                new RecalculateIndividualCardDraftRequest(draft.Id,
                    coefficientId is null ? Array.Empty<Guid>() : new[] { coefficientId.Value }));
            await s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id));
        }
        return (draft.Id, modelId, aggregateId, nodeId, nodeHK.Id, aggregateHK.Id);
    }

    private async Task<Guid> CreateCoefficientTypeAsync(TestScope s, string name)
    {
        var type = new CoefficientType
        {
            Id = Guid.NewGuid(), Name = name, SortOrder = 1,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        s.Db.CoefficientTypes.Add(type);
        await s.Db.SaveChangesAsync();
        return type.Id;
    }

    private async Task<Guid> CreateCoefficientAsync(TestScope s, Guid typeId, string name, decimal value)
    {
        var coefficient = new Coefficient
        {
            Id = Guid.NewGuid(),
            CoefficientTypeId = typeId,
            Name = name,
            Value = value,
            SortOrder = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        s.Db.Coefficients.Add(coefficient);
        await s.Db.SaveChangesAsync();
        return coefficient.Id;
    }

    // ── 1. Formed: полный immutable граф ──────────────────────────────────

    [Fact]
    public async Task FormedExport_ContainsSnapshotsCoefficientsAndImmutableRows()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var typeId = await CreateCoefficientTypeAsync(s, "Климат " + Suffix());
        var coefficientId = await CreateCoefficientAsync(s, typeId, "Коэф " + Suffix(), 1.25m);
        var (cardId, modelId, _, _, nodeHKId, _) =
            await CreateFormedCardAsync(s, volume: 200m, coefficientId: coefficientId);

        var model = await s.Db.EquipmentModels.AsNoTracking().FirstAsync(m => m.Id == modelId);
        var nodeHK = await s.Db.HKCards.AsNoTracking().FirstAsync(h => h.Id == nodeHKId);

        var export = await s.IndividualCards.GetExportAsync(cardId);
        Assert.NotNull(export);
        Assert.Equal(IndividualCardStatus.Formed, export!.Status);
        Assert.Equal("Сформирована", export.StatusDisplay);
        Assert.Equal(model.Index, export.TargetObjectCode);
        Assert.Equal(model.Name, export.TargetObjectName);

        Assert.NotEmpty(export.Compositions);
        Assert.NotEmpty(export.Compositions[0].Aggregates);
        Assert.NotEmpty(export.Compositions[0].Aggregates[0].Nodes);

        Assert.NotEmpty(export.HKSources);
        Assert.Contains(export.HKSources, h => h.SourceHKCardId == nodeHKId);
        Assert.All(export.HKSources, h =>
        {
            Assert.Equal(IndividualCardDisplay.ObjectLevel(h.ObjectLevel), h.ObjectLevelDisplay);
            Assert.True(h.CapturedAt != default);
        });

        var coefficient = Assert.Single(export.Coefficients);
        Assert.Equal(1.25m, coefficient.Value);
        Assert.Equal(1.25m, export.TotalCoefficient);

        var row = Assert.Single(export.Rows);
        Assert.Equal(nodeHK.Code, row.SourceHKCardCode);
        Assert.Equal(nodeHK.Version, row.SourceHKCardVersion);
        Assert.Equal(200m, row.SourceVolume);
        Assert.Equal(200m, row.BaseVolume);
        Assert.Equal(250m, row.CalculatedVolume);
        Assert.Equal("г", row.UnitOfMeasure);
        Assert.Single(row.PrimaryMaterials);
        Assert.Empty(row.DuplicateMaterials);
        Assert.Empty(row.ReserveMaterials);
        Assert.Empty(row.ForeignMaterials);

        var primary = Assert.Single(export.PrimaryMaterials);
        Assert.Equal(250m, primary.Value);
        Assert.Equal(1, primary.RowCount);

        Assert.NotEmpty(export.History);
        Assert.Contains(export.History, h => h.Id == cardId);
    }

    // ── 2. Archived: без черновичного предупреждения ──────────────────────

    [Fact]
    public async Task ArchivedExport_HasArchivedStatusAndNoDraftWarning()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _, _, _, _) = await CreateFormedCardAsync(s);
        await s.IndividualCards.ArchiveIndividualCardAsync(cardId);

        var export = await s.IndividualCards.GetExportAsync(cardId);
        Assert.NotNull(export);
        Assert.Equal(IndividualCardStatus.Archived, export!.Status);
        Assert.Equal("Архив", export.StatusDisplay);
        Assert.DoesNotContain(export.Warnings, w => w.Code == "DraftNotice");
        Assert.DoesNotContain(export.Warnings, w => w.Message.Contains("ЧЕРНОВИК"));
    }

    // ── 3. Draft: черновичное предупреждение ──────────────────────────────

    [Fact]
    public async Task DraftExport_HasDraftNoticeWarning()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _, _, _, _) = await CreateFormedCardAsync(s, form: false);

        var export = await s.IndividualCards.GetExportAsync(cardId);
        Assert.NotNull(export);
        Assert.Equal(IndividualCardStatus.Draft, export!.Status);
        Assert.Contains(export.Warnings, w =>
            w.Code == "DraftNotice" && w.Message == "ЧЕРНОВИК. Данные могут быть изменены.");
    }

    // ── 4. Gaps и problems → warnings ─────────────────────────────────────

    [Fact]
    public async Task GapAndProblemSnapshots_AreExportedAsWarnings()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _, _, _, _) = await CreateFormedCardAsync(s, form: false);

        s.Db.Add(new IndividualCardNormativeGapSnapshot
        {
            Id = Guid.NewGuid(),
            IndividualCardId = cardId,
            Kind = IndividualCardNormativeGapKind.MissingLinkedHKCard,
            RelatedLevel = IndividualCardObjectLevel.Node,
            RelatedObjectType = "Node",
            RelatedObjectName = "Узел без ХК",
            Message = "Для узла не найдена утверждённая ХК.",
            SortOrder = 1,
            CapturedAt = DateTime.UtcNow,
        });
        s.Db.Add(new IndividualCardCalculationProblemSnapshot
        {
            Id = Guid.NewGuid(),
            IndividualCardId = cardId,
            Code = "IncompleteNormativeChain",
            Message = "Нормативная цепочка не завершена.",
            SortOrder = 1,
            CapturedAt = DateTime.UtcNow,
        });
        await s.Db.SaveChangesAsync();

        var export = await s.IndividualCards.GetExportAsync(cardId);
        Assert.NotNull(export);
        Assert.Contains(export!.Warnings, w => w.Code == "MissingLinkedHKCard" && w.Message == "Для узла не найдена утверждённая ХК.");
        Assert.Contains(export.Warnings, w => w.Code == "IncompleteNormativeChain" && w.Message == "Нормативная цепочка не завершена.");
    }

    [Fact]
    public async Task FormedWithHistoricalGaps_HasHistoricalNotice()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _, _, _, _) = await CreateFormedCardAsync(s);

        s.Db.Add(new IndividualCardCalculationProblemSnapshot
        {
            Id = Guid.NewGuid(),
            IndividualCardId = cardId,
            Code = "TestProblem",
            Message = "Зафиксированная проблема расчёта.",
            SortOrder = 1,
            CapturedAt = DateTime.UtcNow,
        });
        await s.Db.SaveChangesAsync();

        var export = await s.IndividualCards.GetExportAsync(cardId);
        Assert.NotNull(export);
        Assert.Equal(IndividualCardStatus.Formed, export!.Status);
        Assert.Contains(export.Warnings, w =>
            w.Code == "HistoricalNotice" &&
            w.Message == "В документе зафиксированы нормативные пробелы или проблемы расчёта.");
        Assert.Contains(export.Warnings, w => w.Message == "Зафиксированная проблема расчёта.");
    }

    // ── 5. Live-изменения источников не влияют на export ──────────────────

    [Fact]
    public async Task LiveSourceChanges_DoNotChangeExport()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var typeId = await CreateCoefficientTypeAsync(s, "Климат " + Suffix());
        var coefficientId = await CreateCoefficientAsync(s, typeId, "Коэф " + Suffix(), 1.1m);
        var (cardId, modelId, aggregateId, nodeId, nodeHKId, aggregateHKId) =
            await CreateFormedCardAsync(s, coefficientId: coefficientId);

        var before = await s.IndividualCards.GetExportAsync(cardId);
        Assert.NotNull(before);

        var model = await s.Db.EquipmentModels.FirstAsync(m => m.Id == modelId);
        model.Name = "Переименовано изделие";
        var aggregate = await s.Db.Aggregates.FirstAsync(a => a.Id == aggregateId);
        aggregate.Name = "Переименовано агрегат";
        var node = await s.Db.Nodes.FirstAsync(n => n.Id == nodeId);
        node.Name = "Переименовано узел";
        var nodeHK = await s.Db.HKCards.FirstAsync(h => h.Id == nodeHKId);
        nodeHK.Code = "HK-REN-" + Suffix();
        nodeHK.Version = "v" + Suffix()[..4];
        var aggregateHK = await s.Db.HKCards.FirstAsync(h => h.Id == aggregateHKId);
        aggregateHK.Code = "HK-AGGR-" + Suffix();
        var material = await s.Db.GsmMaterials.FirstAsync();
        material.Name = "Переименовано ГСМ";
        var coefficient = await s.Db.Coefficients.FirstAsync(c => c.Id == coefficientId);
        coefficient.Name = "Переименовано коэффициент";
        await s.Db.SaveChangesAsync();

        var after = await s.IndividualCards.GetExportAsync(cardId);
        Assert.NotNull(after);
        Assert.Equal(before!.TargetObjectCode, after!.TargetObjectCode);
        Assert.Equal(before.TargetObjectName, after.TargetObjectName);

        Assert.Equal(before.Compositions.Count, after.Compositions.Count);
        Assert.Equal(before.Compositions[0].SourceCompositionVersion, after.Compositions[0].SourceCompositionVersion);
        Assert.Equal(before.Compositions[0].TargetObjectCode, after.Compositions[0].TargetObjectCode);
        Assert.Equal(before.Compositions[0].Aggregates[0].Name, after.Compositions[0].Aggregates[0].Name);
        Assert.Equal(before.Compositions[0].Aggregates[0].Nodes[0].Name, after.Compositions[0].Aggregates[0].Nodes[0].Name);

        Assert.Equal(before.HKSources.Count, after.HKSources.Count);
        Assert.Equal(before.HKSources[0].HKCardCode, after.HKSources[0].HKCardCode);
        Assert.Equal(before.HKSources[0].HKCardVersion, after.HKSources[0].HKCardVersion);

        Assert.Equal(before.Coefficients, after.Coefficients);

        var rowBefore = Assert.Single(before.Rows);
        var rowAfter = Assert.Single(after.Rows);
        Assert.Equal(rowBefore.SourceHKCardCode, rowAfter.SourceHKCardCode);
        Assert.Equal(rowBefore.SourceHKCardVersion, rowAfter.SourceHKCardVersion);
        Assert.Equal(rowBefore.PrimaryMaterials, rowAfter.PrimaryMaterials);
        Assert.Equal(before.PrimaryMaterials, after.PrimaryMaterials);

        var row = Assert.Single(after.Rows);
        Assert.NotEqual(nodeHK.Code, row.SourceHKCardCode);
        Assert.NotEqual(nodeHK.Version, row.SourceHKCardVersion);
    }

    // ── 6. Повторные occurrence одного источника сохраняются ──────────────

    [Fact]
    public async Task RepeatedSourceHKCard_RemainsSeparateOccurrences()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateEquipmentAsync(s);
        var aggregate1 = await CreateAggregateAsync(s);
        var aggregate2 = await CreateAggregateAsync(s);
        var nodeId = await CreateNodeAsync(s);
        var auId = await CreateAssemblyUnitAsync(s);
        var primary = await CreateGsmMaterialAsync(s, gost: "ГОСТ-" + Suffix());
        await CreateProductCompositionAsync(s, modelId, (aggregate1, 1), (aggregate2, 1));
        await CreateAggregateCompositionAsync(s, aggregate1, (nodeId, 1));
        await CreateAggregateCompositionAsync(s, aggregate2, (nodeId, 1));
        var modelHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK1 = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate1, _fixture.BranchA);
        var aggregateHK2 = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregate2, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        await AddComponentAsync(s, modelHK.Id, aggregateHK1.Id);
        await AddComponentAsync(s, modelHK.Id, aggregateHK2.Id);
        await AddComponentAsync(s, aggregateHK1.Id, nodeHK.Id);
        await AddComponentAsync(s, aggregateHK2.Id, nodeHK.Id);
        await AddNodeItemsAsync(s, nodeHK.Id, (auId, 1, 100m, "г", new[] { (primary, GsmCategory.Primary) }));

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));
        await s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id));

        var export = await s.IndividualCards.GetExportAsync(draft.Id);
        Assert.NotNull(export);

        var nodeOccurrences = export!.HKSources
            .Where(h => h.SourceHKCardId == nodeHK.Id)
            .ToList();
        Assert.Equal(2, nodeOccurrences.Count);
        Assert.Equal(2, nodeOccurrences.Select(h => h.ParentHKSourceSnapshotId).Distinct().Count());

        var nodeRows = export.Rows.Where(r => r.SourceHKCardId == nodeHK.Id).ToList();
        Assert.Equal(2, nodeRows.Count);
    }

    // ── 7. Несколько Primary марок без общего итога ────────────────────────

    [Fact]
    public async Task MultiplePrimaryMaterials_StaySeparateWithoutGrandTotal()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var cardId = await CreateTwoPrimaryCardAsync(s);

        var export = await s.IndividualCards.GetExportAsync(cardId);
        Assert.NotNull(export);
        Assert.Equal(2, export!.PrimaryMaterials.Count);
        Assert.All(export.PrimaryMaterials, p =>
        {
            Assert.Equal(1, p.RowCount);
            Assert.Equal(100m, p.Value);
        });

        var row = Assert.Single(export.Rows);
        Assert.Equal(2, row.PrimaryMaterials.Count);
        Assert.Equal(100m, row.CalculatedVolume);
    }

    // ── 8. Категории материалов сохраняются ────────────────────────────────

    [Fact]
    public async Task MaterialCategories_RemainInCategoryArrays()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, primaryName, duplicateName, reserveName, foreignName) =
            await CreateAllCategoriesCardAsync(s);

        var export = await s.IndividualCards.GetExportAsync(cardId);
        Assert.NotNull(export);
        var row = Assert.Single(export!.Rows);

        var primary = Assert.Single(row.PrimaryMaterials);
        Assert.Equal(primaryName, primary.MaterialName);
        var duplicate = Assert.Single(row.DuplicateMaterials);
        Assert.Equal(duplicateName, duplicate.MaterialName);
        var reserve = Assert.Single(row.ReserveMaterials);
        Assert.Equal(reserveName, reserve.MaterialName);
        var foreign = Assert.Single(row.ForeignMaterials);
        Assert.Equal(foreignName, foreign.MaterialName);
    }

    // ── 9. SourceHK Code/Version из immutable D4-полей ─────────────────────

    [Fact]
    public async Task RowSourceHK_FieldsComeFromImmutableItemFields()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _, _, nodeHKId, _) = await CreateFormedCardAsync(s);
        var nodeHK = await s.Db.HKCards.AsNoTracking().FirstAsync(h => h.Id == nodeHKId);

        var export = await s.IndividualCards.GetExportAsync(cardId);
        Assert.NotNull(export);
        var row = Assert.Single(export!.Rows);
        Assert.Equal(nodeHK.Code, row.SourceHKCardCode);
        Assert.Equal(nodeHK.Version, row.SourceHKCardVersion);

        var source = Assert.Single(export.HKSources, h => h.SourceHKCardId == nodeHKId);
        Assert.Equal(nodeHK.Code, source.HKCardCode);
        Assert.Equal(nodeHK.Version, source.HKCardVersion);
    }

    // ── 10. Чужой филиал → null ────────────────────────────────────────────

    [Fact]
    public async Task ForeignBranchCard_ReturnsNullForNonSystemAdmin()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _, _, _, _) = await CreateFormedCardAsync(s);

        var foreign = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchB);
        await GrantAsync(s, foreign, PermissionCodes.IndividualCardView);
        SetUser(s, foreign);

        var export = await s.IndividualCards.GetExportAsync(cardId);
        Assert.Null(export);
    }

    // ── 11. Без IndividualCardView → отказ ─────────────────────────────────

    [Fact]
    public async Task WithoutViewPermission_IsRejected()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _, _, _, _) = await CreateFormedCardAsync(s);

        var user = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchA);
        await DenyAsync(s, user, PermissionCodes.IndividualCardView);
        SetUser(s, user);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.GetExportAsync(cardId));
    }

    // ── 12. GetExportAsync не пишет аудит и не меняет данные ───────────────

    [Fact]
    public async Task GetExportAsync_WritesNoAuditAndChangesNothing()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _, _, _, _) = await CreateFormedCardAsync(s);

        var auditsBefore = await CountAllAuditsAsync(s, cardId);
        var cardBefore = await s.Db.IndividualCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        var compositionsBefore = await s.Db.Set<IndividualCardCompositionSnapshot>().CountAsync(c => c.IndividualCardId == cardId);
        var itemsBefore = await s.Db.Set<IndividualCardItem>().CountAsync(i => i.IndividualCardId == cardId);

        var export = await s.IndividualCards.GetExportAsync(cardId);
        Assert.NotNull(export);

        Assert.Equal(auditsBefore, await CountAllAuditsAsync(s, cardId));
        Assert.Equal(0, await CountAuditsAsync(s, cardId, "IndividualCard.PdfExported"));
        Assert.Equal(0, await CountAuditsAsync(s, cardId, "IndividualCard.XlsxExported"));

        var cardAfter = await s.Db.IndividualCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.Equal(cardBefore.RevisionNumber, cardAfter.RevisionNumber);
        Assert.Equal(cardBefore.Status, cardAfter.Status);
        Assert.Equal(cardBefore.Code, cardAfter.Code);
        Assert.Equal(compositionsBefore,
            await s.Db.Set<IndividualCardCompositionSnapshot>().CountAsync(c => c.IndividualCardId == cardId));
        Assert.Equal(itemsBefore,
            await s.Db.Set<IndividualCardItem>().CountAsync(i => i.IndividualCardId == cardId));
    }

    // ── 13/14. Аудит экспорта ──────────────────────────────────────────────

    [Fact]
    public async Task RecordPdfExportAsync_WritesExactlyOneAudit()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _, _, _, _) = await CreateFormedCardAsync(s);

        await s.IndividualCards.RecordPdfExportAsync(cardId);

        Assert.Equal(1, await CountAuditsAsync(s, cardId, "IndividualCard.PdfExported"));

        var entry = await s.Db.AuditLogs.AsNoTracking()
            .FirstAsync(a => a.EntityType == "IndividualCard" && a.EntityId == cardId.ToString()
                && a.Action == "IndividualCard.PdfExported");
        Assert.Contains("ExportFormat=PDF", entry.Details);
        Assert.Contains("Status=Formed", entry.Details);
        Assert.DoesNotContain("http", entry.Details ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordXlsxExportAsync_WritesExactlyOneAudit()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _, _, _, _) = await CreateFormedCardAsync(s);

        await s.IndividualCards.RecordXlsxExportAsync(cardId);

        Assert.Equal(1, await CountAuditsAsync(s, cardId, "IndividualCard.XlsxExported"));

        var entry = await s.Db.AuditLogs.AsNoTracking()
            .FirstAsync(a => a.EntityType == "IndividualCard" && a.EntityId == cardId.ToString()
                && a.Action == "IndividualCard.XlsxExported");
        Assert.Contains("ExportFormat=XLSX", entry.Details);
        Assert.Contains("Status=Formed", entry.Details);
    }

    // ── 15. Аудит-методы защищены правами и филиалом ───────────────────────

    [Fact]
    public async Task RecordExportMethods_ArePermissionAndBranchGuarded()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _, _, _, _) = await CreateFormedCardAsync(s);

        var foreign = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchB);
        await GrantAsync(s, foreign, PermissionCodes.IndividualCardView);
        SetUser(s, foreign);

        await s.IndividualCards.RecordPdfExportAsync(cardId);
        Assert.Equal(0, await CountAuditsAsync(s, cardId, "IndividualCard.PdfExported"));

        await s.IndividualCards.RecordXlsxExportAsync(cardId);
        Assert.Equal(0, await CountAuditsAsync(s, cardId, "IndividualCard.XlsxExported"));

        var denied = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchA);
        await DenyAsync(s, denied, PermissionCodes.IndividualCardView);
        SetUser(s, denied);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.RecordPdfExportAsync(cardId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.IndividualCards.RecordXlsxExportAsync(cardId));
    }

    // ── composite helpers ──────────────────────────────────────────────────

    private async Task<(Guid CardId, string PrimaryName, string DuplicateName, string ReserveName, string ForeignName)>
        CreateAllCategoriesCardAsync(TestScope s)
    {
        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateEquipmentAsync(s);
        var aggregateId = await CreateAggregateAsync(s);
        var nodeId = await CreateNodeAsync(s);
        var auId = await CreateAssemblyUnitAsync(s);
        var primaryName = "ГСМ основная " + Suffix();
        var duplicateName = "ГСМ дублирующая " + Suffix();
        var reserveName = "ГСМ резервная " + Suffix();
        var foreignName = "ГСМ зарубежная " + Suffix();
        var primary = await CreateGsmMaterialAsync(s, name: primaryName);
        var duplicate = await CreateGsmMaterialAsync(s, name: duplicateName);
        var reserve = await CreateGsmMaterialAsync(s, name: reserveName);
        var foreign = await CreateGsmMaterialAsync(s, name: foreignName);
        await CreateProductCompositionAsync(s, modelId, (aggregateId, 1));
        await CreateAggregateCompositionAsync(s, aggregateId, (nodeId, 1));
        var modelHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        await AddComponentAsync(s, modelHK.Id, aggregateHK.Id);
        await AddComponentAsync(s, aggregateHK.Id, nodeHK.Id);
        await AddNodeItemsAsync(s, nodeHK.Id,
            (auId, 1, 100m, "г", new[]
            {
                (primary, GsmCategory.Primary),
                (duplicate, GsmCategory.Duplicate),
                (reserve, GsmCategory.Reserve),
                (foreign, GsmCategory.Foreign),
            }));

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));
        await s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id));
        return (draft.Id, primaryName, duplicateName, reserveName, foreignName);
    }

    private async Task<Guid> CreateTwoPrimaryCardAsync(TestScope s)
    {
        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateEquipmentAsync(s);
        var aggregateId = await CreateAggregateAsync(s);
        var nodeId = await CreateNodeAsync(s);
        var auId = await CreateAssemblyUnitAsync(s);
        var primary1 = await CreateGsmMaterialAsync(s, gost: "ГОСТ-" + Suffix());
        var primary2 = await CreateGsmMaterialAsync(s, gost: "ГОСТ-" + Suffix());
        await CreateProductCompositionAsync(s, modelId, (aggregateId, 1));
        await CreateAggregateCompositionAsync(s, aggregateId, (nodeId, 1));
        var modelHK = await CreateHKAsync(s, IndividualCardObjectLevel.EquipmentModel, modelId, _fixture.BranchA);
        var aggregateHK = await CreateHKAsync(s, IndividualCardObjectLevel.Aggregate, aggregateId, _fixture.BranchA);
        var nodeHK = await CreateHKAsync(s, IndividualCardObjectLevel.Node, nodeId, _fixture.BranchA);
        await AddComponentAsync(s, modelHK.Id, aggregateHK.Id);
        await AddComponentAsync(s, aggregateHK.Id, nodeHK.Id);
        await AddNodeItemsAsync(s, nodeHK.Id,
            (auId, 1, 100m, "г", new[] { (primary1, GsmCategory.Primary), (primary2, GsmCategory.Primary) }));

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));
        await s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id));
        return draft.Id;
    }
}
