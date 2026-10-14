using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Доступ к конкретной карте в сервисе согласований.
/// <para>
/// Наличие полномочия само по себе не открывает карту: право есть у многих, а
/// решение по документу принимает назначенный согласующий, и отправляет его
/// автор. Каждая проверка живёт в сервисе — контроллер и Blazor защитой не
/// являются.
/// </para>
/// </summary>
[Collection("Database")]
public class HKApprovalAccessIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKApprovalAccessIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    private async Task<TestScope> ScopeAsync(string userId)
    {
        var scope = _fixture.CreateScope();
        scope.User.CurrentUserId = Guid.Parse(userId);
        scope.Permissions.InvalidateCache(userId);
        return scope;
    }

    private Task<TestScope> AuthorAsync() => ScopeAsync(_fixture.NormAdminA.Id);
    private Task<TestScope> ApproverAsync() => ScopeAsync(_fixture.NormAdminA2.Id);

    /// <summary>Карта в филиале BranchA, автор — NormAdminA.</summary>
    private async Task<Guid> CardAsync()
    {
        await using var s = await AuthorAsync();
        return await HKApprovalScenario.CreateDraftCardAsync(s, _fixture.NormAdminA.Id);
    }

    /// <summary>Пользователь с явным индивидуальным ЗАПРЕТОМ права просмотра.</summary>
    private async Task<string> CreateUserWithoutViewAsync(Guid branchId)
    {
        var login = "noview_" + Suffix().ToLowerInvariant();
        await using var setup = _fixture.CreateScope();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = login,
            FullName = "Тест " + login,
            BranchId = branchId,
            IsActive = true
        };

        var result = await setup.Users.CreateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                string.Join("; ", result.Errors.Select(e => e.Description)));

        await setup.Users.AddToRoleAsync(user, nameof(UserRole.Operator));
        setup.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PermissionCode = PermissionCodes.HKView,
            IsGranted = false,
            GrantedByUserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await setup.Db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>
    /// Отключение пользователя всегда обратимо: общая тестовая БД живёт между
    /// тестами, и оставленный отключённым согласующий ломал бы чужие сценарии.
    /// </summary>
    private async Task SetUserActiveAsync(string userId, bool active)
    {
        await using var s = _fixture.CreateScope();
        var user = await s.Db.Users.SingleAsync(u => u.Id == userId);
        user.IsActive = active;
        await s.Db.SaveChangesAsync();
    }

    private async Task<string> CreateUserAsync(Guid branchId, string role, params string[] permissions)
    {
        var login = "acc_" + Suffix().ToLowerInvariant();
        await using var setup = _fixture.CreateScope();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = login,
            FullName = "Тест " + login,
            BranchId = branchId,
            IsActive = true
        };

        var result = await setup.Users.CreateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                string.Join("; ", result.Errors.Select(e => e.Description)));

        await setup.Users.AddToRoleAsync(user, role);
        login = user.Id;

        foreach (var code in permissions)
        {
            setup.Db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                Id = Guid.NewGuid(),
                UserId = login,
                PermissionCode = code,
                IsGranted = true,
                GrantedByUserId = login,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await setup.Db.SaveChangesAsync();
        return login;
    }

    // ── Отправка ──

    [Fact]
    public async Task SubmitDetails_AnotherUsersCardInSameBranch_IsRefused()
    {
        // Право HK.Submit есть, филиал тот же, но отправить может автор.
        var cardId = await CardAsync();
        var stranger = await CreateUserAsync(_fixture.BranchA, nameof(UserRole.Operator),
            PermissionCodes.HKSubmit, PermissionCodes.HKWork);

        await using var s = await ScopeAsync(stranger);
        var version = await HKApprovalScenario.VersionAsync(s, cardId);

        var ex = await Assert.ThrowsAsync<HkCardAccessDeniedException>(
            () => s.Approvals.SubmitDetailsAsync(cardId, _fixture.NormAdminA2.Id, version));
        Assert.Contains("автор", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitDetails_AnotherBranch_IsRefused()
    {
        var cardId = await CardAsync();
        var outsider = await CreateUserAsync(_fixture.BranchB, nameof(UserRole.Operator),
            PermissionCodes.HKSubmit, PermissionCodes.HKWork);

        await using var s = await ScopeAsync(outsider);
        var version = await HKApprovalScenario.VersionAsync(s, cardId);

        await Assert.ThrowsAsync<HkCardAccessDeniedException>(
            () => s.Approvals.SubmitDetailsAsync(cardId, _fixture.NormAdminA2.Id, version));
    }

    [Fact]
    public async Task SubmitDetails_WithoutHkSubmit_IsRefused()
    {
        var cardId = await CardAsync();

        await using var s = await ScopeAsync(_fixture.GuestA.Id);
        var version = await HKApprovalScenario.VersionAsync(s, cardId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.Approvals.SubmitDetailsAsync(cardId, _fixture.NormAdminA2.Id, version));
    }

    // ── Чтение ──

    [Fact]
    public async Task GetStages_AnotherBranch_IsRefused()
    {
        var cardId = await CardAsync();
        var outsider = await CreateUserAsync(_fixture.BranchB, nameof(UserRole.Operator),
            PermissionCodes.HKView);

        await using var s = await ScopeAsync(outsider);

        // Этапы и согласующий не раскрываются по одному известному Id.
        await Assert.ThrowsAsync<HkCardAccessDeniedException>(() => s.Approvals.GetStagesAsync(cardId));
    }

    [Fact]
    public async Task GetStages_WithoutViewPermission_IsRefused()
    {
        var cardId = await CardAsync();
        var noViewer = await CreateUserWithoutViewAsync(_fixture.BranchA);

        await using var s = await ScopeAsync(noViewer);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Approvals.GetStagesAsync(cardId));
    }

    // ── Решение ──

    [Fact]
    public async Task Decide_WithoutStagePermission_IsRefused()
    {
        var cardId = await CardAsync();
        await HKApprovalScenario.SubmitDetailsAsync(
            await AuthorAsync(), cardId, _fixture.NormAdminA2.Id);

        var stranger = await CreateUserAsync(_fixture.BranchA, nameof(UserRole.Operator),
            PermissionCodes.HKWork, PermissionCodes.HKSubmit);

        await using var s = await ScopeAsync(stranger);
        var version = await HKApprovalScenario.VersionAsync(s, cardId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.Approvals.ApproveDetailsAsync(cardId, null, version));
    }

    [Fact]
    public async Task Decide_DeactivatedApprover_IsRefused()
    {
        var cardId = await CardAsync();
        await HKApprovalScenario.SubmitDetailsAsync(await AuthorAsync(), cardId, _fixture.NormAdminA2.Id);

        try
        {
            await SetUserActiveAsync(_fixture.NormAdminA2.Id, false);

            await using var s = await ApproverAsync();
            var version = await HKApprovalScenario.VersionAsync(s, cardId);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => s.Approvals.ApproveDetailsAsync(cardId, null, version));

            // Состояние согласования при этом не меняется.
            await using var check = await AuthorAsync();
            var stage = await HKApprovalScenario.StageAsync(check, cardId, HKApprovalStage.Details);
            Assert.Equal(HKApprovalStageState.OnReview, stage.State);
        }
        finally
        {
            await SetUserActiveAsync(_fixture.NormAdminA2.Id, true);
        }
    }

    [Fact]
    public async Task ResubmitComposition_WithDeactivatedApprover_IsRefused()
    {
        // Согласующий состава — тот же человек, что принял реквизиты. Если его
        // отключили, повторная отправка обязана отказать, а не подменить его.
        var cardId = await CardAsync();
        var auId = await CreateAssemblyUnitAsync();
        await using (var treeScope = await AuthorAsync())
        {
            await treeScope.HK.SaveTreeAsync(cardId, new[]
            {
                new HKCardNodeWrite(Guid.NewGuid(), null, HKObjectLevel.EquipmentModel, "Изделие", 1, 0)
            });
        }
        await AttachItemAsync(cardId, auId);

        await HKApprovalScenario.SubmitDetailsAsync(await AuthorAsync(), cardId, _fixture.NormAdminA2.Id);
        await using (var approver = await ApproverAsync())
        {
            await HKApprovalScenario.ApproveDetailsAsync(approver, cardId);
        }

        try
        {
            await SetUserActiveAsync(_fixture.NormAdminA2.Id, false);

            await using var s = await AuthorAsync();
            var version = await HKApprovalScenario.VersionAsync(s, cardId);

            // Предусловие: согласующий действительно отключён.
            var approverState = await s.Db.Users.AsNoTracking()
                .Where(u => u.Id == _fixture.NormAdminA2.Id)
                .Select(u => u.IsActive)
                .FirstAsync();
            Assert.False(approverState, "Согласующий должен быть отключён для этого сценария.");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => s.Approvals.SubmitCompositionAsync(cardId, version));

            // Согласующий не подменён: этап не ушёл на рассмотрение.
            var composition = await HKApprovalScenario.StageAsync(s, cardId, HKApprovalStage.Composition);
            Assert.NotEqual(HKApprovalStageState.OnReview, composition.State);
        }
        finally
        {
            await SetUserActiveAsync(_fixture.NormAdminA2.Id, true);
        }
    }

    // ── Чтение и инициализация ──

    [Fact]
    public async Task GetStages_DoesNotCreateAnything()
    {
        var cardId = await CardAsync();

        // Этапы удалены напрямую: карта нового процесса без них.
        await using (var setup = _fixture.CreateScope())
        {
            await setup.Db.HKCardApprovals.Where(a => a.HKCardId == cardId).ExecuteDeleteAsync();
        }

        await using (var s = await AuthorAsync())
        {
            var stages = await s.Approvals.GetStagesAsync(cardId);
            Assert.Empty(stages);
        }

        // Чтение не создало ни одной записи.
        await using (var check = await AuthorAsync())
        {
            var count = await check.Db.HKCardApprovals.AsNoTracking()
                .CountAsync(a => a.HKCardId == cardId);
            Assert.Equal(0, count);
        }
    }

    [Fact]
    public async Task FirstCommand_PersistsMissingStages()
    {
        var cardId = await CardAsync();

        await using (var setup = _fixture.CreateScope())
        {
            await setup.Db.HKCardApprovals.Where(a => a.HKCardId == cardId).ExecuteDeleteAsync();
        }

        await HKApprovalScenario.SubmitDetailsAsync(await AuthorAsync(), cardId, _fixture.NormAdminA2.Id);

        // Этапы действительно записаны, а не остались в памяти контекста.
        await using (var check = await AuthorAsync())
        {
            var stages = await check.Db.HKCardApprovals.AsNoTracking()
                .Where(a => a.HKCardId == cardId).ToListAsync();

            Assert.Equal(2, stages.Count);
            Assert.Contains(stages, a => a.Stage == HKApprovalStage.Details
                                        && a.State == HKApprovalStageState.OnReview);
            Assert.Contains(stages, a => a.Stage == HKApprovalStage.Composition
                                        && a.State == HKApprovalStageState.Draft);
            Assert.All(stages, a => Assert.Null(a.ApprovedAtUtc));
        }
    }

    [Fact]
    public async Task ParallelInitialization_DoesNotDuplicateStagePair()
    {
        var cardId = await CardAsync();

        await using (var setup = _fixture.CreateScope())
        {
            await setup.Db.HKCardApprovals.Where(a => a.HKCardId == cardId).ExecuteDeleteAsync();
        }

        // Две независимые команды одного автора со стартовой версией.
        // Одна из них создаст этапы, вторая должна отсеяться по версии, а не
        // вставить вторую пару.
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var s = await AuthorAsync();
            var version = await HKApprovalScenario.VersionAsync(s, cardId);
            try
            {
                await s.Approvals.SubmitDetailsAsync(cardId, _fixture.NormAdminA2.Id, version);
                return "ok";
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return ex.GetType().Name;
            }
        }));

        Assert.Contains("ok", outcomes);

        await using var check = await AuthorAsync();
        var stages = await check.Db.HKCardApprovals.AsNoTracking()
            .Where(a => a.HKCardId == cardId).ToListAsync();

        Assert.Equal(2, stages.Count);
        Assert.Equal(2, stages.Select(a => a.Stage).Distinct().Count());
    }

    // ── Подмена состояния ──

    [Fact]
    public async Task SubmitDetails_TrackedEntityMutatedByCaller_DoesNotBypassAuthorCheck()
    {
        var cardId = await CardAsync();

        var stranger = await CreateUserAsync(_fixture.BranchA, nameof(UserRole.Operator),
            PermissionCodes.HKSubmit, PermissionCodes.HKWork, PermissionCodes.HKApprove);

        await using var s = await ScopeAsync(stranger);

        // Вызывающий код загрузил карту в свой контекст и подменил автора.
        // В базе автор прежний: команда обязана опираться на сохранённое
        // состояние, а не на изменённый в памяти отслеживаемый объект.
        var tracked = await s.Db.HKCards.FirstAsync(c => c.Id == cardId);
        tracked.AuthorId = Guid.Parse(stranger);

        var persisted = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        Assert.NotEqual(Guid.Parse(stranger), persisted.AuthorId);

        await Assert.ThrowsAsync<HkCardAccessDeniedException>(
            () => s.Approvals.SubmitDetailsAsync(cardId, _fixture.NormAdminA2.Id, persisted.RowVersion));
    }

    [Fact]
    public async Task SubmitDetails_StaleRowVersion_IsRefused()
    {
        var cardId = await CardAsync();
        await using var s = await AuthorAsync();

        var current = await HKApprovalScenario.VersionAsync(s, cardId);

        // Карта изменилась после того, как экран её прочитал.
        await using (var other = await AuthorAsync())
        {
            await other.HK.UpdateAsync(await ReloadedAsync(other, cardId, current, "Правка"));
        }

        var stale = await HKApprovalScenario.VersionAsync(s, cardId);
        Assert.NotEqual(current, stale);

        await Assert.ThrowsAsync<HkCardVersionConflictException>(() =>
            s.Approvals.SubmitDetailsAsync(cardId, _fixture.NormAdminA2.Id, current));
    }

    [Fact]
    public async Task TwoParallelDecisions_FromSameVersion_OneWins_OtherRefused()
    {
        // НАСТОЯЩАЯ гонка: два отдельных scope и DbContext, одна исходная версия,
        // синхронный запуск. Не последовательные вызовы: здесь важно, что оба
        // решения увидят одну и ту же версию до применения.
        var cardId = await CardAsync();
        await HKApprovalScenario.SubmitDetailsAsync(await AuthorAsync(), cardId, _fixture.NormAdminA2.Id);

        await using var seed = await ApproverAsync();
        var startVersion = await HKApprovalScenario.VersionAsync(seed, cardId);

        // Синхронный старт: каждый поток сообщает о готовности и ждёт
        // разрешения. Оба решения поэтому стартуют с ОДНОЙ исходной версии.
        var arrived = 0;
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task WaitForBothAsync()
        {
            if (Interlocked.Increment(ref arrived) == 2)
                startGate.TrySetResult();
            await startGate.Task;
        }

        var outcomes = await Task.WhenAll(
            Task.Run(async () =>
            {
                await using var s = await ApproverAsync();
                await WaitForBothAsync();
                try
                {
                    await s.Approvals.ApproveDetailsAsync(cardId, null, startVersion);
                    return "approved";
                }
                catch (Exception ex)
                {
                    return ex.GetType().Name;
                }
            }),
            Task.Run(async () =>
            {
                await using var s = await ApproverAsync();
                await WaitForBothAsync();
                try
                {
                    await s.Approvals.ReturnDetailsAsync(cardId, null, startVersion);
                    return "returned";
                }
                catch (Exception ex)
                {
                    return ex.GetType().Name;
                }
            }));

        var results = outcomes;

        // Ровно одно решение состоялось, второе отказано контролируемо.
        Assert.Equal(1, results.Count(r => r is "approved" or "returned"));
        // Отказ контролируемый. Проигравшая сторона отклоняется либо на проверке
        // версии карты, либо на токене xmin строки этапа, если обе успели
        // прочитать одну исходную версию. Оба отказа приводятся к одному типу
        // конфликта и означают одно: второе решение не состоялось.
        var refused = results.Single(r => r is not ("approved" or "returned"));
        Assert.Equal(nameof(HkCardVersionConflictException), refused);

        // Итоговое состояние одно и соответствует выигравшему решению.
        await using var check = await AuthorAsync();
        var stage = await HKApprovalScenario.StageAsync(check, cardId, HKApprovalStage.Details);
        var expected = results.Contains("approved")
            ? HKApprovalStageState.Approved
            : HKApprovalStageState.RevisionRequired;
        Assert.Equal(expected, stage.State);

        // Открытых задач рассмотрения не осталось.
        var openReviews = await check.Db.WorkTasks.AsNoTracking()
            .CountAsync(x => !x.IsDeleted && x.EntityId == cardId
                             && x.Type == WorkTaskType.HKDetailsReview
                             && (x.Status == WorkTaskStatus.Open
                                 || x.Status == WorkTaskStatus.InProgress
                                 || x.Status == WorkTaskStatus.Overdue));
        Assert.Equal(0, openReviews);
    }

    // ── Вспомогательное ──

    private async Task<HKCard> ReloadedAsync(TestScope s, Guid cardId, uint version, string purpose)
    {
        var card = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
        card.RowVersion = version;
        card.Purpose = purpose;
        return await s.HK.UpdateAsync(card);
    }

    private async Task<Guid> CreateAssemblyUnitAsync()
    {
        await using var s = await AuthorAsync();
        var au = new AssemblyUnit { Id = Guid.NewGuid(), Code = "AU-" + Suffix(), Name = "СЕ " + Suffix() };
        s.Db.AssemblyUnits.Add(au);
        await s.Db.SaveChangesAsync();
        return au.Id;
    }

    private async Task AttachItemAsync(Guid cardId, Guid assemblyUnitId)
    {
        await using var s = await AuthorAsync();
        var nodeId = await s.Db.HKCardNodes.AsNoTracking()
            .Where(n => n.HKCardId == cardId).Select(n => (Guid?)n.Id).FirstOrDefaultAsync();

        s.Db.HKCardItems.Add(new HKCardItem
        {
            Id = Guid.NewGuid(), HKCardId = cardId, HKCardNodeId = nodeId,
            AssemblyUnitId = assemblyUnitId, Quantity = 1, Volume = 1m, SortOrder = 0
        });
        await s.Db.SaveChangesAsync();
    }
}