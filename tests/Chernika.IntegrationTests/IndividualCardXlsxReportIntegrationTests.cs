using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Reports;
using Chernika.Infrastructure.Services;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class IndividualCardXlsxReportIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public IndividualCardXlsxReportIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

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

    private static XLWorkbook OpenWorkbook(byte[] xlsx) => new(new MemoryStream(xlsx));

    private static string FindText(XLWorkbook wb, string contains)
    {
        var ws = wb.Worksheet("ИК");
        foreach (var cell in ws.CellsUsed())
        {
            if (cell.GetString().Contains(contains, StringComparison.Ordinal))
                return cell.GetString();
        }
        return string.Empty;
    }

    private static IXLCell? FindCell(XLWorkbook wb, string contains)
    {
        var ws = wb.Worksheet("ИК");
        foreach (var cell in ws.CellsUsed())
        {
            if (cell.GetString().Contains(contains, StringComparison.Ordinal))
                return cell;
        }
        return null;
    }

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

    private async Task<(Guid CardId, Guid ModelId, Guid NodeHKCardId)> CreateFormedCardAsync(
        TestScope s, decimal volume = 100m, bool form = true, Guid? coefficientId = null)
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
        return (draft.Id, modelId, nodeHK.Id);
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

    private async Task<(Guid CardId, string PrimaryName1, string PrimaryName2, string DuplicateName, string ReserveName, string ForeignName)>
        CreateMultiCategoryCardAsync(TestScope s)
    {
        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateEquipmentAsync(s);
        var aggregateId = await CreateAggregateAsync(s);
        var nodeId = await CreateNodeAsync(s);
        var auId = await CreateAssemblyUnitAsync(s);
        var primaryName1 = "ГСМ осн " + Suffix();
        var primaryName2 = "ГСМ осн2 " + Suffix();
        var duplicateName = "ГСМ дубл " + Suffix();
        var reserveName = "ГСМ рез " + Suffix();
        var foreignName = "ГСМ зар " + Suffix();
        var primary1 = await CreateGsmMaterialAsync(s, name: primaryName1, gost: "ГОСТ-" + Suffix());
        var primary2 = await CreateGsmMaterialAsync(s, name: primaryName2, gost: "ГОСТ-" + Suffix());
        var duplicate = await CreateGsmMaterialAsync(s, name: duplicateName, gost: "ГОСТ-" + Suffix());
        var reserve = await CreateGsmMaterialAsync(s, name: reserveName, gost: "ГОСТ-" + Suffix());
        var foreign = await CreateGsmMaterialAsync(s, name: foreignName, gost: "ГОСТ-" + Suffix());
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
                (primary1, GsmCategory.Primary),
                (primary2, GsmCategory.Primary),
                (duplicate, GsmCategory.Duplicate),
                (reserve, GsmCategory.Reserve),
                (foreign, GsmCategory.Foreign),
            }));

        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.EquipmentModel, modelId));
        await s.IndividualCards.RecalculateDraftAsync(
            new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));
        await s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id));
        return (draft.Id, primaryName1, primaryName2, duplicateName, reserveName, foreignName);
    }

    private async Task<Guid> CreateRepeatedOccurrenceCardAsync(TestScope s)
    {
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
        return draft.Id;
    }

    // ── 1/2/3. Статусы и лист ─────────────────────────────────────────────

    [Fact]
    public async Task DraftXlsx_GeneratedWithDraftWarning()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _) = await CreateFormedCardAsync(s, form: false);

        var export = (await s.IndividualCards.GetExportAsync(cardId))!;
        using var wb = OpenWorkbook(IndividualCardXlsxComposer.Compose(export).Content);

        Assert.Single(wb.Worksheets);
        Assert.Equal("ИК", wb.Worksheets.First().Name);
        Assert.NotEmpty(FindText(wb, "ЧЕРНОВИК. Данные могут быть изменены."));
    }

    [Fact]
    public async Task FormedXlsx_HasAllSections()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, modelId, _) = await CreateFormedCardAsync(s);
        var model = await s.Db.EquipmentModels.AsNoTracking().FirstAsync(m => m.Id == modelId);

        using var wb = OpenWorkbook(IndividualCardXlsxComposer.Compose((await s.IndividualCards.GetExportAsync(cardId))!).Content);

        Assert.Single(wb.Worksheets);
        Assert.Equal("ИК", wb.Worksheets.First().Name);
        Assert.NotEmpty(FindText(wb, model.Index));
        Assert.NotEmpty(FindText(wb, model.Name));
        foreach (var title in new[]
                 {
                     "Версия конструктивного состава", "Нормативные источники ХК", "Применённые коэффициенты",
                     "Нормы расхода ГСМ", "Основные марки ГСМ", "История версий",
                 })
        {
            Assert.NotEmpty(FindText(wb, title));
        }
    }

    [Fact]
    public async Task ArchivedXlsx_HasNoDraftWarning()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _) = await CreateFormedCardAsync(s);
        await s.IndividualCards.ArchiveIndividualCardAsync(cardId);

        using var wb = OpenWorkbook(IndividualCardXlsxComposer.Compose((await s.IndividualCards.GetExportAsync(cardId))!).Content);

        Assert.Empty(FindText(wb, "ЧЕРНОВИК"));
    }

    // ── 5. Повторные источники ХК ─────────────────────────────────────────

    [Fact]
    public async Task Xlsx_PrintsRepeatedSourceHKOccurrencesSeparately()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var cardId = await CreateRepeatedOccurrenceCardAsync(s);

        var export = (await s.IndividualCards.GetExportAsync(cardId))!;
        var nodeCode = export.HKSources.Select(h => h.HKCardCode).First(c => c.StartsWith("HK-Nod"));
        using var wb = OpenWorkbook(IndividualCardXlsxComposer.Compose(export).Content);
        var ws = wb.Worksheet("ИК");

        var count = ws.CellsUsed().Count(c => c.GetString().Contains(nodeCode, StringComparison.Ordinal));
        Assert.True(count >= 2, $"Ожидалось >= 2 вхождений {nodeCode}, найдено {count}");
    }

    // ── 6/7. Категории и несколько Primary ────────────────────────────────

    [Fact]
    public async Task Xlsx_ContainsAllFourCategoriesSeparately()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, primary1, primary2, duplicate, reserve, foreign) = await CreateMultiCategoryCardAsync(s);

        using var wb = OpenWorkbook(IndividualCardXlsxComposer.Compose((await s.IndividualCards.GetExportAsync(cardId))!).Content);
        var ws = wb.Worksheet("ИК");

        var allText = string.Join("\n", ws.CellsUsed().Select(c => c.GetString()));
        Assert.Contains(primary1, allText);
        Assert.Contains(primary2, allText);
        Assert.Contains(duplicate, allText);
        Assert.Contains(reserve, allText);
        Assert.Contains(foreign, allText);
        Assert.Contains(primary2 + "\nГОСТ/ТУ", allText);

        // Несколько Primary — отдельные строки, без общего итога по маркам.
        Assert.DoesNotContain("Итого", allText);
        Assert.DoesNotContain("Всего", allText);
    }

    // ── 8/9/10. Формулы воспроизводят снимок ───────────────────────────────

    [Fact]
    public async Task Xlsx_FormulasReproduceSnapshotValues()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var typeId = await CreateCoefficientTypeAsync(s, "Климат " + Suffix());
        var coefficientId = await CreateCoefficientAsync(s, typeId, "Коэф " + Suffix(), 1.25m);
        var (cardId, _, _) = await CreateFormedCardAsync(s, volume: 200m, coefficientId: coefficientId);

        var export = (await s.IndividualCards.GetExportAsync(cardId))!;
        using var wb = OpenWorkbook(IndividualCardXlsxComposer.Compose(export).Content);
        var ws = wb.Worksheet("ИК");

        var totalCell = FindCell(wb, "Общий коэффициент:");
        Assert.NotNull(totalCell);
        var totalRow = totalCell!.Address.RowNumber;

        var formulaCells = ws.CellsUsed()
            .Where(c => c.HasFormula && c.FormulaA1.StartsWith("ROUNDUP", StringComparison.Ordinal))
            .ToList();
        var calcCell = Assert.Single(formulaCells);
        var r = calcCell.Address.RowNumber;

        Assert.Equal($"ROUNDUP(M{r}*N{r},0)", calcCell.FormulaA1);
        Assert.Equal($"$D${totalRow}", ws.Cell(r, 14).FormulaA1);
        Assert.Equal($"I{r}*C{r}*J{r}*K{r}*L{r}", ws.Cell(r, 13).FormulaA1);

        Assert.Equal(export.Rows[0].SourceVolume, ws.Cell(r, 9).GetValue<decimal>());
        Assert.Equal(export.Rows[0].AssemblyUnitQuantity, ws.Cell(r, 3).GetValue<int>());
        Assert.Equal(export.Rows[0].NodeQuantity, ws.Cell(r, 10).GetValue<int>());
        Assert.Equal(export.Rows[0].AggregateQuantity, ws.Cell(r, 11).GetValue<int>());
        Assert.Equal(export.Rows[0].ProductQuantity, ws.Cell(r, 12).GetValue<int>());

        var baseVolume = export.Rows[0].SourceVolume
            * export.Rows[0].AssemblyUnitQuantity
            * export.Rows[0].NodeQuantity
            * export.Rows[0].AggregateQuantity
            * export.Rows[0].ProductQuantity;
        Assert.Equal(export.Rows[0].BaseVolume, baseVolume);
        Assert.Equal(export.TotalCoefficient, export.Coefficients.Single().Value);
        Assert.Equal(export.Rows[0].CalculatedVolume, decimal.Ceiling(baseVolume * export.TotalCoefficient));
    }

    // ── 11/12/13. Защита и разблокировка ──────────────────────────────────

    [Fact]
    public async Task Xlsx_UnlocksEditableCellsAndLocksRest()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var typeId = await CreateCoefficientTypeAsync(s, "Климат " + Suffix());
        var coefficientId = await CreateCoefficientAsync(s, typeId, "Коэф " + Suffix(), 1.25m);
        var (cardId, _, _) = await CreateFormedCardAsync(s, coefficientId: coefficientId);

        using var wb = OpenWorkbook(IndividualCardXlsxComposer.Compose((await s.IndividualCards.GetExportAsync(cardId))!).Content);
        var ws = wb.Worksheet("ИК");

        Assert.True(ws.Protection.IsProtected);

        var coefficientValueCells = ws.CellsUsed()
            .Where(c => c.Address.ColumnNumber == 4 && c.Style.Protection.Locked == false)
            .ToList();
        Assert.NotEmpty(coefficientValueCells);

        var mainRow = ws.CellsUsed()
            .First(c => c.HasFormula && c.FormulaA1.StartsWith("ROUNDUP", StringComparison.Ordinal))
            .Address.RowNumber;
        foreach (var col in new[] { 3, 10, 11, 12 })
            Assert.False(ws.Cell(mainRow, col).Style.Protection.Locked);
        foreach (var col in new[] { 2, 4, 5, 6, 7, 8, 9, 13, 14, 15, 16, 17, 18 })
            Assert.True(ws.Cell(mainRow, col).Style.Protection.Locked);
    }

    // ── 14. Автофильтр и закрепление ──────────────────────────────────────

    [Fact]
    public async Task Xlsx_HasAutoFilterAndFrozenHeader()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _) = await CreateFormedCardAsync(s);

        using var wb = OpenWorkbook(IndividualCardXlsxComposer.Compose((await s.IndividualCards.GetExportAsync(cardId))!).Content);
        var ws = wb.Worksheet("ИК");

        Assert.NotNull(ws.AutoFilter.Range);
        Assert.True(ws.SheetView.SplitRow > 0);
    }

    // ── 15. Live-переименования ───────────────────────────────────────────

    [Fact]
    public async Task Xlsx_UsesImmutableExportValuesAfterLiveRename()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, modelId, nodeHKId) = await CreateFormedCardAsync(s);
        var export = (await s.IndividualCards.GetExportAsync(cardId))!;

        var model = await s.Db.EquipmentModels.FirstAsync(m => m.Id == modelId);
        model.Name = "Переименовано изделие";
        var nodeHK = await s.Db.HKCards.FirstAsync(h => h.Id == nodeHKId);
        nodeHK.Code = "HK-LIVE-" + Suffix();
        nodeHK.Version = "v" + Suffix()[..4];
        await s.Db.SaveChangesAsync();

        var after = (await s.IndividualCards.GetExportAsync(cardId))!;
        using var wb = OpenWorkbook(IndividualCardXlsxComposer.Compose(after).Content);
        var allText = string.Join("\n", wb.Worksheet("ИК").CellsUsed().Select(c => c.GetString()));

        Assert.Contains(export.TargetObjectName, allText);
        Assert.DoesNotContain("Переименовано изделие", allText);
        Assert.Contains(export.Rows[0].SourceHKCardCode, allText);
        Assert.DoesNotContain(nodeHK.Code, allText);
    }

    // ── 16. Авторизация ───────────────────────────────────────────────────

    [Fact]
    public async Task ForeignBranchOrDeniedUser_CannotGenerateXlsx()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var service = new ReportService(s.IndividualCards);
        var (cardId, _, _) = await CreateFormedCardAsync(s);

        var foreign = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchB);
        await GrantAsync(s, foreign, PermissionCodes.IndividualCardView);
        SetUser(s, foreign);
        Assert.Null(await service.GenerateIndividualCardXlsxAsync(cardId));

        var denied = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchA);
        await DenyAsync(s, denied, PermissionCodes.IndividualCardView);
        SetUser(s, denied);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GenerateIndividualCardXlsxAsync(cardId));

        Assert.Equal(0, await CountAuditsAsync(s, cardId, "IndividualCard.XlsxExported"));
    }

    // ── 17. Аудит ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SuccessfulGeneration_WritesSingleXlsxExportedAudit()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var service = new ReportService(s.IndividualCards);
        var (cardId, _, _) = await CreateFormedCardAsync(s);

        var file = await service.GenerateIndividualCardXlsxAsync(cardId);

        Assert.NotNull(file);
        Assert.NotEmpty(file!.Content);
        Assert.Equal(1, await CountAuditsAsync(s, cardId, "IndividualCard.XlsxExported"));
    }

    // ── 18. Ошибка рендера без аудита ─────────────────────────────────────

    [Fact]
    public async Task RendererException_WritesNoXlsxExportedAudit()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _) = await CreateFormedCardAsync(s);

        var broken = (await s.IndividualCards.GetExportAsync(cardId))!;
        var invalid = new IndividualCardExportDto(
            broken.IndividualCardId, broken.Code, broken.Version, broken.RevisionNumber,
            broken.Status, broken.StatusDisplay, broken.ObjectLevel, broken.ObjectLevelDisplay,
            broken.TargetObjectCode, broken.TargetObjectName, broken.TargetContext,
            broken.BranchId, broken.BranchName, broken.CreatedByUserId, broken.CreatedByName,
            broken.CreatedAt, broken.FormedAt, broken.ArchivedAt,
            Warnings: null!,
            broken.Compositions, broken.HKSources, broken.Coefficients, broken.TotalCoefficient,
            broken.Rows, broken.PrimaryMaterials, broken.History);

        Assert.ThrowsAny<Exception>(() => IndividualCardXlsxComposer.Compose(invalid));
        Assert.Equal(0, await CountAuditsAsync(s, cardId, "IndividualCard.XlsxExported"));
    }

    // ── 19. Имя файла ─────────────────────────────────────────────────────

    [Fact]
    public void FileName_IsSafeAndUsesImmutableCodeVersion()
    {
        var name = ReportFileNameBuilder.Build("ИК-12/34:56", "v 7 (1)", ".xlsx");

        Assert.EndsWith(".xlsx", name);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain(":", name);
        Assert.DoesNotContain(" ", name);
        Assert.Matches(@"^[\w\-]+\.xlsx$", name);
    }

    // ── 20. Без технических терминов и GUID ───────────────────────────────

    [Fact]
    public async Task XlsxText_HasNoTechnicalTermsOrRawGuids()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _) = await CreateFormedCardAsync(s);

        var export = (await s.IndividualCards.GetExportAsync(cardId))!;
        using var wb = OpenWorkbook(IndividualCardXlsxComposer.Compose(export).Content);
        var allText = string.Join("\n", wb.Worksheet("ИК").CellsUsed().Select(c => c.GetString()));

        Assert.DoesNotContain("snapshot", allText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preflight", allText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("occurrence", allText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("immutable", allText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("снапшот", allText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(cardId.ToString("D"), allText);
        Assert.DoesNotContain(export.HKSources[0].PreflightOccurrenceId.ToString("D"), allText);
    }
}
