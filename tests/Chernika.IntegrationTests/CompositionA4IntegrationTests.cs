using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class CompositionA4IntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public CompositionA4IntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AddAggregate_Ungrouped_Succeeds_WithPartIdNull()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, _) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateEntityAsync(s, "A-UNG");

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var pca = await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, null, aggregate.Id, 2));

        Assert.Equal(compositionId, pca.ProductCompositionId);
        Assert.Null(pca.PartId);
        Assert.Equal(2, pca.Quantity);
    }

    [Fact]
    public async Task AddAggregate_DuplicateInSameNamedPart_Rejected()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateEntityAsync(s, "A-DUP");

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 1));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 2)));
        Assert.Contains("уже добавлен", ex.Message);
    }

    [Fact]
    public async Task AddAggregate_SameAggregateInTwoDifferentParts_Succeeds()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var part2 = await s.Equipment.AddPartAsync(new AddPartRequest(compositionId, "Вторая часть", null, 2));
        var aggregate = await AddAggregateEntityAsync(s, "A-SHARED");

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var first = await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 1));
        var second = await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, part2.Id, aggregate.Id, 1));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(partId, first.PartId);
        Assert.Equal(part2.Id, second.PartId);
    }

    [Fact]
    public async Task AddAggregate_SameAggregateInUngroupedAndNamedPart_Succeeds()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateEntityAsync(s, "A-MIX");

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var ungrouped = await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, null, aggregate.Id, 1));
        var inPart = await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 2));

        Assert.Null(ungrouped.PartId);
        Assert.Equal(partId, inPart.PartId);
    }

    [Fact]
    public async Task AddAggregate_DuplicateInUngrouped_Rejected()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, _) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateEntityAsync(s, "A-DUP-UNG");

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, null, aggregate.Id, 1));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, null, aggregate.Id, 2)));
        Assert.Contains("Без группы", ex.Message);
    }

    [Fact]
    public async Task MoveAggregate_BetweenGroups_PreservesQuantityAndWritesAudit()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var part2 = await s.Equipment.AddPartAsync(new AddPartRequest(compositionId, "Вторая часть", null, 2));
        var aggregate = await AddAggregateEntityAsync(s, "A-MOVE");

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var pca = await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 5));

        var moved = await s.Equipment.MoveProductCompositionAggregateAsync(pca.Id, null);
        Assert.True(moved);

        var afterUngrouped = await s.Db.ProductCompositionAggregates.AsNoTracking()
            .SingleAsync(a => a.Id == pca.Id);
        Assert.Null(afterUngrouped.PartId);
        Assert.Equal(5, afterUngrouped.Quantity);

        var movedToPart = await s.Equipment.MoveProductCompositionAggregateAsync(pca.Id, part2.Id);
        Assert.True(movedToPart);

        var afterPart = await s.Db.ProductCompositionAggregates.AsNoTracking()
            .SingleAsync(a => a.Id == pca.Id);
        Assert.Equal(part2.Id, afterPart.PartId);

        Assert.True(await s.Db.AuditLogs.AnyAsync(a =>
            a.EntityType == "ProductCompositionAggregate" &&
            a.EntityId == pca.Id.ToString() &&
            a.Action == "ProductComposition.AggregateMoved"));
    }

    [Fact]
    public async Task MoveAggregate_ToDuplicateTarget_Rejected()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateEntityAsync(s, "A-MOVE-DUP");

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var inPart = await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 1));
        var ungrouped = await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, null, aggregate.Id, 1));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Equipment.MoveProductCompositionAggregateAsync(ungrouped.Id, partId));
        Assert.Contains("уже присутствует в целевой части", ex.Message);
    }

    [Fact]
    public async Task RemovePart_EmptyPart_Works()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, _) = await CreateProductCompositionDraftAsync(s);
        var emptyPart = await s.Equipment.AddPartAsync(new AddPartRequest(compositionId, "Пустая часть", null, 2));

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var ok = await s.Equipment.RemovePartAsync(emptyPart.Id);
        Assert.True(ok);

        var detail = await s.Equipment.GetProductCompositionDetailAsync(compositionId);
        Assert.DoesNotContain(detail!.Parts, p => p.Id == emptyPart.Id);
    }

    [Fact]
    public async Task RemovePart_NonEmptyPart_MovesAggregatesToUngrouped()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateEntityAsync(s, "A-PART-DEL");

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var pca = await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 3));

        var ok = await s.Equipment.RemovePartAsync(partId);
        Assert.True(ok);

        var after = await s.Db.ProductCompositionAggregates.AsNoTracking().SingleAsync(a => a.Id == pca.Id);
        Assert.Null(after.PartId);
        Assert.Equal(3, after.Quantity);

        var detail = await s.Equipment.GetProductCompositionDetailAsync(compositionId);
        Assert.Single(detail!.UngroupedAggregates);
    }

    [Fact]
    public async Task RemovePart_ConflictWithExistingUngrouped_Rejected()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateEntityAsync(s, "A-PART-CONF");

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, null, aggregate.Id, 1));
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 2));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Equipment.RemovePartAsync(partId));
        Assert.Contains("Без группы", ex.Message);
    }

    [Fact]
    public async Task RemovePart_DraftOnly_AndBranchProtection()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateEntityAsync(s, "A-PART-PROT");

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 1));
        await s.Equipment.SubmitForReviewAsync(compositionId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Equipment.RemovePartAsync(partId));
    }

    [Fact]
    public async Task Registry_Default_ReturnsObjectsWithAndWithoutComposition()
    {
        await using var s = _fixture.CreateScope();
        var modelWith = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-REG-W", Name = "Изделие с составом" };
        var modelWithout = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-REG-N", Name = "Изделие без состава" };
        s.Db.EquipmentModels.AddRange(modelWith, modelWithout);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(modelWith.Id, null));

        var result = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.EquipmentModel,
            Presence = CompositionPresenceFilter.All
        });

        Assert.Contains(result.Items, r => r.ObjectId == modelWith.Id && r.CompositionId.HasValue);
        Assert.Contains(result.Items, r => r.ObjectId == modelWithout.Id && !r.CompositionId.HasValue);
    }

    [Fact]
    public async Task Registry_PresenceFilters_AreCorrect()
    {
        await using var s = _fixture.CreateScope();
        var withComp = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-WITH", Name = "With" };
        var withoutComp = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-WITHOUT", Name = "Without" };
        s.Db.EquipmentModels.AddRange(withComp, withoutComp);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(withComp.Id, null));

        var with = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.EquipmentModel,
            Presence = CompositionPresenceFilter.WithComposition
        });
        Assert.Contains(with.Items, r => r.ObjectId == withComp.Id);
        Assert.DoesNotContain(with.Items, r => r.ObjectId == withoutComp.Id);

        var without = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.EquipmentModel,
            Presence = CompositionPresenceFilter.WithoutComposition
        });
        Assert.Contains(without.Items, r => r.ObjectId == withoutComp.Id);
        Assert.DoesNotContain(without.Items, r => r.ObjectId == withComp.Id);
    }

    [Fact]
    public async Task Registry_GlobalSearch_MatchesCodeAndVersion()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-SEARCH-IDX", Name = "Поисковое изделие" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var draft = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, null));

        var byCode = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.EquipmentModel,
            SearchText = "T-SEARCH-IDX",
            SearchAllLevels = true
        });
        Assert.Contains(byCode.Items, r => r.ObjectId == model.Id);

        var byVersion = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.EquipmentModel,
            SearchText = draft.Version,
            SearchAllLevels = true
        });
        Assert.Contains(byVersion.Items, r => r.ObjectId == model.Id);
    }

    [Fact]
    public async Task Registry_SearchAllLevels_False_SearchesOnlyCurrentLevel()
    {
        await using var s = _fixture.CreateScope();
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-LEV", Name = "Агрегат уровня" };
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-LEV", Name = "Изделие уровня" };
        s.Db.Aggregates.Add(aggregate);
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, null));

        var aggregateResult = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.Aggregate,
            SearchText = "T-LEV",
            SearchAllLevels = false
        });
        Assert.DoesNotContain(aggregateResult.Items, r => r.ObjectCode == "T-LEV");

        var modelResult = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.EquipmentModel,
            SearchText = "T-LEV",
            SearchAllLevels = false
        });
        Assert.Contains(modelResult.Items, r => r.ObjectCode == "T-LEV");
    }

    [Fact]
    public async Task Registry_SearchAllLevels_True_FindsAllLevels()
    {
        await using var s = _fixture.CreateScope();
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-ALL", Name = "Агрегат общий" };
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-ALL", Name = "Изделие общий" };
        var complex = new Complex { Id = Guid.NewGuid(), Code = "C-ALL", Name = "Комплекс общий" };
        s.Db.Aggregates.Add(aggregate);
        s.Db.EquipmentModels.Add(model);
        s.Db.Complexes.Add(complex);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, null));

        var result = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.Aggregate,
            SearchText = "A-ALL",
            SearchAllLevels = true
        });
        Assert.Contains(result.Items, r => r.Level == CompositionRegistryLevel.Aggregate && r.ObjectCode == "A-ALL");

        var modelSearch = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.Aggregate,
            SearchText = "T-ALL",
            SearchAllLevels = true
        });
        Assert.Contains(modelSearch.Items, r => r.Level == CompositionRegistryLevel.EquipmentModel && r.ObjectCode == "T-ALL");

        var complexSearch = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.Aggregate,
            SearchText = "C-ALL",
            SearchAllLevels = true
        });
        Assert.Contains(complexSearch.Items, r => r.Level == CompositionRegistryLevel.Complex && r.ObjectCode == "C-ALL");
    }

    [Fact]
    public async Task Registry_SearchAllLevels_True_EmptySearchText_ReturnsCurrentLevelOnly()
    {
        await using var s = _fixture.CreateScope();
        var otherLevelModel = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-OTHER", Name = "Другое изделие" };
        s.Db.EquipmentModels.Add(otherLevelModel);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var result = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.Aggregate,
            SearchText = "",
            SearchAllLevels = true
        });
        Assert.DoesNotContain(result.Items, r => r.Level == CompositionRegistryLevel.EquipmentModel);
    }

    [Fact]
    public async Task Registry_Default_OneOperationalRowPerObject_WithArchivedHidden()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-ARCH", Name = "Архивное изделие" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (approvedId, _, _) = await CreateApprovedProductCompositionAsync(s, model.Id);
        await s.Equipment.ArchiveCompositionAsync(approvedId);

        var result = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.EquipmentModel,
            ShowArchivedVersions = false
        });

        var operational = result.Items.Where(r => r.ObjectId == model.Id && !r.IsHistoricalArchiveRow).ToList();
        Assert.Single(operational);
        Assert.Null(operational[0].CompositionId);
        Assert.Null(operational[0].Status);

        Assert.DoesNotContain(result.Items, r => r.ObjectId == model.Id && r.IsHistoricalArchiveRow);
    }

    [Fact]
    public async Task Registry_ShowArchivedVersions_AddsHistoricalRows()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-ARCH-VIS", Name = "Изделие с архивом" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (approvedId, _, _) = await CreateApprovedProductCompositionAsync(s, model.Id);
        await s.Equipment.ArchiveCompositionAsync(approvedId);

        var result = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.EquipmentModel,
            ShowArchivedVersions = true
        });

        Assert.Contains(result.Items, r => r.ObjectId == model.Id && r.IsHistoricalArchiveRow && r.Status == ProductCompositionStatus.Archived);
        Assert.Contains(result.Items, r => r.ObjectId == model.Id && !r.IsHistoricalArchiveRow);
    }

    [Fact]
    public async Task Registry_BranchIsolation_NormAdminBDoesNotSeeBranchAComposition()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-BRISO", Name = "Изделие филиала А" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, null));

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminB.Id);
        var result = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.EquipmentModel,
            Presence = CompositionPresenceFilter.All
        });

        var row = result.Items.FirstOrDefault(r => r.ObjectId == model.Id);
        Assert.NotNull(row);
        Assert.Null(row.CompositionId);
        Assert.Null(row.Status);
    }

    [Fact]
    public async Task Registry_BranchIsolation_NormAdminB_CannotRequestBranchA()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminB.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
            {
                Level = CompositionRegistryLevel.EquipmentModel,
                BranchId = _fixture.BranchA
            }));
    }

    [Fact]
    public async Task Registry_BranchIsolation_SystemAdmin_CanFilterByBranch()
    {
        await using var s = _fixture.CreateScope();
        var modelA = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-SA-A", Name = "Изделие А" };
        var modelB = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-SA-B", Name = "Изделие Б" };
        s.Db.EquipmentModels.AddRange(modelA, modelB);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(modelA.Id, null));

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminB.Id);
        await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(modelB.Id, null));

        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        var branchAResult = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.EquipmentModel,
            BranchId = _fixture.BranchA
        });
        Assert.Contains(branchAResult.Items, r => r.ObjectId == modelA.Id && r.CompositionId.HasValue);
        Assert.DoesNotContain(branchAResult.Items, r => r.ObjectId == modelB.Id && r.CompositionId.HasValue);

        var branchBResult = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.EquipmentModel,
            BranchId = _fixture.BranchB
        });
        Assert.Contains(branchBResult.Items, r => r.ObjectId == modelB.Id && r.CompositionId.HasValue);
        Assert.DoesNotContain(branchBResult.Items, r => r.ObjectId == modelA.Id && r.CompositionId.HasValue);
    }

    [Fact]
    public async Task Registry_Pagination_ServerSide_ReturnsCorrectPage()
    {
        await using var s = _fixture.CreateScope();
        var existingCount = await s.Db.EquipmentModels.CountAsync(m => !m.IsDeleted);
        for (int i = 0; i < 5; i++)
        {
            var m = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-PAG-" + i.ToString("D2"), Name = "Пагинация " + i };
            s.Db.EquipmentModels.Add(m);
        }
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var all = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.EquipmentModel,
            PageSize = 2,
            Page = 1,
            SortBy = "name"
        });
        Assert.Equal(existingCount + 5, all.TotalCount);
        Assert.Equal(2, all.Items.Count);

        var page2 = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.EquipmentModel,
            PageSize = 2,
            Page = 2,
            SortBy = "name"
        });
        Assert.Equal(2, page2.Items.Count);
        Assert.DoesNotContain(page2.Items, r => all.Items.Any(a => a.ObjectId == r.ObjectId));
    }

    [Fact]
    public async Task Registry_ArchivedStatusFilter_WithoutShowArchived_ReturnsNothing()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-ARCHF", Name = "Архивное изделие" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (approvedId, _, _) = await CreateApprovedProductCompositionAsync(s, model.Id);
        await s.Equipment.ArchiveCompositionAsync(approvedId);

        var result = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.EquipmentModel,
            Status = ProductCompositionStatus.Archived,
            ShowArchivedVersions = false
        });
        Assert.DoesNotContain(result.Items, r => r.ObjectId == model.Id);
    }

    [Fact]
    public async Task Registry_ArchivedStatusFilter_WithShowArchived_ReturnsOnlyArchiveRows()
    {
        await using var s = _fixture.CreateScope();
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-ARCHF2", Name = "Изделие с архивом" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (approvedId, _, _) = await CreateApprovedProductCompositionAsync(s, model.Id);
        await s.Equipment.ArchiveCompositionAsync(approvedId);

        var result = await s.Equipment.GetCompositionRegistryAsync(new CompositionRegistryQuery
        {
            Level = CompositionRegistryLevel.EquipmentModel,
            Status = ProductCompositionStatus.Archived,
            ShowArchivedVersions = true
        });
        Assert.Contains(result.Items, r => r.ObjectId == model.Id && r.IsHistoricalArchiveRow && r.Status == ProductCompositionStatus.Archived);
        Assert.DoesNotContain(result.Items, r => r.ObjectId == model.Id && !r.IsHistoricalArchiveRow);
    }

    [Fact]
    public async Task RemovePart_NonEmptyPart_CreatesAuditWithMovedCount()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateEntityAsync(s, "A-PART-AUD");

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 3));

        var auditCountBefore = await s.Db.AuditLogs.CountAsync(a => a.Action == "ProductComposition.PartRemoved");

        var ok = await s.Equipment.RemovePartAsync(partId);
        Assert.True(ok);

        var auditCountAfter = await s.Db.AuditLogs.CountAsync(a => a.Action == "ProductComposition.PartRemoved");
        Assert.Equal(auditCountBefore + 1, auditCountAfter);

        var audit = await s.Db.AuditLogs
            .Where(a => a.Action == "ProductComposition.PartRemoved")
            .OrderByDescending(a => a.CreatedAt)
            .FirstAsync();
        Assert.Contains("1", audit.Details);
    }

    [Fact]
    public async Task RemovePart_DuplicateConflict_LeavesDataAndNoSuccessAudit()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = await AddAggregateEntityAsync(s, "A-PART-CONF2");

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, null, aggregate.Id, 1));
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(compositionId, partId, aggregate.Id, 2));

        var auditCountBefore = await s.Db.AuditLogs.CountAsync(a => a.Action == "ProductComposition.PartRemoved");

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Equipment.RemovePartAsync(partId));

        var partStillExists = await s.Db.ProductCompositionParts.AnyAsync(p => p.Id == partId);
        Assert.True(partStillExists);

        var rowStillInPart = await s.Db.ProductCompositionAggregates
            .AnyAsync(a => a.ProductCompositionId == compositionId && a.PartId == partId && a.AggregateId == aggregate.Id);
        Assert.True(rowStillInPart);

        var auditCountAfter = await s.Db.AuditLogs.CountAsync(a => a.Action == "ProductComposition.PartRemoved");
        Assert.Equal(auditCountBefore, auditCountAfter);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task<(Guid CompositionId, Guid ModelId, Guid PartId)> CreateProductCompositionDraftAsync(TestScope s)
    {
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие тест" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var comp = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "Тест"));
        var part = await s.Equipment.AddPartAsync(new AddPartRequest(comp.Id, "Силовая установка", null, 1));
        return (comp.Id, model.Id, part.Id);
    }

    private async Task<(Guid SourceId, Guid ModelId, Guid PartId)> CreateApprovedProductCompositionAsync(TestScope s, Guid modelId)
    {
        var model = await s.Db.EquipmentModels.FindAsync(modelId);
        if (model == null)
        {
            model = new EquipmentModel { Id = modelId, Index = "T-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие тест" };
            s.Db.EquipmentModels.Add(model);
            await s.Db.SaveChangesAsync();
        }

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var comp = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(modelId, "Тест"));
        var part = await s.Equipment.AddPartAsync(new AddPartRequest(comp.Id, "Силовая установка", null, 1));
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(comp.Id, part.Id, aggregate.Id, 2));
        await s.Equipment.SubmitForReviewAsync(comp.Id);
        var approved = await s.Equipment.ApproveCompositionAsync(comp.Id, null);
        Assert.True(approved);
        return (comp.Id, modelId, part.Id);
    }

    private static async Task<Aggregate> AddAggregateEntityAsync(TestScope s, string codePrefix)
    {
        var aggregate = new Aggregate
        {
            Id = Guid.NewGuid(),
            Code = codePrefix + "-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Агрегат тест"
        };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();
        return aggregate;
    }
}
