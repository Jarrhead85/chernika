using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class SearchServiceExtendedIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public SearchServiceExtendedIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private TestScope Scope() => _fixture.CreateScope();

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    private static SearchService Service(TestScope s) =>
        new(s.Db, s.User, s.Users, TimeProvider.System, s.Permissions);

    // ── helpers ───────────────────────────────────────────────────────────

    private static HashSet<string> Types(SearchPageDto page) =>
        page.Items.Select(i => i.EntityType).ToHashSet(StringComparer.Ordinal);

    private async Task<Guid> CreateNodeAsync(TestScope s)
    {
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Code = "N-" + Suffix(),
            Name = "Узел " + Suffix(),
            IsDeleted = false,
            IsDraft = false,
        };
        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();
        return node.Id;
    }

    private async Task<Guid> CreateEquipmentAsync(TestScope s)
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

    private async Task<Guid> CreateMaterialAsync(TestScope s, string? name = null, string? gost = null)
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

    private async Task<Guid> CreateHKCardAsync(TestScope s, Guid nodeId, Guid? materialId = null, Guid? unitId = null)
    {
        var hk = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-" + Suffix(),
            Version = "v" + Suffix()[..4],
            Status = HKCardStatus.Approved,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = nodeId,
            BranchId = _fixture.BranchA,
            CreatedAt = DateTime.UtcNow,
            ApprovedDate = DateTime.UtcNow,
        };
        s.Db.HKCards.Add(hk);
        if (materialId is { } materialIdValue || unitId is { } unitIdValue)
        {
            Guid actualUnitId = unitId ?? Guid.NewGuid();
            if (unitId is null)
            {
                var unit = new AssemblyUnit
                {
                    Id = actualUnitId,
                    Code = "AU-" + Suffix(),
                    Name = "СЕ " + Suffix(),
                    IsDeleted = false,
                    IsDraft = false,
                };
                s.Db.AssemblyUnits.Add(unit);
            }
            var item = new HKCardItem
            {
                Id = Guid.NewGuid(),
                HKCardId = hk.Id,
                AssemblyUnitId = actualUnitId,
                Quantity = 1,
                Volume = 100m,
                UnitOfMeasure = "г",
                SortOrder = 1,
            };
            if (materialId is { } mid)
            {
                item.Materials.Add(new HKCardItemMaterial
                {
                    Id = Guid.NewGuid(),
                    HKCardItemId = item.Id,
                    GsmMaterialId = mid,
                    Category = GsmCategory.Primary,
                });
            }
            s.Db.HKCardItems.Add(item);
        }
        await s.Db.SaveChangesAsync();
        return hk.Id;
    }

    private async Task<ApplicationUser> CreateUserAsync(TestScope s, string role, Guid branchId, bool grantHK = false)
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
        if (grantHK)
        {
            s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                PermissionCode = Chernika.Domain.PermissionCodes.HKView,
                IsGranted = true,
                Reason = "Test",
                GrantedByUserId = _fixture.SystemAdminUser.Id,
                CreatedAt = DateTime.UtcNow,
            });
            await s.Db.SaveChangesAsync();
            s.Permissions.InvalidateCache(user.Id);
        }
        return user;
    }

    // ── tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_EmptyQueryNoFilters_ReturnsEmptyWithoutScan()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var service = Service(s);

        var page = await service.SearchAsync(new SearchQuery());

        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
        Assert.Equal(1, page.Page);
    }

    [Fact]
    public async Task SearchAsync_HKCardByCode_FoundWithDirectMatch()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var hkId = await CreateHKCardAsync(s, nodeId);
        var hk = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == hkId);

        var page = await Service(s).SearchAsync(new SearchQuery { Text = hk.Code, PageSize = 25 });

        var card = page.Items.FirstOrDefault(i => i.EntityType == "HKCard" && i.EntityId == hk.Id);
        Assert.NotNull(card);
        Assert.Equal("Химмотологическая карта", card!.EntityTypeDisplay);
        Assert.Contains(hk.Code, card.Title);
        Assert.Contains("реквизиты", card.MatchContext ?? "");
        Assert.Equal("/хк/" + hk.Id, card.NavigationUrl);
        Assert.True(card.CanOpen);
    }

    [Fact]
    public async Task SearchAsync_GsmMaterialName_FindsMaterialAndDependentHKCard()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var materialName = "М-10Г2к " + Suffix();
        var materialId = await CreateMaterialAsync(s, name: materialName, gost: "ГОСТ-" + Suffix());
        var nodeId = await CreateNodeAsync(s);
        var hkId = await CreateHKCardAsync(s, nodeId, materialId: materialId);
        var hk = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == hkId);

        var page = await Service(s).SearchAsync(new SearchQuery { Text = materialName, PageSize = 50 });

        var material = page.Items.FirstOrDefault(
            i => i.EntityType == "GsmMaterial" && i.EntityId == materialId);
        var hkResult = page.Items.FirstOrDefault(
            i => i.EntityType == "HKCard" && i.EntityId == hk.Id);
        Assert.NotNull(material);
        Assert.Equal(materialName, material!.Title);
        Assert.NotNull(hkResult);
        Assert.Contains("марка ГСМ", hkResult!.MatchContext ?? "");
        Assert.All(page.Items, i => Assert.NotEqual("AuditLog", i.EntityType));
    }

    [Fact]
    public async Task SearchAsync_ByGost_FindsMaterialWithGostContext()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var gost = "8581-" + Suffix();
        var materialName = "ГСМ ГОСТ " + Suffix();
        var materialId = await CreateMaterialAsync(s, name: materialName, gost: gost);

        var page = await Service(s).SearchAsync(new SearchQuery { Text = gost, PageSize = 25 });

        var material = page.Items.FirstOrDefault(i => i.EntityType == "GsmMaterial" && i.EntityId == materialId);
        Assert.NotNull(material);
        Assert.Contains("ГОСТ/ТУ", material!.MatchContext ?? "");
    }

    [Fact]
    public async Task SearchAsync_AssemblyUnitCode_FindsUnitWithHKReference()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var unit = new AssemblyUnit
        {
            Id = Guid.NewGuid(),
            Code = "AU-" + Suffix(),
            Name = "СЕ " + Suffix(),
            IsDeleted = false,
            IsDraft = false,
        };
        s.Db.AssemblyUnits.Add(unit);
        await s.Db.SaveChangesAsync();
        var nodeId = await CreateNodeAsync(s);
        var hkId = await CreateHKCardAsync(s, nodeId, unitId: unit.Id);

        var page = await Service(s).SearchAsync(new SearchQuery { Text = unit.Code, PageSize = 25 });

        Assert.Contains(page.Items, i => i.EntityType == "AssemblyUnit" && i.EntityId == unit.Id);
        Assert.Contains(page.Items, i => i.EntityType == "HKCard" && i.EntityId == hkId);
    }

    [Fact]
    public async Task SearchAsync_ResultsWithoutDuplicates()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var materialName = "М-10Г2 " + Suffix();
        var materialId = await CreateMaterialAsync(s, name: materialName);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKCardAsync(s, nodeId, materialId: materialId);

        var page = await Service(s).SearchAsync(new SearchQuery { Text = materialName, PageSize = 25 });

        var keys = page.Items
            .Select(i => (i.EntityType, i.EntityId))
            .ToHashSet();
        Assert.Equal(page.Items.Count, keys.Count);
    }

    [Fact]
    public async Task SearchAsync_EntityTypeFilter_NarrowsResultsToRequestedType()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var materialName = "ГСМ фильтр " + Suffix();
        var materialId = await CreateMaterialAsync(s, name: materialName);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKCardAsync(s, nodeId, materialId: materialId);

        var page = await Service(s).SearchAsync(
            new SearchQuery { Text = materialName, EntityType = "GsmMaterial" });

        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, i => Assert.Equal("GsmMaterial", i.EntityType));
    }

    [Fact]
    public async Task SearchAsync_InvertedDateRange_ThrowsControlledError()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var service = Service(s);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new SearchQuery
        {
            Text = "тест",
            CreatedFrom = new DateTime(2026, 6, 20),
            CreatedTo = new DateTime(2026, 1, 10),
        }));
    }

    [Fact]
    public async Task SearchAsync_PageSizeIsFixed()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var service = Service(s);

        var skipped = await service.SearchAsync(new SearchQuery { Text = "тест", PageSize = 5 });
        Assert.Equal(50, skipped.PageSize);

        var bigger = await service.SearchAsync(new SearchQuery { Text = "тест", PageSize = 1000 });
        Assert.Equal(50, bigger.PageSize);

        var defaulted = await service.SearchAsync(new SearchQuery { Text = "тест" });
        Assert.Equal(50, defaulted.PageSize);
    }

    [Fact]
    public async Task SearchAsync_NonSystemAdmin_CannotEscapeOwnBranch()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKCardAsync(s, nodeId);
        var foreign = await CreateUserAsync(s, nameof(UserRole.Operator), _fixture.BranchB, grantHK: true);

        SetUser(s, foreign);
        var page = await Service(s).SearchAsync(new SearchQuery
        {
            Text = "HK-",
            BranchId = _fixture.BranchA, // подделка не расширяет область
        });

        Assert.All(page.Items, i =>
        {
            if (i.BranchId is { } branchId)
                Assert.NotEqual(_fixture.BranchA, branchId);
        });
    }

    [Fact]
    public async Task SearchAsync_WorkTaskSearch_FindsBySnapshotFields()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var login = "op_" + Suffix();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = login,
            FullName = "Тест " + login,
            BranchId = _fixture.BranchA,
            IsActive = true,
        };
        await s.Users.CreateAsync(user);
        await s.Users.AddToRoleAsync(user, nameof(UserRole.Operator));
        s.Permissions.InvalidateCache(user.Id);

        var taskCode = "TASK-" + Suffix();
        var task = new WorkTask
        {
            Id = Guid.NewGuid(),
            Title = "Рассмотреть ХК",
            Type = WorkTaskType.HKReview,
            Status = WorkTaskStatus.Open,
            Priority = WorkTaskPriority.Normal,
            AssignedToUserId = user.Id,
            CreatedByUserId = _fixture.SystemAdminUser.Id,
            BranchId = _fixture.BranchA,
            EntityType = "HKCard",
            EntityId = Guid.NewGuid(),
            EntityCodeSnapshot = "HK-" + Suffix(),
            EntityTitleSnapshot = taskCode,
            CreatedAtUtc = DateTime.UtcNow,
        };
        s.Db.WorkTasks.Add(task);
        await s.Db.SaveChangesAsync();

        SetUser(s, _fixture.SystemAdminUser);
        var page = await Service(s).SearchAsync(new SearchQuery { Text = taskCode, PageSize = 25 });

        var match = page.Items.FirstOrDefault(i => i.EntityType == "WorkTask" && i.EntityId == task.Id);
        Assert.NotNull(match);
        Assert.Equal("/задачи/" + task.Id, match!.NavigationUrl);
    }

    [Fact]
    public async Task SearchAsync_SystemAdminBranchFilter_NarrowsResults()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeA = await CreateNodeAsync(s);
        var hkA = await CreateHKCardAsync(s, nodeA);
        var allPage = await Service(s).SearchAsync(
            new SearchQuery { Text = "HK-", BranchId = _fixture.BranchA });

        Assert.All(allPage.Items, i =>
        {
            if (i.EntityType == "HKCard")
                Assert.Equal(_fixture.BranchA, i.BranchId);
        });
    }

    private void SetUser(TestScope s, ApplicationUser user) =>
        s.User.CurrentUserId = Guid.Parse(user.Id);

    // ── UX corrective tests ───────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_RelatedScopeHKOnly_RestrictsToHKCards()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var materialName = "М-10В " + Suffix();
        var materialId = await CreateMaterialAsync(s, name: materialName);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKCardAsync(s, nodeId, materialId: materialId);

        var page = await Service(s).SearchAsync(new SearchQuery
        {
            Text = materialName,
            RelatedScope = RelatedResultsScope.HKOnly,
        });

        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, i => Assert.Equal("HKCard", i.EntityType));
    }

    [Fact]
    public async Task SearchAsync_RelatedScopeReferenceOnly_ShowsMaterialOnly()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var materialName = "М-10С " + Suffix();
        var materialId = await CreateMaterialAsync(s, name: materialName);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKCardAsync(s, nodeId, materialId: materialId);

        var page = await Service(s).SearchAsync(new SearchQuery
        {
            Text = materialName,
            RelatedScope = RelatedResultsScope.ReferenceOnly,
        });

        Assert.All(page.Items, i =>
            Assert.DoesNotContain(i.EntityType, new[] { "HKCard", "IndividualCard" }));
        Assert.Contains(page.Items, i => i.EntityType == "GsmMaterial" && i.EntityId == materialId);
    }

    [Fact]
    public async Task SearchAsync_RelatedScopeICOnly_ShowsOnlyIndividualCards()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var materialName = "М-10Э " + Suffix();
        var materialId = await CreateMaterialAsync(s, name: materialName);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKCardAsync(s, nodeId, materialId: materialId);

        var page = await Service(s).SearchAsync(new SearchQuery
        {
            Text = materialName,
            RelatedScope = RelatedResultsScope.ICOnly,
        });

        Assert.All(page.Items, i => Assert.Equal("IndividualCard", i.EntityType));
    }

    [Fact]
    public async Task SearchAsync_IndividualCardFormStateFilter_Works()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var hkId = await CreateHKCardAsync(s, nodeId);
        var hk = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == hkId);
        var modelId = await CreateEquipmentAsync(s);
        var draft = await s.IndividualCards.CreateDraftAsync(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId));
        var draftCode = "ИК-" + Suffix();
        var card = await s.Db.IndividualCards.FirstAsync(c => c.Id == draft.Id);
        card.Code = draftCode;
        card.Version = "v" + Suffix()[..4];
        await s.Db.SaveChangesAsync();

        var notFormed = await Service(s).SearchAsync(new SearchQuery
        {
            Text = draftCode,
            EntityType = "IndividualCard",
            IsFormed = false,
        });
        Assert.Contains(notFormed.Items, i => i.EntityType == "IndividualCard" && i.EntityId == draft.Id);

        var formed = await Service(s).SearchAsync(new SearchQuery
        {
            Text = draftCode,
            EntityType = "IndividualCard",
            IsFormed = true,
        });
        Assert.DoesNotContain(formed.Items, i => i.EntityId == draft.Id);
    }

    [Fact]
    public async Task SearchAsync_RussianDisplay_MapsStatusAndTags()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKCardAsync(s, nodeId);

        var page = await Service(s).SearchAsync(new SearchQuery { Text = "HK-", PageSize = 50 });

        var hkResults = page.Items.Where(i => i.EntityType == "HKCard").ToList();
        Assert.NotEmpty(hkResults);
        Assert.Contains(hkResults, i => i.StatusDisplay == "Утверждена");
        Assert.All(page.Items, i =>
        {
            Assert.DoesNotContain("EquipmentModel", i.EntityTypeDisplay);
            Assert.DoesNotContain("HKCard", i.EntityTypeDisplay);
        });
    }

    [Fact]
    public async Task SearchAsync_RelevanceRank_ExactCodeFirst()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        var hkId = await CreateHKCardAsync(s, nodeId);
        var hk = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == hkId);
        var nodeId2 = await CreateNodeAsync(s);
        await CreateHKCardAsync(s, nodeId2);

        var page = await Service(s).SearchAsync(new SearchQuery { Text = hk.Code, SortBy = "Relevance" });

        Assert.Equal(hk.Id, page.Items.First().EntityId);
    }

    [Fact]
    public async Task SearchAsync_CancelledToken_DoesNotOverwriteResults()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var nodeId = await CreateNodeAsync(s);
        await CreateHKCardAsync(s, nodeId);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(s).SearchAsync(new SearchQuery { Text = "HK-", PageSize = 25 }, cts.Token));
    }

    [Theory]
    [InlineData("HKCard", "ХИММОТОЛОГИЧЕСКАЯ КАРТА")]
    [InlineData("IndividualCard", "ИНДИВИДУАЛЬНАЯ КАРТА")]
    [InlineData("Complex", "КОМПЛЕКС")]
    [InlineData("EquipmentModel", "ИЗДЕЛИЕ")]
    [InlineData("Aggregate", "АГРЕГАТ")]
    [InlineData("Node", "УЗЕЛ")]
    [InlineData("AssemblyUnit", "СБОРОЧНАЯ ЕДИНИЦА")]
    [InlineData("EquipmentInstance", "ЭКЗЕМПЛЯР ИЗДЕЛИЯ")]
    [InlineData("GsmMaterial", "МАРКА ГСМ")]
    [InlineData("Coefficient", "КОЭФФИЦИЕНТ")]
    [InlineData("WorkTask", "ЗАДАЧА")]
    public void SearchDisplayCatalog_RussianTagsOnly(string entityType, string expected)
    {
        var tag = Chernika.Domain.SearchDisplayCatalog.EntityTypeTag(entityType);
        Assert.Equal(expected, tag);
        foreach (var forbidden in new[]
                 {
                     "EquipmentModel", "HKCard", "IndividualCard", "WorkTask", "CreatedAt",
                     "Snapshot", "Preflight", "Occurrence", "GUID",
                     "Complex", "Aggregate", "Node", "AssemblyUnit", "GsmMaterial", "Coefficient",
                 })
        {
            Assert.DoesNotContain(tag, forbidden, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SearchAsync_ResultCards_NeverExposeEnglishTechnicalNames()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var materialName = "М-10Х " + Suffix();
        var materialId = await CreateMaterialAsync(s, name: materialName, gost: "ГОСТ-" + Suffix());
        var nodeId = await CreateNodeAsync(s);
        await CreateHKCardAsync(s, nodeId, materialId: materialId);

        var page = await Service(s).SearchAsync(new SearchQuery { Text = materialName, PageSize = 50 });

        var forbidden = new[]
        {
            "EquipmentModel", "HKCard", "IndividualCard", "WorkTask", "CreatedAt",
            "Snapshot", "Preflight", "Occurrence", "GUID",
        };
        foreach (var result in page.Items)
        {
            // ровно один русский тип-тег
            Assert.DoesNotContain(result.EntityTypeDisplay, forbidden, StringComparison.OrdinalIgnoreCase);
            if (result.StatusDisplay is { } status)
                Assert.DoesNotContain(result.StatusDisplay, forbidden, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(result.Title, forbidden, StringComparison.OrdinalIgnoreCase);
            if (result.Subtitle is { } subtitle)
                Assert.DoesNotContain(subtitle, forbidden, StringComparison.OrdinalIgnoreCase);
            if (result.MatchContext is { } context)
                Assert.DoesNotContain(context, forbidden, StringComparison.OrdinalIgnoreCase);
        }

        var nodeResult = page.Items.FirstOrDefault(i => i.EntityType == "Node");
        Assert.NotNull(nodeResult);
        Assert.Equal("Узел", nodeResult!.EntityTypeDisplay);
        Assert.Null(nodeResult.StatusDisplay);
    }
}
