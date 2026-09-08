using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Reports;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class IndividualCardPdfReportIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public IndividualCardPdfReportIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

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

    private static string ExtractText(byte[] pdf)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var raw = string.Join("\n", document.GetPages().Select(p =>
            string.Join(" ", p.GetWords().Select(w => w.Text))));
        return string.Join(' ', raw.Split(new[] { ' ', '\r', '\n', '\t' },
            StringSplitOptions.RemoveEmptyEntries));
    }

    private static async Task<IndividualCardExportDto> GetExportAsync(TestScope s, Guid cardId) =>
        (await s.IndividualCards.GetExportAsync(cardId))!;

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

    private async Task<(Guid CardId, Guid ModelId, Guid NodeHKCardId)>
        CreateFormedCardAsync(TestScope s, decimal volume = 100m, bool form = true)
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
                new RecalculateIndividualCardDraftRequest(draft.Id, Array.Empty<Guid>()));
            await s.IndividualCards.FormDraftAsync(new FormIndividualCardRequest(draft.Id));
        }
        return (draft.Id, modelId, nodeHK.Id);
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
        var primary1 = await CreateGsmMaterialAsync(s, name: primaryName1);
        var primary2 = await CreateGsmMaterialAsync(s, name: primaryName2);
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

    // ── 1. Draft PDF ──────────────────────────────────────────────────────

    [Fact]
    public async Task DraftPdf_IsGeneratedWithDraftWarning()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _) = await CreateFormedCardAsync(s, form: false);

        var export = await GetExportAsync(s, cardId);
        var file = IndividualCardPdfComposer.Compose(export);

        Assert.NotEmpty(file.Content);
        Assert.Equal(0x25, file.Content[0]); // '%' — PDF magic
        var text = ExtractText(file.Content);
        Assert.Contains("ЧЕРНОВИК. Данные могут быть изменены.", text);
        Assert.Contains("Нормы расхода ГСМ", text);
    }

    // ── 2. Formed PDF ─────────────────────────────────────────────────────

    [Fact]
    public async Task FormedPdf_ContainsAllSectionsAndRequisites()
    {
        await using var s = Scope();
        var service = new ReportService(s.IndividualCards);
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, modelId, _) = await CreateFormedCardAsync(s);
        var model = await s.Db.EquipmentModels.AsNoTracking().FirstAsync(m => m.Id == modelId);

        var file = (await service.GenerateIndividualCardPdfAsync(cardId))!;
        var text = ExtractText(file.Content);

        Assert.NotEmpty(file.Content);
        Assert.Contains(model.Index, text);
        Assert.Contains(model.Name, text);
        Assert.Contains("Версия конструктивного состава", text);
        Assert.Contains("Нормативные источники ХК", text);
        Assert.Contains("Применённые коэффициенты", text);
        Assert.Contains("Нормы расхода ГСМ", text);
        Assert.Contains("Основные марки ГСМ", text);
        Assert.Contains("История версий", text);
        Assert.Contains("ИНДИВИДУАЛЬНАЯ КАРТА", text);
        Assert.Contains("Разработал:", text);
    }

    // ── 3. Archived PDF ───────────────────────────────────────────────────

    [Fact]
    public async Task ArchivedPdf_HasNoDraftWarning()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _) = await CreateFormedCardAsync(s);
        await s.IndividualCards.ArchiveIndividualCardAsync(cardId);

        var file = IndividualCardPdfComposer.Compose(await GetExportAsync(s, cardId));
        var text = ExtractText(file.Content);

        Assert.DoesNotContain("ЧЕРНОВИК", text);
        Assert.Contains("Архив", text);
    }

    // ── 4. Повторные источники ХК печатаются отдельно ──────────────────────

    [Fact]
    public async Task Pdf_PrintsRepeatedSourceHKOccurrencesSeparately()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var cardId = await CreateRepeatedOccurrenceCardAsync(s);

        var export = await GetExportAsync(s, cardId);
        var nodeCode = export.HKSources.Select(h => h.HKCardCode)
            .First(c => c.StartsWith("HK-Nod"));
        var nodeId = export.HKSources.First(h => h.HKCardCode == nodeCode).SourceHKCardId;

        Assert.Equal(2, export.HKSources.Count(h => h.SourceHKCardId == nodeId));
        Assert.Equal(2, export.Rows.Count(r => r.SourceHKCardCode == nodeCode));

        var text = ExtractText(IndividualCardPdfComposer.Compose(export).Content);

        var occurrences = text.Split(nodeCode).Length - 1;
        Assert.True(occurrences >= 2, $"Ожидалось >= 2 вхождений {nodeCode}, найдено {occurrences}");
    }

    // ── 5. Все четыре категории ГСМ ────────────────────────────────────────

    [Fact]
    public async Task Pdf_ContainsAllFourGsmCategories()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, primary1, primary2, duplicate, reserve, foreign) =
            await CreateMultiCategoryCardAsync(s);

        var text = ExtractText(IndividualCardPdfComposer.Compose(await GetExportAsync(s, cardId)).Content);
        var compact = text.Replace(" ", string.Empty);

        Assert.Contains(primary1, text);
        Assert.Contains(primary2, text);
        Assert.Contains(duplicate, text);
        Assert.Contains(reserve, text);
        Assert.Contains(foreign, text);
        Assert.Contains("Основные марки ГСМ", text);
        Assert.Contains("Дублирующие", text);
        Assert.Contains("Резервные", text);
        Assert.Contains("Зарубежные", text);
    }

    // ── 6. Несколько Primary отдельно, без общего итога ────────────────────

    [Fact]
    public async Task Pdf_PrimaryBrandsSeparateWithoutGrandTotal()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, primary1, primary2, _, _, _) = await CreateMultiCategoryCardAsync(s);

        var text = ExtractText(IndividualCardPdfComposer.Compose(await GetExportAsync(s, cardId)).Content);

        Assert.Contains(primary1, text);
        Assert.Contains(primary2, text);
        Assert.DoesNotContain("Итого", text);
        Assert.DoesNotContain("Всего", text);
        Assert.DoesNotContain("альтернативы", text);
        Assert.DoesNotContain("не суммируются", text);
    }

    // ── 7. Live-переименования не влияют на PDF ────────────────────────────

    [Fact]
    public async Task Pdf_UsesImmutableExportValuesAfterLiveRename()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, modelId, nodeHKId) = await CreateFormedCardAsync(s);
        var export = await GetExportAsync(s, cardId);

        var model = await s.Db.EquipmentModels.FirstAsync(m => m.Id == modelId);
        model.Name = "Переименованное изделие";
        var nodeHK = await s.Db.HKCards.FirstAsync(h => h.Id == nodeHKId);
        nodeHK.Code = "HK-LIVE-" + Suffix();
        nodeHK.Version = "v" + Suffix()[..4];
        await s.Db.SaveChangesAsync();

        var text = ExtractText(IndividualCardPdfComposer.Compose(export).Content);

        Assert.Contains(export.TargetObjectName, text);
        Assert.DoesNotContain("Переименованное изделие", text);
        var rowCode = export.Rows[0].SourceHKCardCode;
        Assert.Contains(rowCode, text);
        Assert.DoesNotContain(nodeHK.Code, text);
    }

    // ── 8. Авторизация на уровне ReportService ─────────────────────────────

    [Fact]
    public async Task ForeignBranchOrDeniedUser_CannotGeneratePdf()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var service = new ReportService(s.IndividualCards);
        var (cardId, _, _) = await CreateFormedCardAsync(s);

        var foreign = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchB);
        await GrantAsync(s, foreign, PermissionCodes.IndividualCardView);
        SetUser(s, foreign);
        Assert.Null(await service.GenerateIndividualCardPdfAsync(cardId));

        var denied = await CreateUserAsync(s, nameof(UserRole.NormAdmin), _fixture.BranchA);
        await DenyAsync(s, denied, PermissionCodes.IndividualCardView);
        SetUser(s, denied);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GenerateIndividualCardPdfAsync(cardId));

        Assert.Equal(0, await CountAuditsAsync(s, cardId, "IndividualCard.PdfExported"));
    }

    // ── 9. Успешная генерация → ровно один PdfExported аудит ───────────────

    [Fact]
    public async Task SuccessfulGeneration_WritesSinglePdfExportedAudit()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var service = new ReportService(s.IndividualCards);
        var (cardId, _, _) = await CreateFormedCardAsync(s);

        var file = await service.GenerateIndividualCardPdfAsync(cardId);

        Assert.NotNull(file);
        Assert.NotEmpty(file!.Content);
        Assert.Equal(1, await CountAuditsAsync(s, cardId, "IndividualCard.PdfExported"));
    }

    // ── 10. Ошибка рендера → аудит не пишется ──────────────────────────────

    [Fact]
    public async Task RendererException_WritesNoPdfExportedAudit()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _) = await CreateFormedCardAsync(s);

        var broken = await GetExportAsync(s, cardId);
        var invalid = new IndividualCardExportDto(
            broken.IndividualCardId, broken.Code, broken.Version, broken.RevisionNumber,
            broken.Status, broken.StatusDisplay, broken.ObjectLevel, broken.ObjectLevelDisplay,
            broken.TargetObjectCode, broken.TargetObjectName, broken.TargetContext,
            broken.BranchId, broken.BranchName, broken.CreatedByUserId, broken.CreatedByName,
            broken.CreatedAt, broken.FormedAt, broken.ArchivedAt,
            Warnings: null!,
            broken.Compositions, broken.HKSources, broken.Coefficients, broken.TotalCoefficient,
            broken.Rows, broken.PrimaryMaterials, broken.History);

        Assert.ThrowsAny<Exception>(() => IndividualCardPdfComposer.Compose(invalid));
        Assert.Equal(0, await CountAuditsAsync(s, cardId, "IndividualCard.PdfExported"));
    }

    // ── 11. Безопасное имя файла ───────────────────────────────────────────

    [Fact]
    public void FileName_IsSafeAndUsesImmutableCodeVersion()
    {
        var name = IndividualCardPdfComposer.BuildFileName("ИК-12/34:56", "v 7 (1)");

        Assert.EndsWith(".pdf", name);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain(":", name);
        Assert.DoesNotContain(" ", name);
        Assert.DoesNotContain("(", name);
        Assert.Matches(@"^[\w\-]+\.pdf$", name);
    }

    // ── 12. Без технических терминов и GUID ────────────────────────────────

    [Fact]
    public async Task PdfText_HasNoTechnicalTermsOrRawGuids()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var (cardId, _, _) = await CreateFormedCardAsync(s);

        var export = await GetExportAsync(s, cardId);
        var text = ExtractText(IndividualCardPdfComposer.Compose(export).Content);

        Assert.DoesNotContain("snapshot", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preflight", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("occurrence", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("immutable", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("снапшот", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(cardId.ToString("D"), text);
        Assert.DoesNotContain(export.HKSources[0].PreflightOccurrenceId.ToString("D"), text);
        Assert.DoesNotContain(export.Compositions[0].SourceCompositionId.ToString("D"), text);
    }
}
