using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Единое право HK.Work на создание и редактирование ХК.
/// <para>
/// Проверяется, что уровневые Create/EditDraft перестали быть условием,
/// индивидуальный запрет HK.Work действует, а права на согласование,
/// утверждение, архивирование и удаление остались отдельными.
/// </para>
/// </summary>
[Collection("Database")]
public class HKWorkPermissionIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKWorkPermissionIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    /// <summary>Область с заданным состоянием права HK.Work для пользователя A.</summary>
    private async Task<TestScope> OperatorScopeAsync(bool grant)
    {
        var scope = _fixture.CreateScope();
        var userId = scope.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);

        var existing = await scope.Db.UserPermissionOverrides
            .FirstOrDefaultAsync(o => o.UserId == userId.ToString() && o.PermissionCode == PermissionCodes.HKWork);

        if (existing is null)
        {
            scope.Db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                Id = Guid.NewGuid(),
                UserId = userId.ToString(),
                PermissionCode = PermissionCodes.HKWork,
                IsGranted = grant,
                Reason = "тест перевода на HK.Work",
                GrantedByUserId = userId.ToString(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.IsGranted = grant;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await scope.Db.SaveChangesAsync();
        scope.Permissions.InvalidateCache(userId.ToString());
        return scope;
    }

    private async Task<Guid> CreateNodeObjectAsync(TestScope s)
    {
        var node = new Node { Id = Guid.NewGuid(), Code = "NW-" + Suffix(), Name = "Узел " + Suffix() };
        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();
        return node.Id;
    }

    private static HKCard NewCard(Guid branchId, Guid nodeId) => new()
    {
        Id = Guid.NewGuid(),
        Code = "",
        Version = "",
        Status = HKCardStatus.Draft,
        ObjectLevel = HKObjectLevel.Node,
        NodeId = nodeId,
        BranchId = branchId,
        Purpose = "Проверка перевода прав",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    // ── Создание ──

    [Theory]
    [InlineData(HKObjectLevel.Node)]
    [InlineData(HKObjectLevel.Aggregate)]
    [InlineData(HKObjectLevel.EquipmentModel)]
    [InlineData(HKObjectLevel.Complex)]
    public async Task CreateAsync_HkWork_Allows_AllLevels(HKObjectLevel level)
    {
        await using var s = await OperatorScopeAsync(grant: true);

        var (complexId, modelId, aggregateId, nodeId) = await CreateObjectsAsync(s, level);
        var card = NewCard(_fixture.BranchA, nodeId);

        // Ровно одна объектная ссылка — того уровня, который проверяется.
        card.ObjectLevel = level;
        card.ComplexId = complexId;
        card.EquipmentModelId = modelId;
        card.AggregateId = aggregateId;
        card.NodeId = level == HKObjectLevel.Node ? nodeId : null;

        var created = await s.HK.CreateAsync(card);

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.False(string.IsNullOrEmpty(created.Code));
    }

    private async Task<(Guid? ComplexId, Guid? ModelId, Guid? AggregateId, Guid NodeId)> CreateObjectsAsync(
        TestScope s, HKObjectLevel level)
    {
        var complexId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        s.Db.Complexes.Add(new Complex { Id = complexId, Code = "C-" + Suffix(), Name = "Комплекс " + Suffix() });
        s.Db.EquipmentModels.Add(new EquipmentModel { Id = modelId, Index = "E-" + Suffix(), Name = "Изделие " + Suffix() });
        s.Db.Aggregates.Add(new Aggregate { Id = aggregateId, Code = "A-" + Suffix(), Name = "Агрегат " + Suffix() });
        s.Db.Nodes.Add(new Node { Id = nodeId, Code = "N-" + Suffix(), Name = "Узел " + Suffix() });
        await s.Db.SaveChangesAsync();

        return level switch
        {
            HKObjectLevel.Complex => (complexId, null, null, nodeId),
            HKObjectLevel.EquipmentModel => (null, modelId, null, nodeId),
            HKObjectLevel.Aggregate => (null, null, aggregateId, nodeId),
            _ => (null, null, null, nodeId)
        };
    }

    [Fact]
    public async Task CreateAsync_WithoutHkWork_IsRefused()
    {
        await using var s = await OperatorScopeAsync(grant: false);
        var nodeId = await CreateNodeObjectAsync(s);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.HK.CreateAsync(NewCard(_fixture.BranchA, nodeId)));
        Assert.Contains("прав", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_LegacyLevelPermission_DoesNotSubstituteForHkWork()
    {
        // У пользователя есть уровневое HK.Node.Create из шаблона Operator,
        // но индивидуальный запрет HK.Work: операция обязана быть отклонена.
        await using var s = await OperatorScopeAsync(grant: false);
        var nodeId = await CreateNodeObjectAsync(s);

        Assert.True(await s.Permissions.HasPermissionAsync(
            _fixture.OperatorA.Id, PermissionCodes.HKNodeCreate),
            "Уровневое право из шаблона должно сохраниться: старые коды не удаляются.");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.HK.CreateAsync(NewCard(_fixture.BranchA, nodeId)));
    }

    [Fact]
    public async Task CreateAsync_Autonomous_UsesSameHkWorkPermission()
    {
        // Единое право распространяется и на автономный сценарий: отдельного
        // права у него нет.
        await using var s = await OperatorScopeAsync(grant: true);

        var card = NewCard(_fixture.BranchA, Guid.NewGuid());
        card.IsAutonomous = true;
        card.ObjectLevel = HKObjectLevel.Undefined;
        card.NodeId = null;
        card.Name = "Автономная " + Suffix();

        var created = await s.HK.CreateAsync(card);

        Assert.True(created.IsAutonomous);
        Assert.StartsWith($"ХК-{card.Name}", created.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_Autonomous_StillRefusedWithoutHkWork()
    {
        await using var s = await OperatorScopeAsync(grant: false);

        var card = NewCard(_fixture.BranchA, Guid.NewGuid());
        card.IsAutonomous = true;
        card.ObjectLevel = HKObjectLevel.Undefined;
        card.NodeId = null;
        card.Name = "Автономная " + Suffix();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.HK.CreateAsync(card));
    }

    // ── Редактирование ──

    [Fact]
    public async Task UpdateAsync_HkWork_AllowsRegardlessOfLevel()
    {
        await using var s = await OperatorScopeAsync(grant: true);

        var complexId = Guid.NewGuid();
        s.Db.Complexes.Add(new Complex { Id = complexId, Code = "C-" + Suffix(), Name = "Комплекс " + Suffix() });
        await s.Db.SaveChangesAsync();

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "EDIT-" + Suffix(),
            Version = "v0101",
            Status = HKCardStatus.Draft,
            ObjectLevel = HKObjectLevel.Complex,
            ComplexId = complexId,
            BranchId = _fixture.BranchA,
            Purpose = "исходное",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        s.Db.HKCards.Add(card);
        await s.Db.SaveChangesAsync();

        var tracked = await s.Db.HKCards.FirstAsync(c => c.Id == card.Id);
        tracked.Purpose = "изменённое";
        tracked.RowVersion = await CurrentVersionAsync(s, card.Id);

        await s.HK.UpdateAsync(tracked);

        var check = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == card.Id);
        Assert.Equal("изменённое", check.Purpose);
    }

    private static async Task<uint> CurrentVersionAsync(TestScope s, Guid cardId)
    {
        s.Db.ChangeTracker.Clear();
        return (await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId)).RowVersion;
    }

    [Fact]
    public async Task UpdateAsync_WithoutHkWork_IsRefused()
    {
        await using var s = await OperatorScopeAsync(grant: false);
        var nodeId = await CreateNodeObjectAsync(s);

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "EDIT-" + Suffix(),
            Version = "v0101",
            Status = HKCardStatus.Draft,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = nodeId,
            BranchId = _fixture.BranchA,
            Purpose = "исходное",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        s.Db.HKCards.Add(card);
        await s.Db.SaveChangesAsync();

        var tracked = await s.Db.HKCards.FirstAsync(c => c.Id == card.Id);
        tracked.Purpose = "изменённое";
        tracked.RowVersion = await CurrentVersionAsync(s, card.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.HK.UpdateAsync(tracked));
    }

    [Fact]
    public async Task UpdateAsync_BranchRestriction_IsKept()
    {
        // HK.Work есть, но карта в чужом филиале: филиальная граница сохраняется.
        await using var s = await OperatorScopeAsync(grant: true);
        var nodeId = await CreateNodeObjectAsync(s);

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "EDIT-" + Suffix(),
            Version = "v0101",
            Status = HKCardStatus.Draft,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = nodeId,
            BranchId = _fixture.BranchB,
            Purpose = "исходное",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        s.Db.HKCards.Add(card);
        await s.Db.SaveChangesAsync();

        var tracked = await s.Db.HKCards.FirstAsync(c => c.Id == card.Id);
        tracked.Purpose = "изменённое";
        tracked.RowVersion = await CurrentVersionAsync(s, card.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.HK.UpdateAsync(tracked));
    }

    [Fact]
    public async Task UpdateAsync_StatusLock_IsKept()
    {
        // Право есть, но карта утверждена: правка заблокирована состоянием.
        await using var s = await OperatorScopeAsync(grant: true);
        var nodeId = await CreateNodeObjectAsync(s);

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "EDIT-" + Suffix(),
            Version = "v0101",
            Status = HKCardStatus.Approved,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = nodeId,
            BranchId = _fixture.BranchA,
            Purpose = "исходное",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        s.Db.HKCards.Add(card);
        await s.Db.SaveChangesAsync();

        var tracked = await s.Db.HKCards.FirstAsync(c => c.Id == card.Id);
        tracked.Purpose = "изменённое";
        tracked.RowVersion = await CurrentVersionAsync(s, card.Id);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.UpdateAsync(tracked));
        Assert.Contains("статус", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── HK.Work не даёт остальных действий ──

    [Fact]
    public async Task HkWork_DoesNotAllowSubmitReviewApproveArchiveDelete()
    {
        // Пользователю оставлено ТОЛЬКО HK.Work: права на согласование,
        // проверку, утверждение, архивирование и удаление запрещены индивидуально.
        await using var s = await OperatorScopeAsync(grant: true);
        var userId = _fixture.OperatorA.Id;

        foreach (var code in new[]
                 {
                     PermissionCodes.HKNodeSubmit, PermissionCodes.HKReview,
                     PermissionCodes.HKApprove, PermissionCodes.HKArchive,
                     PermissionCodes.HKDeleteDraft
                 })
        {
            await SetPermissionAsync(s, userId, code, granted: false);
        }
        s.Permissions.InvalidateCache(userId);

        foreach (var code in new[]
                 {
                     PermissionCodes.HKNodeSubmit, PermissionCodes.HKReview,
                     PermissionCodes.HKApprove, PermissionCodes.HKArchive,
                     PermissionCodes.HKDeleteDraft
                 })
        {
            Assert.False(await s.Permissions.HasPermissionAsync(userId, code),
                $"HK.Work не должен давать право {code}.");
        }

        var nodeId = await CreateNodeObjectAsync(s);

        // Удаление черновика: при отсутствии HK.Delete.Draft должно быть отказано.
        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "DEL-" + Suffix(),
            Version = "v0101",
            Status = HKCardStatus.Draft,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = nodeId,
            BranchId = _fixture.BranchA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        s.Db.HKCards.Add(card);
        await s.Db.SaveChangesAsync();

        var (ok, error) = await s.HK.DeleteAsync(card.Id, "причина", default);
        Assert.False(ok, "HK.Work не должен разрешать удаление.");
        Assert.NotNull(error);
    }

    private static async Task SetPermissionAsync(TestScope s, string userId, string code, bool granted)
    {
        var existing = await s.Db.UserPermissionOverrides
            .FirstOrDefaultAsync(o => o.UserId == userId && o.PermissionCode == code);

        if (existing is null)
        {
            s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                Id = Guid.NewGuid(), UserId = userId,
                PermissionCode = code, IsGranted = granted,
                GrantedByUserId = userId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.IsGranted = granted;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await s.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task ChangeStatus_HkWork_DoesNotReplaceSubmitPermission()
    {
        // Отправка на согласование остаётся на уровневом HK.Node.Submit.
        await using var s = await OperatorScopeAsync(grant: true);
        var nodeId = await CreateNodeObjectAsync(s);

        var au = new AssemblyUnit
        {
            Id = Guid.NewGuid(), Code = "AU-" + Suffix(), Name = "СЕ " + Suffix()
        };
        s.Db.AssemblyUnits.Add(au);
        await s.Db.SaveChangesAsync();

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "ST-" + Suffix(),
            Version = "v0101",
            Status = HKCardStatus.Draft,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = nodeId,
            BranchId = _fixture.BranchA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        card.Items.Add(new HKCardItem
        {
            Id = Guid.NewGuid(),
            AssemblyUnitId = au.Id,
            Quantity = 1,
            Volume = 1m,
            SortOrder = 0
        });
        s.Db.HKCards.Add(card);
        await s.Db.SaveChangesAsync();

        var (ok, error) = await s.HK.ChangeStatusAsync(card.Id, HKCardStatus.OnReview, null, default);

        // Решение определяет уровневое право отправки, а не HK.Work.
        Assert.Equal(
            await s.Permissions.HasPermissionAsync(_fixture.OperatorA.Id, PermissionCodes.HKNodeSubmit),
            ok);
        if (!ok) Assert.NotNull(error);
    }

    // ── Кэш разрешений ──

    [Fact]
    public async Task PermissionCache_HkWorkRespects_CacheAndAfterReset()
    {
        await using var s = _fixture.CreateScope();
        var userId = _fixture.OperatorA.Id;

        // Выдача и сброс кэша — тем же путём, что у UserManagementService.
        await SetHkWorkAsync(s, userId, granted: true);
        s.Permissions.InvalidateCache(userId);
        Assert.True(await s.Permissions.HasPermissionAsync(userId, PermissionCodes.HKWork));

        // Без сброса кэш продолжает отдавать прежний результат — это ожидаемо
        // и не является ошибкой доступа.
        await SetHkWorkAsync(s, userId, granted: false);
        Assert.True(await s.Permissions.HasPermissionAsync(userId, PermissionCodes.HKWork));

        s.Permissions.InvalidateCache(userId);
        Assert.False(await s.Permissions.HasPermissionAsync(userId, PermissionCodes.HKWork));

        await SetHkWorkAsync(s, userId, granted: true);
    }

    private static async Task SetHkWorkAsync(TestScope s, string userId, bool granted)
    {
        var existing = await s.Db.UserPermissionOverrides
            .FirstOrDefaultAsync(o => o.UserId == userId && o.PermissionCode == PermissionCodes.HKWork);

        if (existing is null)
        {
            s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                Id = Guid.NewGuid(), UserId = userId,
                PermissionCode = PermissionCodes.HKWork, IsGranted = granted,
                GrantedByUserId = userId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.IsGranted = granted;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await s.Db.SaveChangesAsync();
    }
}