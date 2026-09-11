using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class IndividualCardWorkAreaIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public IndividualCardWorkAreaIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private TestScope Scope() => _fixture.CreateScope();

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    private void SetUser(TestScope s, ApplicationUser user) =>
        s.User.CurrentUserId = Guid.Parse(user.Id);

    private async Task<ApplicationUser> CreateUserAsync(TestScope s, string role, Guid branchId)
    {
        var login = role.ToLowerInvariant() + "_" + Suffix();
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
                "Создание пользователя не удалось: " + string.Join("; ", result.Errors.Select(e => e.Description)));
        await s.Users.AddToRoleAsync(user, role);
        return user;
    }

    [Fact]
    public async Task RepeatedRecalculation_CreatesNoNewVersion()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var card = await CreateEquipmentModelDraftAsync(s);
        var first = await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(card.Id, Array.Empty<Guid>()));
        var second = await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(card.Id, Array.Empty<Guid>()));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.TotalCoefficient, second.TotalCoefficient);
        Assert.Equal(first.Rows.Count, second.Rows.Count);

        // Выбор коэффициента/пересчёт не создаёт новую версию ИК.
        var sameCard = await s.Db.IndividualCards.AsNoTracking().FirstAsync(c => c.Id == card.Id);
        Assert.Equal(IndividualCardStatus.Draft, sameCard.Status);
        Assert.Equal(card.Version, sameCard.Version);
    }

    [Fact]
    public async Task FormedCard_RecalculationForbidden_PreservesData()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var card = await CreateEquipmentModelDraftAsync(s);
        await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(card.Id, Array.Empty<Guid>()));
        await s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(card.Id));
        var rowsBefore = await s.Db.IndividualCardItems.CountAsync(i => i.IndividualCardId == card.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.IndividualCards.RecalculateDraftAsync(
                new RecalculateIndividualCardDraftRequest(card.Id, Array.Empty<Guid>())));

        Assert.Equal(rowsBefore, await s.Db.IndividualCardItems.CountAsync(i => i.IndividualCardId == card.Id));
    }

    [Fact]
    public async Task HistoryAndLog_ResolvedNamesWithoutRawId()
    {
        await using var s = Scope();
        var admin = await CreateUserAsync(s, nameof(UserRole.SystemAdmin), _fixture.BranchA);
        SetUser(s, admin);
        var card = await CreateEquipmentModelDraftAsync(s);
        await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(card.Id, Array.Empty<Guid>()));
        await s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(card.Id));

        var detail = await s.IndividualCards.GetDetailAsync(card.Id);

        Assert.NotNull(detail);
        Assert.All(detail!.History, h => Assert.False(string.IsNullOrWhiteSpace(h.CreatedByUserId)));
        // В цепочке версий — ФИО/логин, а не сырой GUID-идентификатор.
        Assert.All(detail.History, h => Assert.DoesNotContain(
            $"{Guid.NewGuid():D}", h.CreatedByUserId));
    }

    [Fact]
    public async Task RecalculatedAudit_WritesTechnicalDetails()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var card = await CreateEquipmentModelDraftAsync(s);

        await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(card.Id, Array.Empty<Guid>()));

        var recalculated = await s.Db.AuditLogs
            .Where(a => a.EntityType == "IndividualCard" && a.EntityId == card.Id.ToString())
            .OrderByDescending(a => a.CreatedAt)
            .FirstAsync(a => a.Action == "IndividualCard.Recalculated");

        Assert.Contains("CoefficientCount", recalculated.Details ?? "");
        Assert.Contains("TotalCoefficient", recalculated.Details ?? "");
        Assert.Contains("CalculationItemCount", recalculated.Details ?? "");
    }

    // ── Display catalog tests ─────────────────────────────────────────────

    [Fact]
    public void DisplayCatalog_FormatTechnicalRecalcDetails()
    {
        var raw = "CoefficientCount=4; TotalCoefficient=1.100000; CalculationItemCount=1; " +
                  "PrimaryMaterialSnapshotCount=0; CalculationProblemCount=2";
        var result = IndividualCardAuditDisplayCatalog.FormatDetails(raw);
        Assert.NotNull(result);
        Assert.Contains("Коэффициентов: 4", result);
        Assert.Contains("Общий коэффициент: 1,10", result);
        Assert.Contains("Расчётных строк: 1", result);
        Assert.Contains("Основных марок ГСМ: 0", result);
        Assert.Contains("Нормативных замечаний: 2", result);
    }

    [Fact]
    public void DisplayCatalog_FormatDraftCreationDetails()
    {
        var raw = "ObjectLevel=Node; " + Guid.NewGuid() + "; BranchId=" + Guid.NewGuid() +
                  "; CompositionCount=0; HKSourceCount=1; NormativeGapCount=0";
        Assert.NotNull(IndividualCardAuditDisplayCatalog.FormatDetails(raw));
    }

    [Fact]
    public void DisplayCatalog_KeylessDetails_RequireNoTransformation()
    {
        Assert.Null(IndividualCardAuditDisplayCatalog.FormatDetails("Ручная причина, без технических ключей"));
        Assert.Null(IndividualCardAuditDisplayCatalog.FormatDetails(null));
        Assert.Null(IndividualCardAuditDisplayCatalog.FormatDetails(" "));
    }

    private async Task<IndividualCard> CreateEquipmentModelDraftAsync(TestScope s)
    {
        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateEquipmentCoreAsync(s);
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
        await AddNodeItemsAsync(s, nodeHK.Id, (auId, 1, 100m, "г", new[] { (primary, GsmCategory.Primary) }));

        await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        var chain = await s.Db.IndividualCards
            .Where(c => c.EquipmentModelId == modelId)
            .OrderByDescending(c => c.CreatedAt)
            .FirstAsync();
        return chain;
    }

    private async Task<Guid> CreateEquipmentCoreAsync(TestScope s)
    {
        var model = new EquipmentModel
        {
            Id = Guid.NewGuid(),
            Index = "EM-" + Suffix(),
            Name = "Изделие " + Suffix(),
            IsDeleted = false,
        };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();
        return model.Id;
    }

private async Task<Guid> CreateAggregateAsync(TestScope s)
    {
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Suffix(), Name = "Агрегат " + Suffix(), IsDeleted = false };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();
        return aggregate.Id;
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

    // auto helpers

private async Task<Guid> CreateNodeAsync(TestScope s)
    {
        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Suffix(), Name = "Узел " + Suffix(), IsDeleted = false };
        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();
        return node.Id;
    }

    }

