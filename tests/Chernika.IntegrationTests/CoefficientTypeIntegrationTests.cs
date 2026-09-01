using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class CoefficientTypeIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public CoefficientTypeIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private TestScope Scope() => _fixture.CreateScope();

    private void SetNormAdminA(TestScope s) => s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
    private void SetSystemAdmin(TestScope s) => s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

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

    [Fact]
    public async Task Create_TypeAssignsGuid_UtcDates_IsDeletedFalse()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit).Wait();

        var result = await s.CoeffService.CreateCoefficientTypeAsync(
            new CreateCoefficientTypeRequest("Тестовый тип"));

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.False(result.IsDeleted);
        Assert.True(result.CreatedAt > DateTime.MinValue);
        Assert.True(result.UpdatedAt > DateTime.MinValue);
    }

    [Fact]
    public async Task Create_TrimsName()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var uniqueName = "Trim Test " + Guid.NewGuid().ToString("N")[..8];
        var result = await s.CoeffService.CreateCoefficientTypeAsync(
            new CreateCoefficientTypeRequest("  " + uniqueName + "  "));

        Assert.Equal(uniqueName, result.Name);
    }

    [Fact]
    public async Task Create_RejectsBlankName()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("   ")));
    }

    [Fact]
    public async Task Create_RejectsCaseInsensitiveDuplicate()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var uniqueName = "DupTest " + Guid.NewGuid().ToString("N")[..8];
        await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest(uniqueName));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("  " + uniqueName.ToUpper() + "  ")));
    }

    [Fact]
    public async Task Create_DefaultSortOrder_IsMaxPlus10()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var first = await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("SortA " + Guid.NewGuid().ToString("N")[..4]));
        var second = await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("SortB " + Guid.NewGuid().ToString("N")[..4]));

        Assert.Equal(first.SortOrder + 10, second.SortOrder);
    }

    [Fact]
    public async Task Update_ExcludesCurrentId_ButRejectsConflict()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit).Wait();

        var a = await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("Тип А"));
        var b = await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("Тип Б"));

        var updated = await s.CoeffService.UpdateCoefficientTypeAsync(
            new UpdateCoefficientTypeRequest(a.Id, "Тип А обновлённый", 99));
        Assert.Equal("Тип А обновлённый", updated.Name);
        Assert.Equal(99, updated.SortOrder);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.UpdateCoefficientTypeAsync(new UpdateCoefficientTypeRequest(b.Id, "Тип А обновлённый", 100)));
    }

    [Fact]
    public async Task List_OrderIs_SortOrderThenName()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit).Wait();

        await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("Lzz " + Guid.NewGuid().ToString("N")[..4], 1));
        await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("Laa " + Guid.NewGuid().ToString("N")[..4], 1));
        await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("Lbb " + Guid.NewGuid().ToString("N")[..4], 2));

        var list = await s.CoeffService.GetActiveCoefficientTypesForSelectAsync();
        var items = list.Where(t => t.Name.StartsWith("L")).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToList();
        Assert.Equal(3, items.Count);
        Assert.True(items[0].SortOrder <= items[1].SortOrder);
        Assert.True(items[1].SortOrder <= items[2].SortOrder);
    }

    [Fact]
    public async Task List_FiltersArchived_AndIncludesArchivedWhenRequested()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var marker = Guid.NewGuid().ToString("N")[..6];
        var a = await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("Active " + marker));
        var b = await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("Archive " + marker));
        await s.CoeffService.ArchiveCoefficientTypeAsync(b.Id);

        var active = await s.CoeffService.GetCoefficientTypesAsync(new CoefficientTypeListQuery(SearchText: marker));
        Assert.Single(active.Items);

        var all = await s.CoeffService.GetCoefficientTypesAsync(new CoefficientTypeListQuery(SearchText: marker, StatusFilter: ReferenceStatusFilter.All));
        Assert.Equal(2, all.Items.Count);
    }

    [Fact]
    public async Task List_ServerSideSearch()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var uniqueMarker = Guid.NewGuid().ToString("N")[..6];
        await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("SearchMatch " + uniqueMarker));
        await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("Other " + uniqueMarker));

        var result = await s.CoeffService.GetCoefficientTypesAsync(
            new CoefficientTypeListQuery(SearchText: "searchmatch"));
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task ReferenceView_CanRead_CannotCreate()
    {
        await using var s = Scope();
        s.User.CurrentUserId = Guid.Parse(_fixture.GuestA.Id);

        await DenyPermissionAsync(s, _fixture.GuestA.Id, PermissionCodes.ReferenceEdit);
        await GrantPermissionAsync(s, _fixture.GuestA.Id, PermissionCodes.ReferenceView);

        var list = await s.CoeffService.GetActiveCoefficientTypesForSelectAsync();
        Assert.NotNull(list);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("Test " + Guid.NewGuid().ToString("N")[..4])));

        await GrantPermissionAsync(s, _fixture.GuestA.Id, PermissionCodes.ReferenceEdit);
        var created = await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("Test " + Guid.NewGuid().ToString("N")[..4]));
        Assert.NotNull(created);
    }

    [Fact]
    public async Task Archive_WithoutWorkingCoefficients_Succeeds()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit).Wait();

        var type = await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("Архивный"));
        await s.CoeffService.ArchiveCoefficientTypeAsync(type.Id);

        var list = await s.CoeffService.GetActiveCoefficientTypesForSelectAsync();
        Assert.DoesNotContain(list, t => t.Id == type.Id);
    }

    [Fact]
    public async Task Archive_WithWorkingCoefficients_Fails()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var type = await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("WithCoeff " + Guid.NewGuid().ToString("N")[..4]));
        s.Db.Coefficients.Add(new Coefficient
        {
            Id = Guid.NewGuid(), CoefficientTypeId = type.Id, Name = "К1", Value = 1.0m,
            IsActive = true, IsDeleted = false
        });
        await s.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.ArchiveCoefficientTypeAsync(type.Id));
    }

    [Fact]
    public async Task Restore_Succeeds_WhenNoConflict()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var type = await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("Restore " + Guid.NewGuid().ToString("N")[..4]));
        await s.CoeffService.ArchiveCoefficientTypeAsync(type.Id);
        await s.CoeffService.RestoreCoefficientTypeAsync(type.Id);

        var all = await s.CoeffService.GetCoefficientTypesAsync(new CoefficientTypeListQuery(StatusFilter: ReferenceStatusFilter.All));
        var restored = all.Items.FirstOrDefault(t => t.Id == type.Id);
        Assert.NotNull(restored);
        Assert.False(restored.IsDeleted);
    }

    [Fact]
    public async Task Restore_Fails_OnNameConflict()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var uniqueName = "Conflict " + Guid.NewGuid().ToString("N")[..8];
        var original = await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest(uniqueName));
        await s.CoeffService.ArchiveCoefficientTypeAsync(original.Id);
        await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest(uniqueName));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.RestoreCoefficientTypeAsync(original.Id));
    }

    [Fact]
    public async Task Archive_Restore_DoNot_PhysicallyDelete()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var type = await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("SoftDel " + Guid.NewGuid().ToString("N")[..4]));
        await s.CoeffService.ArchiveCoefficientTypeAsync(type.Id);

        var inDb = await s.Db.CoefficientTypes.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == type.Id);
        Assert.NotNull(inDb);
        Assert.True(inDb.IsDeleted);
    }

    [Fact]
    public async Task Audit_WritesCreated_Archived_Restored()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var type = await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("Audit " + Guid.NewGuid().ToString("N")[..4]));
        await s.CoeffService.ArchiveCoefficientTypeAsync(type.Id);
        await s.CoeffService.RestoreCoefficientTypeAsync(type.Id);

        var audits = await s.Db.AuditLogs
            .Where(a => a.EntityType == "CoefficientType" && a.EntityId == type.Id.ToString())
            .Select(a => a.Action)
            .ToListAsync();

        Assert.Contains("CoefficientType.Created", audits);
        Assert.Contains("CoefficientType.Archived", audits);
        Assert.Contains("CoefficientType.Restored", audits);
    }

    [Fact]
    public async Task Seed_IsIdempotent()
    {
        await using var s = Scope();
        SetSystemAdmin(s);

        DemoDataSeeder.SeedCoefficientTypes(s.Db);
        await s.Db.SaveChangesAsync();

        var first = await s.CoeffService.GetActiveCoefficientTypesForSelectAsync();
        var firstNormalized = string.Join(",", first.Select(t => t.Name.Trim().ToUpperInvariant()).OrderBy(n => n));
        var firstCount = first.Count;

        DemoDataSeeder.SeedCoefficientTypes(s.Db);
        await s.Db.SaveChangesAsync();

        var second = await s.CoeffService.GetActiveCoefficientTypesForSelectAsync();
        var secondNormalized = string.Join(",", second.Select(t => t.Name.Trim().ToUpperInvariant()).OrderBy(n => n));

        Assert.Equal(firstCount, second.Count);
        Assert.Equal(firstNormalized, secondNormalized);
    }

    [Fact]
    public async Task ArchiveConflict_ProducesNoAudit()
    {
        await using var s = Scope();
        SetSystemAdmin(s);
        await GrantPermissionAsync(s, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceEdit);

        var type = await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest("ConflictAudit " + Guid.NewGuid().ToString("N")[..4]));
        s.Db.Coefficients.Add(new Coefficient
        {
            Id = Guid.NewGuid(), CoefficientTypeId = type.Id, Name = "К1", Value = 1.0m,
            IsActive = true, IsDeleted = false
        });
        await s.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CoeffService.ArchiveCoefficientTypeAsync(type.Id));

        var archiveAudits = await s.Db.AuditLogs
            .CountAsync(a => a.EntityType == "CoefficientType" && a.EntityId == type.Id.ToString() && a.Action == "CoefficientType.Archived");
        Assert.Equal(0, archiveAudits);
    }

    [Fact]
    public async Task Branch_DoesNotRestrict_GlobalRegistry()
    {
        await using var s = Scope();
        SetNormAdminA(s);
        await GrantPermissionAsync(s, _fixture.NormAdminA.Id, PermissionCodes.ReferenceEdit);

        var uniqueName = "Global " + Guid.NewGuid().ToString("N")[..8];
        await s.CoeffService.CreateCoefficientTypeAsync(new CreateCoefficientTypeRequest(uniqueName));

        await using var s2 = Scope();
        SetSystemAdmin(s2);
        await GrantPermissionAsync(s2, _fixture.SystemAdminUser.Id, PermissionCodes.ReferenceView);

        var list = await s2.CoeffService.GetActiveCoefficientTypesForSelectAsync();
        Assert.Contains(list, t => t.Name == uniqueName);
    }
}
