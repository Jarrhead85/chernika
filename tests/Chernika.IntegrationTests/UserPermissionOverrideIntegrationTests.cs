using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Индивидуальные решения по полномочиям: сочетания «роль разрешает / роль
/// запрещает» × «решения нет / разрешающее / запрещающее».
/// <para>
/// Проверяется не отображение таблицы, а результат: <c>IsEffective</c> до и
/// после команды, реальная строка в БД, состояние кэша и то, что прямой вызов
/// защищённой операции сервер пропускает или отклоняет.
/// </para>
/// </summary>
[Collection("Database")]
public class UserPermissionOverrideIntegrationTests : IAsyncLifetime
{
    private readonly TestDatabaseFixture _fixture;
    /// <summary>
    /// Снимок кодов шаблона роли ДО прогона. Шаблон роли общий для всей фикстуры,
    /// поэтому после теста он возвращается точно в это состояние: простое
    /// удаление оставило бы общую фикстуру без прав и ломало бы чужие тесты.
    /// </summary>
    private readonly HashSet<string> _roleTemplateSnapshot = new(StringComparer.Ordinal);
    private string _subjectUserId = null!;
    private string _subjectRole = null!;

    public UserPermissionOverrideIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        // Отдельный пользователь на каждый прогон: роли и права назначаются ему,
        // поэтому чужие назначения общей фикстуры не влияют на результат.
        await using var s = _fixture.CreateScope();

        var user = new ApplicationUser
        {
            UserName = "perm_subject_" + Guid.NewGuid().ToString("N")[..6],
            Email = Guid.NewGuid().ToString("N") + "@perm.test",
            EmailConfirmed = true,
            BranchId = _fixture.BranchA,
            IsActive = true,
        };
        var created = await s.Users.CreateAsync(user, "Perm-Subject-Pass-1");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        _subjectUserId = user.Id;

        // Роль по умолчанию — Operator: её шаблон и меняется при проверке
        // сочетаний «роль разрешает / роль запрещает».
        await s.Users.AddToRoleAsync(user, nameof(UserRole.Operator));
        _subjectRole = nameof(UserRole.Operator);

        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        _roleTemplateSnapshot.Clear();
        var seeded = await s.Db.RolePermissionTemplates
            .Where(t => t.RoleName == _subjectRole)
            .Select(t => t.PermissionCode)
            .ToListAsync();

        foreach (var code in seeded)
            _roleTemplateSnapshot.Add(code);
    }

    public async Task DisposeAsync()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        await s.Db.UserPermissionOverrides
            .Where(o => o.UserId == _subjectUserId)
            .ExecuteDeleteAsync();

        // Шаблон роли приводится к снимку: лишнее удаляется, недостающее
        // возвращается. После прогона общая фикстура не отличается от того, что
        // было до него.
        var current = await s.Db.RolePermissionTemplates
            .Where(t => t.RoleName == _subjectRole)
            .Select(t => t.PermissionCode)
            .ToListAsync();

        var extra = current.Where(c => !_roleTemplateSnapshot.Contains(c)).ToList();
        if (extra.Count > 0)
        {
            await s.Db.RolePermissionTemplates
                .Where(t => t.RoleName == _subjectRole && extra.Contains(t.PermissionCode))
                .ExecuteDeleteAsync();
        }

        foreach (var code in _roleTemplateSnapshot.Where(c => !current.Contains(c)))
        {
            s.Db.RolePermissionTemplates.Add(new RolePermissionTemplate
            {
                Id = Guid.NewGuid(),
                RoleName = _subjectRole,
                PermissionCode = code,
            });
        }

        await s.Db.SaveChangesAsync();

        var user = await s.Users.FindByIdAsync(_subjectUserId);
        if (user != null)
            await s.Users.DeleteAsync(user);
    }

    /// <summary>
    /// Ставит роль в нужное положение по проверяемому коду: при <c>false</c>
    /// строка шаблона удаляется, при <c>true</c> — добавляется.
    /// </summary>
    /// <remarks>
    /// Две ошибки были здесь и обе делали тест бессмысленным. Первая: строка
    /// добавлялась в обоих случаях, и сочетание «роль запрещает» на деле
    /// проверяло «роль разрешает». Вторая: удаление не сохранялось, потому что
    /// выход происходил раньше <c>SaveChangesAsync</c>, — роль продолжала
    /// разрешать право и «запрещающий» случай проходил как «разрешающий».
    /// </remarks>
    private async Task SetRoleTemplateAsync(TestScope s, bool grantedByRole, params string[] codes)
    {
        var existing = await s.Db.RolePermissionTemplates
            .Where(t => t.RoleName == _subjectRole && codes.Contains(t.PermissionCode))
            .ToListAsync();

        if (existing.Count > 0)
            s.Db.RolePermissionTemplates.RemoveRange(existing);

        if (grantedByRole)
        {
            foreach (var code in codes)
            {
                s.Db.RolePermissionTemplates.Add(new RolePermissionTemplate
                {
                    Id = Guid.NewGuid(),
                    RoleName = _subjectRole,
                    PermissionCode = code,
                });
            }
        }

        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(_subjectUserId);
    }

    private async Task SeedOverrideAsync(TestScope s, string code, bool granted)
    {
        s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = _subjectUserId,
            PermissionCode = code,
            IsGranted = granted,
            Reason = "основание из теста",
            GrantedByUserId = _fixture.SystemAdminUser.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(_subjectUserId);
    }

    private async Task<UserEffectivePermissionDto> EffectiveAsync(TestScope s, string code)
    {
        var dto = await s.UserMgmt.GetEffectivePermissionsAsync(_subjectUserId);
        Assert.NotNull(dto);
        return Assert.Single(dto!.Permissions.Where(p => p.Code == code));
    }

    private async Task<UserPermissionOverride?> OverrideRowAsync(TestScope s, string code) =>
        await s.Db.UserPermissionOverrides
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.UserId == _subjectUserId && o.PermissionCode == code);

    // ── 1. Итоговый доступ по всем сочетаниям роли и решения ──────────────

    public static TheoryData<bool, bool?> Combinations => new()
    {
        // роль разрешает
        { true, null },   // доступ разрешён ролью, решения нет
        { true, true },   // роль разрешает, решение разрешающее (доступ тот же)
        { true, false },  // роль разрешает, решение запрещающее (доступ изменён)
        // роль запрещает
        { false, null },  // доступ запрещён, решения нет
        { false, true },  // роль запрещает, решение разрешающее (доступ изменён)
        { false, false }, // роль запрещает, решение запрещающее (доступ тот же)
    };

    [Theory]
    [MemberData(nameof(Combinations))]
    public async Task EffectiveAccess_CombinesRoleAndOverride(bool grantedByRole, bool? grantedOverride)
    {
        const string code = PermissionCodes.ReferenceView;
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        await SetRoleTemplateAsync(s, grantedByRole, code);
        if (grantedOverride.HasValue)
            await SeedOverrideAsync(s, code, grantedOverride.Value);

        var p = await EffectiveAsync(s, code);

        Assert.Equal(grantedByRole, p.GrantedByRole);
        Assert.Equal(grantedOverride, p.OverrideIsGranted);
        // Решение сильнее роли; без решения итог равен роли.
        Assert.Equal(grantedOverride ?? grantedByRole, p.IsEffective);
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public async Task CachedEffectivePermissions_MatchDatabase(bool grantedByRole, bool? grantedOverride)
    {
        const string code = PermissionCodes.ReferenceView;
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        await SetRoleTemplateAsync(s, grantedByRole, code);
        if (grantedOverride.HasValue)
            await SeedOverrideAsync(s, code, grantedOverride.Value);

        // Кэш заполняется первым запросом, второй обязан дать тот же ответ.
        var first = await s.Permissions.HasPermissionAsync(_subjectUserId, code);
        var second = await s.Permissions.HasPermissionAsync(_subjectUserId, code);

        Assert.Equal(grantedOverride ?? grantedByRole, first);
        Assert.Equal(first, second);
    }

    // ── 2. Команды меняют БД, итог и кэш ─────────────────────────────────

    [Fact]
    public async Task Grant_ForDeniedRole_CreatesOverride_AndFlipsEffective()
    {
        const string code = PermissionCodes.ReferenceView;
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        await SetRoleTemplateAsync(s, grantedByRole: false, code);

        Assert.False((await EffectiveAsync(s, code)).IsEffective);

        var (result, error) = await s.UserMgmt.GrantPermissionAsync(_subjectUserId, code, "нужно для работы");
        Assert.Null(error);
        Assert.NotNull(result);

        var row = await OverrideRowAsync(s, code);
        Assert.NotNull(row);
        Assert.True(row!.IsGranted);
        Assert.Equal("нужно для работы", row.Reason);

        Assert.True((await EffectiveAsync(s, code)).IsEffective);

        // Кэш эффективных прав обновлён: прямой запрос даёт новый ответ без
        // ручного сброса.
        Assert.True(await s.Permissions.HasPermissionAsync(_subjectUserId, code));
    }

    [Fact]
    public async Task Grant_ForAllowedRole_CreatesOverride_ButDoesNotChangeAccess()
    {
        const string code = PermissionCodes.ReferenceView;
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        await SetRoleTemplateAsync(s, grantedByRole: true, code);

        var (_, error) = await s.UserMgmt.GrantPermissionAsync(_subjectUserId, code, "подтвердить роль");
        Assert.Null(error);

        // Решение записано, но доступ остался тем же — роль его уже давала.
        var row = await OverrideRowAsync(s, code);
        Assert.NotNull(row);
        Assert.True(row!.IsGranted);
        Assert.True((await EffectiveAsync(s, code)).IsEffective);
    }

    [Fact]
    public async Task Deny_ForAllowedRole_CreatesOverride_AndBlocksAccess()
    {
        const string code = PermissionCodes.ReferenceView;
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        await SetRoleTemplateAsync(s, grantedByRole: true, code);
        Assert.True((await EffectiveAsync(s, code)).IsEffective);

        var (result, error) = await s.UserMgmt.DenyPermissionAsync(_subjectUserId, code, "ограничить");
        Assert.Null(error);
        Assert.NotNull(result);

        var row = await OverrideRowAsync(s, code);
        Assert.NotNull(row);
        Assert.False(row!.IsGranted);

        Assert.False((await EffectiveAsync(s, code)).IsEffective);
        Assert.False(await s.Permissions.HasPermissionAsync(_subjectUserId, code));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deny_WithoutPriorOverride_CreatesExactlyOneRow(bool grantedByRole)
    {
        const string code = PermissionCodes.ReferenceEdit;
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        await SetRoleTemplateAsync(s, grantedByRole, code);

        var (_, error) = await s.UserMgmt.DenyPermissionAsync(_subjectUserId, code, "запретить");
        Assert.Null(error);

        Assert.Equal(1, await s.Db.UserPermissionOverrides
            .CountAsync(o => o.UserId == _subjectUserId && o.PermissionCode == code));
    }

    [Fact]
    public async Task Revoke_RemovesOverride_AndRestoresRoleAccess()
    {
        // Ключевое различие: снятие решения возвращает доступ ПО РОЛИ, а не
        // записывает противоположное решение.
        const string code = PermissionCodes.ReferenceView;
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        await SetRoleTemplateAsync(s, grantedByRole: true, code);
        await SeedOverrideAsync(s, code, granted: false);

        Assert.False((await EffectiveAsync(s, code)).IsEffective);

        var (result, error) = await s.UserMgmt.RevokePermissionAsync(_subjectUserId, code);
        Assert.Null(error);
        Assert.NotNull(result);

        // Строки решения в БД больше нет — это снятие, а не запись «разрешить».
        Assert.Null(await OverrideRowAsync(s, code));

        var p = await EffectiveAsync(s, code);
        Assert.Null(p.OverrideIsGranted);
        Assert.True(p.GrantedByRole);
        Assert.True(p.IsEffective);
        Assert.True(await s.Permissions.HasPermissionAsync(_subjectUserId, code));
    }

    [Fact]
    public async Task Revoke_ForDeniedRole_RestoresDenial_NotGrant()
    {
        const string code = PermissionCodes.ReferenceView;
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        await SetRoleTemplateAsync(s, grantedByRole: false, code);
        await SeedOverrideAsync(s, code, granted: true);

        Assert.True((await EffectiveAsync(s, code)).IsEffective);

        var (_, error) = await s.UserMgmt.RevokePermissionAsync(_subjectUserId, code);
        Assert.Null(error);

        Assert.Null(await OverrideRowAsync(s, code));
        Assert.False((await EffectiveAsync(s, code)).IsEffective);
    }

    [Fact]
    public async Task RepeatedCommands_DoNotDuplicateOverride_Row()
    {
        const string code = PermissionCodes.ReferenceView;
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
        await SetRoleTemplateAsync(s, grantedByRole: false, code);

        await s.UserMgmt.GrantPermissionAsync(_subjectUserId, code, "первое");
        await s.UserMgmt.DenyPermissionAsync(_subjectUserId, code, "второе");
        await s.UserMgmt.GrantPermissionAsync(_subjectUserId, code, "третье");

        Assert.Equal(1, await s.Db.UserPermissionOverrides
            .CountAsync(o => o.UserId == _subjectUserId && o.PermissionCode == code));
    }

    // ── 3. Защищённая операция отклоняется сервером, а не кнопкой ─────────

    [Fact]
    public async Task SelfModification_IsRejectedByServer()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_subjectUserId);

        var (result, error) = await s.UserMgmt.GrantPermissionAsync(
            _subjectUserId, PermissionCodes.ReferenceView, "себе");

        Assert.Null(result);
        Assert.Contains("самого себя", error);
        Assert.Null(await OverrideRowAsync(s, PermissionCodes.ReferenceView));
    }

    [Fact]
    public async Task UnknownPermissionCode_IsRejectedByServer()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var (result, error) = await s.UserMgmt.GrantPermissionAsync(_subjectUserId, "HK.NoSuchCode", "нет такого");

        Assert.Null(result);
        Assert.Contains("Неизвестный код", error);
        Assert.Empty(await s.Db.UserPermissionOverrides
            .Where(o => o.UserId == _subjectUserId).ToListAsync());
    }

    [Fact]
    public async Task EmptyReason_IsRejectedByServer()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var (result, error) = await s.UserMgmt.GrantPermissionAsync(
            _subjectUserId, PermissionCodes.ReferenceView, "   ");

        Assert.Null(result);
        Assert.Contains("Причина обязательна", error);
    }

    [Fact]
    public async Task Revoke_WithoutDecision_IsRejectedByServer()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var (result, error) = await s.UserMgmt.RevokePermissionAsync(
            _subjectUserId, PermissionCodes.ReferenceView);

        Assert.Null(result);
        Assert.Contains("не найдено", error);
    }

    // ── 4. Права законсервированного модуля ИК не выдаются ───────────────

    [Theory]
    [InlineData(PermissionCodes.IndividualCardView)]
    [InlineData(PermissionCodes.IndividualCardCreateDraft)]
    [InlineData(PermissionCodes.IndividualCardArchive)]
    public async Task IndividualCardPermissions_AreNotOfferedInActiveForm(string code)
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        Assert.True(PermissionCatalog.IsConserved(code), "код должен быть помечен как законсервированный");

        var dto = await s.UserMgmt.GetEffectivePermissionsAsync(_subjectUserId);
        Assert.NotNull(dto);
        // Полномочие есть в полном каталоге (история сохраняется)…
        Assert.Contains(dto!.Permissions, p => p.Code == code);

        // …но активная форма его не предлагает.
        var offered = dto.Permissions.Where(p => !PermissionCatalog.IsConserved(p.Code)).ToList();
        Assert.DoesNotContain(offered, p => p.Code == code);
    }

    [Fact]
    public async Task ConservedPermissions_AreAbsentFromActiveCatalog()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var dto = await s.UserMgmt.GetEffectivePermissionsAsync(_subjectUserId);
        Assert.NotNull(dto);

        var active = dto!.Permissions.Where(p => !PermissionCatalog.IsConserved(p.Code)).ToList();
        var activeModules = active.Select(p => p.Module).Distinct().ToList();

        Assert.DoesNotContain(PermissionCatalog.ConservedIndividualCardModule, activeModules);
        Assert.All(PermissionCatalog.ConservedIndividualCardCodes,
            code => Assert.DoesNotContain(active, p => p.Code == code));
    }

    // ── 5. Новая сессия видит то же, что сохранено ───────────────────────

    [Fact]
    public async Task NewSession_AfterCommands_ReadsSavedState()
    {
        const string code = PermissionCodes.ReferenceView;
        await using (var setup = _fixture.CreateScope())
        {
            setup.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
            await SetRoleTemplateAsync(setup, grantedByRole: true, code);
            var (_, error) = await setup.UserMgmt.DenyPermissionAsync(_subjectUserId, code, "сохранённое решение");
            Assert.Null(error);
        }

        // Отдельная сессия и новый контекст: состояние обязано прийти из БД.
        await using (var fresh = _fixture.CreateScope())
        {
            fresh.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);
            var p = await EffectiveAsync(fresh, code);

            Assert.True(p.GrantedByRole);
            Assert.False(p.OverrideIsGranted);
            Assert.False(p.IsEffective);
            Assert.Equal("сохранённое решение", p.OverrideReason);
        }
    }
}
