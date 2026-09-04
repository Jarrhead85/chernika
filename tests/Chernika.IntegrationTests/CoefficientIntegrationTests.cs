using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class CoefficientIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public CoefficientIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private TestScope Scope() => _fixture.CreateScope();

    private void SetSystemAdmin(TestScope s) =>
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

    private async Task GrantPermissionAsync(TestScope s, string userId, string code)
    {
        var existing = await s.Db.UserPermissionOverrides
            .Where(o => o.UserId == userId && o.PermissionCode == code)
            .ToListAsync();
        s.Db.UserPermissionOverrides.RemoveRange(existing);
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(), UserId = userId, PermissionCode = code, IsGranted = true,
            Reason = "Test", GrantedByUserId = _fixture.SystemAdminUser.Id, CreatedAt = DateTime.UtcNow
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(userId);
    }

    private async Task DenyPermissionAsync(TestScope s, string userId, string code)
    {
        var existing = await s.Db.UserPermissionOverrides
            .Where(o => o.UserId == userId && o.PermissionCode == code)
            .ToListAsync();
        s.Db.UserPermissionOverrides.RemoveRange(existing);
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(), UserId = userId, PermissionCode = code, IsGranted = false,
            Reason = "Test deny", GrantedByUserId = _fixture.SystemAdminUser.Id, CreatedAt = DateTime.UtcNow
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(userId);
    }

    private async Task<Guid> CreateWorkingTypeAsync(TestScope s, string suffix)
    {
        var name = "Type " + suffix + " " + Guid.NewGuid().ToString("N")[..4];
        s.Db.CoefficientTypes.Add(new CoefficientType
        {
            Id = Guid.NewGuid(),
            Name = name,
            SortOrder = 100,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await s.Db.SaveChangesAsync();
        return s.Db.CoefficientTypes.Local.First(t => t.Name == name).Id;
    }

    private static CreateCoefficientRequest MakeCreateRequest(
        Guid typeId, string name, decimal value,
        string? conditionDescription = null, string? normativeBasis = null,
        int? sortOrder = null) => new()
    {
        CoefficientTypeId = typeId,
        Name = name,
        Value = value,
        ConditionDescription = conditionDescription,
        NormativeBasis = normativeBasis,
        SortOrder = sortOrder
    };

    private static UpdateCoefficientRequest MakeUpdateRequest(
        Guid id, Guid typeId, string name, decimal value, int sortOrder,
        string? conditionDescription = null, string? normativeBasis = null) => new()
    {
        Id = id,
        CoefficientTypeId = typeId,
        Name = name,
        Value = value,
        ConditionDescription = conditionDescription,
        NormativeBasis = normativeBasis,
        SortOrder = sortOrder
    };

    [Fact]
    public async Task Create_AssignsGuid_UtcDates_IsDeletedFalse()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "CreateAssigns");
        var result = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "Зимняя эксплуатация", 1.10m));

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.False(result.IsDeleted);
        Assert.True(result.CreatedAt > DateTime.MinValue);
        Assert.True(result.UpdatedAt > DateTime.MinValue);
    }

    [Fact]
    public async Task Create_TrimsName_BlankRejected()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "Trims");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var result = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "  Зимняя " + suffix + "  ", 1.10m));

        Assert.Equal("Зимняя " + suffix, result.Name);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.CreateCoefficientAsync(MakeCreateRequest(typeId, "   ", 1.0m)));
    }

    [Fact]
    public async Task Create_ValueValidation()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "ValueVal");
        var suffix = Guid.NewGuid().ToString("N")[..6];

        var accepted = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "Accept " + suffix, 0.9m));
        Assert.Equal(0.9m, accepted.Value);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.CreateCoefficientAsync(MakeCreateRequest(typeId, "Zero " + suffix, 0m)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.CreateCoefficientAsync(MakeCreateRequest(typeId, "Neg " + suffix, -0.5m)));
    }

    [Fact]
    public async Task Create_NullConditionAndBasisAccepted()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "NullFields");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var result = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "Name " + suffix, 1.0m, "", "   "));

        Assert.Null(result.ConditionDescription);
        Assert.Null(result.NormativeBasis);
    }

    [Fact]
    public async Task Create_ArchivedOrMissingTypeRejected()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var missingTypeId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.CreateCoefficientAsync(
                MakeCreateRequest(missingTypeId, "Missing", 1.0m)));

        var typeId = await CreateWorkingTypeAsync(s, "Archived");
        var type = s.Db.CoefficientTypes.First(t => t.Id == typeId);
        type.IsDeleted = true;
        type.DeletedAt = DateTime.UtcNow;
        await s.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.CreateCoefficientAsync(
                MakeCreateRequest(typeId, "Archived", 1.0m)));
    }

    [Fact]
    public async Task Create_TypeAndNameNormalizedDuplicateRejected()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "DupTypeName");
        var suffix = Guid.NewGuid().ToString("N")[..6];

        await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "Коэф " + suffix, 1.0m));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.CreateCoefficientAsync(
                MakeCreateRequest(typeId, "  коэф " + suffix + "  ", 1.1m)));
    }

    [Fact]
    public async Task Create_SameNameDifferentTypesAllowed()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var type1 = await CreateWorkingTypeAsync(s, "Multi1");
        var type2 = await CreateWorkingTypeAsync(s, "Multi2");
        var suffix = Guid.NewGuid().ToString("N")[..6];

        var c1 = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(type1, "Идент " + suffix, 1.0m));
        var c2 = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(type2, "Идент " + suffix, 1.1m));

        Assert.NotEqual(c1.Id, c2.Id);
        Assert.Equal(type1, c1.CoefficientTypeId);
        Assert.Equal(type2, c2.CoefficientTypeId);
    }

    [Fact]
    public async Task Create_DefaultSortOrder_IsMaxPlus10()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "SortOrder");
        var suffix = Guid.NewGuid().ToString("N")[..6];

        var first = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "First " + suffix, 1.0m));
        var second = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "Second " + suffix, 1.0m));

        Assert.Equal(first.SortOrder + 10, second.SortOrder);
    }

    [Fact]
    public async Task List_SearchAndFiltersServerSide()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var type1 = await CreateWorkingTypeAsync(s, "Search1");
        var type2 = await CreateWorkingTypeAsync(s, "Search2");
        var suffix = Guid.NewGuid().ToString("N")[..6];

        await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(type1, "Зимняя эксплуатация " + suffix, 1.1m, "Температура ниже -20", "НП-001"));
        await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(type2, "Летняя эксплуатация " + suffix, 1.05m));

        var searchResult = await s.CoeffService.GetCoefficientsAsync(new CoefficientListQuery
        {
            SearchText = "зимняя",
            PageSize = 200
        });
        Assert.Contains(searchResult.Items, c => c.Name.Contains("Зимняя"));

        var basisResult = await s.CoeffService.GetCoefficientsAsync(new CoefficientListQuery
        {
            HasNormativeBasis = true,
            PageSize = 200
        });
        Assert.Contains(basisResult.Items, c => c.Name.Contains("Зимняя"));
        Assert.DoesNotContain(basisResult.Items, c => c.Name.Contains("Летняя"));

        var typeResult = await s.CoeffService.GetCoefficientsAsync(new CoefficientListQuery
        {
            CoefficientTypeId = type1,
            PageSize = 200
        });
        Assert.Single(typeResult.Items);
        Assert.Equal(type1, typeResult.Items[0].CoefficientTypeId);
    }

    [Fact]
    public async Task List_SortingAndPaginationServerSide()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "SortPage");
        var suffix = Guid.NewGuid().ToString("N")[..6];

        for (int i = 0; i < 3; i++)
        {
            await s.CoeffService.CreateCoefficientAsync(
                MakeCreateRequest(typeId, $"Coef {i} {suffix}", 1.0m + i * 0.1m));
        }

        var page1 = await s.CoeffService.GetCoefficientsAsync(new CoefficientListQuery
        {
            CoefficientTypeId = typeId,
            Page = 1,
            PageSize = 2,
            SortBy = "value",
            SortDescending = true
        });
        Assert.Equal(2, page1.Items.Count);
        Assert.True(page1.Items[0].Value >= page1.Items[1].Value);

        var page2 = await s.CoeffService.GetCoefficientsAsync(new CoefficientListQuery
        {
            CoefficientTypeId = typeId,
            Page = 2,
            PageSize = 2,
            SortBy = "value",
            SortDescending = true
        });
        Assert.Single(page2.Items);
        Assert.Equal(3, page1.TotalCount);
    }

    [Fact]
    public async Task ReferenceView_CanRead_CannotMutate()
    {
        await using var s = Scope();
        var guestId = _fixture.GuestA.Id;
        await DenyPermissionAsync(s, guestId, PermissionCodes.ReferenceEdit);
        await GrantPermissionAsync(s, guestId, PermissionCodes.ReferenceView);
        s.User.CurrentUserId = Guid.Parse(guestId);

        var typeId = await CreateWorkingTypeAsync(s, "ViewOnly");
        var list = await s.CoeffService.GetCoefficientsAsync(new CoefficientListQuery { CoefficientTypeId = typeId });
        Assert.NotNull(list);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.CoeffService.CreateCoefficientAsync(MakeCreateRequest(typeId, "Forbidden", 1.0m)));
    }

    [Fact]
    public async Task ReferenceEdit_CanCreateUpdateArchiveRestore()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "FullCycle");
        var suffix = Guid.NewGuid().ToString("N")[..6];

        var created = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "Cycle " + suffix, 1.0m));

        var updated = await s.CoeffService.UpdateCoefficientAsync(
            MakeUpdateRequest(created.Id, typeId, created.Name, 1.5m, created.SortOrder, "Updated"));

        Assert.Equal(1.5m, updated.Value);

        await s.CoeffService.ArchiveCoefficientAsync(created.Id);
        var archived = await s.CoeffService.GetCoefficientByIdAsync(created.Id, includeArchived: true);
        Assert.NotNull(archived);
        Assert.True(archived.IsDeleted);

        await s.CoeffService.RestoreCoefficientAsync(created.Id);
        var restored = await s.CoeffService.GetCoefficientByIdAsync(created.Id);
        Assert.NotNull(restored);
        Assert.False(restored.IsDeleted);
    }

    [Fact]
    public async Task Archive_LeavesRowInDb()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "ArchiveDb");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var created = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "Arch " + suffix, 1.0m));

        await s.CoeffService.ArchiveCoefficientAsync(created.Id);

        var inDb = await s.Db.Coefficients.IgnoreQueryFilters().FirstAsync(c => c.Id == created.Id);
        Assert.True(inDb.IsDeleted);
        Assert.NotNull(inDb.DeletedAt);
    }

    [Fact]
    public async Task Archive_AllowedWithExistingIndividualCardLink()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "WithIC");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var coeff = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "IC " + suffix, 1.0m));

        // Create required dependent entities for IndividualCard.
        // We just need the FK relationships to exist — the coefficient will
        // be linked to the IC via the many-to-many table.
        var equipModelId = Guid.NewGuid();
        s.Db.EquipmentModels.Add(new EquipmentModel
        {
            Id = equipModelId, Index = "EM-" + suffix, Name = "TestModel",
            IsDeleted = false
        });

        var equipInstId = Guid.NewGuid();
        s.Db.EquipmentInstances.Add(new EquipmentInstance
        {
            Id = equipInstId, SerialNumber = "SN-" + suffix, Name = "TestInstance",
            EquipmentModelId = equipModelId, IsDeleted = false
        });

        var nodeId = Guid.NewGuid();
        s.Db.Nodes.Add(new Node
        {
            Id = nodeId, Code = "N-" + suffix, Name = "TestNode", IsDraft = false
        });

        var productCompId = Guid.NewGuid();
        s.Db.ProductCompositions.Add(new ProductComposition
        {
            Id = productCompId, EquipmentModelId = equipModelId,
            Version = "v1", Status = ProductCompositionStatus.Approved, IsActive = true
        });

        var hkId = Guid.NewGuid();
        s.Db.HKCards.Add(new HKCard
        {
            Id = hkId, Code = "HK-" + suffix, Version = "v1", Status = HKCardStatus.Approved,
            BranchId = _fixture.BranchA, EquipmentModelId = equipModelId
        });

        var icId = Guid.NewGuid();
        s.Db.IndividualCards.Add(new IndividualCard
        {
            Id = icId,
            ObjectLevel = IndividualCardObjectLevel.EquipmentInstance,
            Status = IndividualCardStatus.Draft,
            RevisionNumber = 1,
            Code = "ИК-ЭКЗ-SN-" + suffix + "-" + DateTime.UtcNow.Year,
            EquipmentInstanceId = equipInstId,
            HKCardId = hkId,
            ProductCompositionId = productCompId,
            BranchId = _fixture.BranchA,
            CreatedByUserId = _fixture.SystemAdminUser.Id,
            Version = "v1",
            TotalNorm = 1.0m
        });
        await s.Db.SaveChangesAsync();

        // Link coefficient to IndividualCard via raw SQL
        // (the relationship is a skip-navigation many-to-many without an explicit entity)
        await s.Db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"CoefficientIndividualCard\" (\"AppliedCoefficientsId\", \"IndividualCardsId\") VALUES ({0}, {1})",
            coeff.Id, icId);

        // Verify the link exists
        var linkedCount = await s.Db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS \"Value\" FROM \"CoefficientIndividualCard\" WHERE \"AppliedCoefficientsId\" = {0}",
            coeff.Id).FirstAsync();
        Assert.Equal(1, linkedCount);

        await s.CoeffService.ArchiveCoefficientAsync(coeff.Id);
        var inDb = await s.Db.Coefficients.IgnoreQueryFilters().FirstAsync(c => c.Id == coeff.Id);
        Assert.True(inDb.IsDeleted);

        // Verify the link still exists (archive preserves historical IC links)
        var linkedCountAfter = await s.Db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS \"Value\" FROM \"CoefficientIndividualCard\" WHERE \"AppliedCoefficientsId\" = {0}",
            coeff.Id).FirstAsync();
        Assert.Equal(1, linkedCountAfter);
    }

    [Fact]
    public async Task Restore_BlockedByWorkingTypeNameConflict()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "RestoreBlock");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var orig = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "Конфликт " + suffix, 1.0m));
        await s.CoeffService.ArchiveCoefficientAsync(orig.Id);
        await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "конфликт " + suffix, 1.5m));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.RestoreCoefficientAsync(orig.Id));
    }

    [Fact]
    public async Task Restore_BlockedIfParentTypeArchived()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "ParentArchived");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var coeff = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "Parent " + suffix, 1.0m));
        await s.CoeffService.ArchiveCoefficientAsync(coeff.Id);

        var type = s.Db.CoefficientTypes.First(t => t.Id == typeId);
        type.IsDeleted = true;
        type.DeletedAt = DateTime.UtcNow;
        await s.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.RestoreCoefficientAsync(coeff.Id));
    }

    [Fact]
    public async Task Audit_WritesEachActionOnce()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "Audit");
        var suffix = Guid.NewGuid().ToString("N")[..6];

        var created = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "Audit " + suffix, 1.0m));

        await s.CoeffService.UpdateCoefficientAsync(
            MakeUpdateRequest(created.Id, typeId, created.Name, 1.5m, created.SortOrder));

        await s.CoeffService.ArchiveCoefficientAsync(created.Id);
        await s.CoeffService.RestoreCoefficientAsync(created.Id);

        var audits = await s.Db.AuditLogs
            .Where(a => a.EntityType == "Coefficient" && a.EntityId == created.Id.ToString())
            .Select(a => a.Action)
            .ToListAsync();

        Assert.Equal(1, audits.Count(a => a == "Coefficient.Created"));
        Assert.Equal(1, audits.Count(a => a == "Coefficient.Updated"));
        Assert.Equal(1, audits.Count(a => a == "Coefficient.Archived"));
        Assert.Equal(1, audits.Count(a => a == "Coefficient.Restored"));
    }

    [Fact]
    public async Task FailedValidation_NoAuditWritten()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "FailAudit");
        var beforeCount = await s.Db.AuditLogs.CountAsync(a => a.EntityType == "Coefficient");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.CreateCoefficientAsync(MakeCreateRequest(typeId, "", 1.0m)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.CreateCoefficientAsync(MakeCreateRequest(typeId, "Zero", 0m)));

        var afterCount = await s.Db.AuditLogs.CountAsync(a => a.EntityType == "Coefficient");
        Assert.Equal(beforeCount, afterCount);
    }

    [Fact]
    public async Task TypeArchiveGuard_BlockedByWorkingCoefficients()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "GuardBlock");
        await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "Working", 1.0m));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.ArchiveCoefficientTypeAsync(typeId));
    }

    [Fact]
    public async Task TypeArchiveGuard_AllowsArchiveWhenCoefficientsArchived()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "GuardAllow");
        var c1 = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "AllArchived", 1.0m));
        await s.CoeffService.ArchiveCoefficientAsync(c1.Id);

        await s.CoeffService.ArchiveCoefficientTypeAsync(typeId);

        var type = await s.Db.CoefficientTypes.IgnoreQueryFilters().FirstAsync(t => t.Id == typeId);
        Assert.True(type.IsDeleted);
    }

    [Fact]
    public async Task Selector_ExcludesCoefficientOfArchivedType()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        // Normal flow: working type with a working coefficient cannot be archived until
        // the coefficient is archived first.
        var typeId = await CreateWorkingTypeAsync(s, "SelArchType");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var coeff = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "Sel " + suffix, 1.0m));

        Assert.Contains(
            (await s.CoeffService.GetWorkingCoefficientsForSelectAsync()).Select(c => c.Id),
            id => id == coeff.Id);

        await s.CoeffService.ArchiveCoefficientAsync(coeff.Id);
        await s.CoeffService.ArchiveCoefficientTypeAsync(typeId);

        // Defensive setup: create a legacy working (non-deleted) coefficient for the
        // now-archived type directly in the DB, bypassing the service create guard.
        var legacyId = Guid.NewGuid();
        s.Db.Coefficients.Add(new Coefficient
        {
            Id = legacyId,
            CoefficientTypeId = typeId,
            Name = "Legacy " + suffix,
            Value = 1.2m,
            SortOrder = 10,
            IsDeleted = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var working = await s.CoeffService.GetWorkingCoefficientsForSelectAsync();
        var workingIds = working.Select(c => c.Id).ToList();

        Assert.DoesNotContain(legacyId, workingIds);
        Assert.DoesNotContain(coeff.Id, workingIds);
    }

    [Fact]
    public async Task Selector_ExcludesCoefficientOfArchivedType_ScopedByTypeId()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "SelArchTypeId");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var coeff = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "Scoped " + suffix, 1.0m));
        await s.CoeffService.ArchiveCoefficientAsync(coeff.Id);
        await s.CoeffService.ArchiveCoefficientTypeAsync(typeId);

        var legacyId = Guid.NewGuid();
        s.Db.Coefficients.Add(new Coefficient
        {
            Id = legacyId,
            CoefficientTypeId = typeId,
            Name = "LegacyScoped " + suffix,
            Value = 1.3m,
            SortOrder = 20,
            IsDeleted = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var working = await s.CoeffService.GetWorkingCoefficientsForSelectAsync(typeId);
        Assert.Empty(working);
    }

    [Fact]
    public async Task Search_FindsCoefficientByTypeName()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeMarker = "Сезонный-" + Guid.NewGuid().ToString("N")[..6];
        var typeId = await CreateWorkingTypeAsync(s, "ByName");
        var type = s.Db.CoefficientTypes.First(t => t.Id == typeId);
        type.Name = typeMarker;
        await s.Db.SaveChangesAsync();

        var coeff = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "Обычное имя", 1.0m, "условие", "основание"));

        // The search term only appears in the type name, not in the coefficient
        // Name/ConditionDescription/NormativeBasis.
        var result = await s.CoeffService.GetCoefficientsAsync(new CoefficientListQuery
        {
            SearchText = typeMarker,
            StatusFilter = ReferenceStatusFilter.Active,
            PageSize = 50
        });

        Assert.Contains(result.Items, c => c.Id == coeff.Id);
        Assert.Equal(typeMarker, result.Items.First(c => c.Id == coeff.Id).CoefficientTypeName);
    }

    [Fact]
    public async Task Update_ArchivedCoefficientBlocked_NoStateChange_NoAudit()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var typeId = await CreateWorkingTypeAsync(s, "UpdArch");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var created = await s.CoeffService.CreateCoefficientAsync(
            MakeCreateRequest(typeId, "ArchUpd " + suffix, 1.0m, "cond", "basis", sortOrder: 5));

        await s.CoeffService.ArchiveCoefficientAsync(created.Id);

        // Capture original state after archive, reloading from the DB so the captured
        // UpdatedAt matches the PostgreSQL microsecond precision stored value.
        s.Db.ChangeTracker.Clear();
        var original = await s.Db.Coefficients.IgnoreQueryFilters().FirstAsync(c => c.Id == created.Id);
        var originalName = original.Name;
        var originalValue = original.Value;
        var originalTypeId = original.CoefficientTypeId;
        var originalCondition = original.ConditionDescription;
        var originalBasis = original.NormativeBasis;
        var originalSortOrder = original.SortOrder;
        var originalUpdatedAt = original.UpdatedAt;
        var auditCountBefore = await s.Db.AuditLogs.CountAsync(a =>
            a.EntityType == "Coefficient" && a.EntityId == created.Id.ToString() && a.Action == "Coefficient.Updated");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.UpdateCoefficientAsync(new UpdateCoefficientRequest
            {
                Id = created.Id,
                CoefficientTypeId = typeId,
                Name = "Changed " + suffix,
                Value = 9.9m,
                ConditionDescription = "newcond",
                NormativeBasis = "newbasis",
                SortOrder = 99
            }));

        Assert.Contains("архивирован", ex.Message);

        // Reload with IgnoreQueryFilters to bypass soft-delete filter.
        s.Db.ChangeTracker.Clear();
        var reloaded = await s.Db.Coefficients.IgnoreQueryFilters().FirstAsync(c => c.Id == created.Id);

        Assert.True(reloaded.IsDeleted);
        Assert.Equal(originalName, reloaded.Name);
        Assert.Equal(originalValue, reloaded.Value);
        Assert.Equal(originalTypeId, reloaded.CoefficientTypeId);
        Assert.Equal(originalCondition, reloaded.ConditionDescription);
        Assert.Equal(originalBasis, reloaded.NormativeBasis);
        Assert.Equal(originalSortOrder, reloaded.SortOrder);
        Assert.Equal(originalUpdatedAt, reloaded.UpdatedAt);

        var auditCountAfter = await s.Db.AuditLogs.CountAsync(a =>
            a.EntityType == "Coefficient" && a.EntityId == created.Id.ToString() && a.Action == "Coefficient.Updated");
        Assert.Equal(auditCountBefore, auditCountAfter);
    }
}
