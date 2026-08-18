using Chernika.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class PermissionMatrixTests
{
    private readonly TestDatabaseFixture _fixture;

    public PermissionMatrixTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SeededRoleTemplates_ContainTaskAndNotificationPermissions()
    {
        await using var s = _fixture.CreateScope();

        var templates = await s.Db.RolePermissionTemplates
            .AsNoTracking()
            .ToListAsync();

        var permsByRole = templates
            .GroupBy(t => t.RoleName)
            .ToDictionary(g => g.Key, g => g.Select(t => t.PermissionCode).ToHashSet(StringComparer.Ordinal));

        // NormAdmin: assign/cancel allowed
        Assert.Contains(PermissionCodes.TaskAssign, permsByRole["NormAdmin"]);
        Assert.Contains(PermissionCodes.TaskCancel, permsByRole["NormAdmin"]);
        Assert.Contains(PermissionCodes.NotificationMarkRead, permsByRole["NormAdmin"]);

        // Operator: only own-view + complete, no assign/cancel
        Assert.Contains(PermissionCodes.TaskViewOwn, permsByRole["Operator"]);
        Assert.Contains(PermissionCodes.TaskComplete, permsByRole["Operator"]);
        Assert.DoesNotContain(PermissionCodes.TaskAssign, permsByRole["Operator"]);
        Assert.DoesNotContain(PermissionCodes.TaskCancel, permsByRole["Operator"]);

        // HeadOfDepartment: assign allowed, cancel not
        Assert.Contains(PermissionCodes.TaskAssign, permsByRole["HeadOfDepartment"]);
        Assert.DoesNotContain(PermissionCodes.TaskCancel, permsByRole["HeadOfDepartment"]);

        // Guest: view-only for tasks
        Assert.Contains(PermissionCodes.TaskView, permsByRole["Guest"]);
        Assert.Contains(PermissionCodes.TaskViewOwn, permsByRole["Guest"]);
        Assert.DoesNotContain(PermissionCodes.TaskComplete, permsByRole["Guest"]);

        // SystemAdmin: all codes
        var allCodes = PermissionCodes.All;
        Assert.True(allCodes.IsSubsetOf(permsByRole["SystemAdmin"]));

        // HK delete permissions are granular; old HK.Delete is not seeded
        Assert.DoesNotContain("HK.Delete", permsByRole["NormAdmin"]);
        Assert.DoesNotContain("HK.Delete", permsByRole["Operator"]);
        Assert.Contains(PermissionCodes.HKDeleteDraft, permsByRole["NormAdmin"]);
        Assert.Contains(PermissionCodes.HKDeleteRevisionRequired, permsByRole["NormAdmin"]);
        Assert.DoesNotContain(PermissionCodes.HKDeleteOnReview, permsByRole["NormAdmin"]);
        Assert.Contains(PermissionCodes.HKDeleteDraft, permsByRole["Operator"]);
        Assert.Contains(PermissionCodes.HKDeleteRevisionRequired, permsByRole["Operator"]);
        Assert.DoesNotContain(PermissionCodes.HKDeleteOnReview, permsByRole["Operator"]);
        Assert.DoesNotContain(PermissionCodes.HKDeleteDraft, permsByRole["HeadOfDepartment"]);
        Assert.DoesNotContain(PermissionCodes.HKDeleteDraft, permsByRole["Guest"]);
    }

    [Fact]
    public async Task SystemAdmin_EffectivePermissions_IncludeAllCodes()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var perms = await s.Permissions.GetEffectivePermissionsAsync(_fixture.SystemAdminUser.Id);

        Assert.Equal(PermissionCodes.All, perms);
        Assert.True(await s.Permissions.HasPermissionAsync(_fixture.SystemAdminUser.Id, PermissionCodes.TaskCancel));
    }

    [Fact]
    public async Task Operator_EffectivePermissions_DoNotIncludeAssign()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);

        Assert.True(await s.Permissions.HasPermissionAsync(_fixture.OperatorA.Id, PermissionCodes.TaskView));
        Assert.True(await s.Permissions.HasPermissionAsync(_fixture.OperatorA.Id, PermissionCodes.TaskComplete));
        Assert.False(await s.Permissions.HasPermissionAsync(_fixture.OperatorA.Id, PermissionCodes.TaskAssign));
        Assert.False(await s.Permissions.HasPermissionAsync(_fixture.OperatorA.Id, PermissionCodes.TaskCancel));
    }
}
