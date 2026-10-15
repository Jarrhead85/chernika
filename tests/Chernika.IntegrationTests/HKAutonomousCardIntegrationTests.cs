using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Создание автономной ХК: название, сформированный номер и реквизиты.
/// <para>
/// Сценарий проверяется через сервис, а не прямой вставкой: номер формирует
/// сервер, и подделать его в DbSet нельзя.
/// </para>
/// </summary>
[Collection("Database")]
public class HKAutonomousCardIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKAutonomousCardIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    private async Task<TestScope> AuthorScopeAsync()
    {
        var scope = _fixture.CreateScope();
        scope.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        scope.Permissions.InvalidateCache(_fixture.NormAdminA.Id);
        return scope;
    }

    private static HKCard NewAutonomousDraft(Guid branchId, string? name) => new()
    {
        Id = Guid.NewGuid(),
        Code = "",
        Name = name,
        IsAutonomous = true,
        ObjectLevel = HKObjectLevel.Undefined,
        BranchId = branchId,
        Purpose = "Проверка автономного создания",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static int CurrentYear() => _year ?? DateTime.UtcNow.Year;

    private static int? _year;

    // ── Создание ──

    [Fact]
    public async Task CreateAsync_Autonomous_WithoutObject_CreatesDraft()
    {
        await using var s = await AuthorScopeAsync();

        var card = NewAutonomousDraft(_fixture.BranchA, "Насосный агрегат");

        var created = await s.HK.CreateAsync(card);

        Assert.Equal(Guid.Parse(_fixture.NormAdminA.Id), created.AuthorId);
        Assert.Equal(_fixture.BranchA, created.BranchId);
        Assert.Equal(HKCardStatus.Draft, created.Status);
        Assert.True(created.IsAutonomous);
        Assert.Equal(HKObjectLevel.Undefined, created.ObjectLevel);
        Assert.Null(created.NodeId);
        Assert.Null(created.ComplexId);
        Assert.Null(created.EquipmentModelId);
        Assert.Null(created.AggregateId);
    }

    [Fact]
    public async Task CreateAsync_Autonomous_EmptyTree_IsAccepted()
    {
        await using var s = await AuthorScopeAsync();

        var created = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, "Пустая структура"));

        var nodes = await s.Db.HKCardNodes.AsNoTracking()
            .Where(n => n.HKCardId == created.Id).CountAsync();
        Assert.Equal(0, nodes);
    }

    [Fact]
    public async Task CreateAsync_Autonomous_SavesName()
    {
        await using var s = await AuthorScopeAsync();

        const string name = "Насосный агрегат";
        var created = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, name));

        var stored = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == created.Id);
        Assert.Equal(name, stored.Name);
    }

    [Fact]
    public async Task CreateAsync_Autonomous_TrimsNameEdges()
    {
        await using var s = await AuthorScopeAsync();

        var created = await s.HK.CreateAsync(
            NewAutonomousDraft(_fixture.BranchA, "   Насосный агрегат   "));

        var stored = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == created.Id);
        Assert.Equal("Насосный агрегат", stored.Name);
    }

    [Fact]
    public async Task CreateAsync_Autonomous_BuildsNumberFromNameAndYear()
    {
        await using var s = await AuthorScopeAsync();

        // Имя уникально внутри набора: общая тестовая БД живёт между тестами,
        // иначе ожидался бы суффикс совпадения, а не базовый номер.
        var name = "Насосный агрегат " + Suffix();
        var year = s.Clock.GetUtcNow().UtcDateTime.Year;
        var created = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, name));

        Assert.Equal($"ХК-{name}-{year}", created.Code);
        Assert.StartsWith("ХК-", created.Code);
        Assert.DoesNotContain("AUTONOMOUS", created.Code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_Autonomous_KeepsInnerHyphensAndSpaces()
    {
        await using var s = await AuthorScopeAsync();

        var name = "Узел насоса АБВ-12Г " + Suffix();
        var year = s.Clock.GetUtcNow().UtcDateTime.Year;
        var created = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, name));

        // Дефисы внутри названия — часть номера, а не границы справочного кода.
        Assert.Equal($"ХК-{name}-{year}", created.Code);
        Assert.True(HKCardService.IsValidAutonomousCode(created.Code));
    }

    [Fact]
    public async Task CreateAsync_Autonomous_NumberEndingInDigits_IsNotMistakenForSuffix()
    {
        await using var s = await AuthorScopeAsync();

        var name = "Агрегат 202 " + Suffix();
        var year = s.Clock.GetUtcNow().UtcDateTime.Year;
        var created = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, name));

        Assert.Equal($"ХК-{name}-{year}", created.Code);
    }

    [Fact]
    public async Task CreateAsync_Autonomous_SameName_Twice_AddsSuffix()
    {
        await using var s = await AuthorScopeAsync();

        var name = "Дубликат " + Suffix();
        var first = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, name));
        var second = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, name));

        Assert.NotEqual(first.Code, second.Code);
        Assert.Equal(first.Code + "-2", second.Code);

        // Совпадение названия не объединяет карты: это разные документы.
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task CreateAsync_Autonomous_NumberIsProtectedByUniqueIndex()
    {
        // Проверка «номер пока свободен» перед вставкой сама по себе не
        // доказывает конкурентную безопасность: два создания могут выбрать
        // один номер. Доказывает это реальный уникальный индекс.
        await using var s = await AuthorScopeAsync();

        var index = await ReadIndexDefinitionAsync(s, "IX_HKCards_Code_Version");

        Assert.NotNull(index);
        Assert.Contains("UNIQUE", index!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Code", index!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Version", index!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_Autonomous_DuplicateNumber_IsRejectedByDatabase()
    {
        // Реальное следствие индекса: повтор номера и версии отвергается базой,
        // а не молча создаёт вторую карту с тем же номером.
        await using var s = await AuthorScopeAsync();
        var name = "Дубль " + Suffix();
        var first = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, name));

        var clone = NewAutonomousDraft(_fixture.BranchA, name);
        clone.Id = Guid.NewGuid();
        clone.Code = first.Code;
        clone.Version = first.Version;
        s.Db.HKCards.Add(clone);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        Assert.Contains("IX_HKCards_Code_Version", ex.ToString(), StringComparison.Ordinal);

        // Второй документ не создан.
        Assert.Equal(1, await s.Db.HKCards.AsNoTracking().CountAsync(c => c.Name == name));
    }

    [Fact]
    public async Task CreateAsync_Autonomous_WritesAuditForCreatedCard()
    {
        // Карта и аудит пишутся в одной транзакции, карта — раньше аудита.
        // Запись аудита обязана существовать: AuditService.CreateLogAsync только
        // добавляет её в трекер, и сохранение делает вызывающий.
        await using var s = await AuthorScopeAsync();
        var created = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, "Аудит " + Suffix()));

        var audit = await s.Db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "HKCard" && a.EntityId == created.Id.ToString())
            .ToListAsync();

        Assert.Contains(audit, a => a.Action == "Created");
    }

    [Fact]
    public async Task CreateAsync_Autonomous_RejectedCreation_LeavesNoAuditRecord()
    {
        // Запись аудита не должна появляться для документа, который не создан.
        await using var s = await AuthorScopeAsync();
        var rejectedId = Guid.NewGuid();

        var card = NewAutonomousDraft(_fixture.BranchA, "   ");
        card.Id = rejectedId;

        await Assert.ThrowsAsync<HKCardValidationException>(() => s.HK.CreateAsync(card));

        Assert.Equal(0, await s.Db.HKCards.AsNoTracking().CountAsync(c => c.Id == rejectedId));
        Assert.Equal(0, await s.Db.AuditLogs.AsNoTracking().CountAsync(a => a.EntityId == rejectedId.ToString()));
    }

    [Fact]
    public async Task CreateAsync_Autonomous_BlankName_IsRefused()
    {
        await using var s = await AuthorScopeAsync();

        foreach (var bad in new[] { null, "", "   " })
        {
            var ex = await Assert.ThrowsAsync<HKCardValidationException>(
                () => s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, bad)));
            Assert.Contains(ex.Errors, e => e.Code == "name-required");
        }
    }

    [Fact]
    public async Task CreateAsync_Autonomous_TooLongName_IsRefused()
    {
        await using var s = await AuthorScopeAsync();

        var tooLong = new string('Н', HKCard.NameMaxLength + 1);

        var ex = await Assert.ThrowsAsync<HKCardValidationException>(
            () => s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, tooLong)));
        Assert.Contains(ex.Errors, e => e.Code == "text-too-long");
    }

    [Fact]
    public async Task CreateAsync_Autonomous_MaxLengthName_ProducesNumberWithinColumnLimit()
    {
        await using var s = await AuthorScopeAsync();

        var year = s.Clock.GetUtcNow().UtcDateTime.Year;
        var maxName = new string('Н', HKCard.NameMaxLength);
        var created = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, maxName));

        // Название не обрезается молча ради лимита колонки.
        Assert.Equal(maxName, created.Name);
        Assert.True(created.Code.Length <= HKCard.CodeMaxLength);
        Assert.True(created.Code.Length >= 3 + HKCard.NameMaxLength + 1 + year.ToString().Length);
    }

    // ── Права и границы ──

    [Fact]
    public async Task CreateAsync_Autonomous_WithoutHkWork_IsRefused()
    {
        await using var s = _fixture.CreateScope();
        var userId = s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);

        var deny = await s.Db.UserPermissionOverrides.FirstOrDefaultAsync(
            o => o.UserId == userId.ToString() && o.PermissionCode == PermissionCodes.HKWork);
        if (deny is null)
        {
            s.Db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                Id = Guid.NewGuid(), UserId = userId.ToString(),
                PermissionCode = PermissionCodes.HKWork, IsGranted = false,
                GrantedByUserId = userId.ToString(),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            deny.IsGranted = false;
        }
        await s.Db.SaveChangesAsync();
        s.Permissions.InvalidateCache(userId.ToString());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, "Без права")));

        // Возвращаем шаблонное состояние, чтобы решение не утекло в другие тесты.
        if (deny is not null)
        {
            deny.IsGranted = true;
            await s.Db.SaveChangesAsync();
        }
        s.Permissions.InvalidateCache(userId.ToString());
    }

    [Fact]
    public async Task CreateAsync_Autonomous_OtherBranch_KeepsAuthorBranch()
    {
        await using var s = await AuthorScopeAsync();

        var created = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchB, "Чужой филиал"));

        // Филиал назначает сервер: запросу подменить организацию нельзя.
        Assert.Equal(_fixture.BranchA, created.BranchId);
    }

    // ── Редактирование ──

    [Fact]
    public async Task UpdateAsync_Autonomous_ChangesNameWithoutRenumbering()
    {
        await using var s = await AuthorScopeAsync();
        var created = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, "Прежнее название"));
        var numberBefore = created.Code;

        created.Name = "Новое название";
        created.RowVersion = await CurrentRowVersionAsync(s, created.Id);
        await s.HK.UpdateAsync(created);

        var stored = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == created.Id);
        Assert.Equal("Новое название", stored.Name);
        Assert.Equal(numberBefore, stored.Code);
    }

    [Fact]
    public async Task UpdateAsync_Autonomous_KeepsTreeItemsNodeLinksAndMaterials()
    {
        var cardId = await CreateAutonomousCardWithTreeAsync();

        await using var s = await AuthorScopeAsync();

        var card = await s.Db.HKCards.Include(x => x.Items).ThenInclude(i => i.Materials)
            .FirstAsync(c => c.Id == cardId);

        card.Name = "Переименованная карта";
        card.Purpose = "Новое назначение";
        card.RowVersion = card.RowVersion;
        await s.HK.UpdateAsync(card);

        await using var check = await AuthorScopeAsync();
        var stored = await check.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);

        Assert.Equal("Переименованная карта", stored.Name);
        Assert.Equal("Новое назначение", stored.Purpose);

        // Дерево и привязки строк не тронуты правкой реквизитов.
        Assert.Equal(2, await check.Db.HKCardNodes.AsNoTracking().CountAsync(n => n.HKCardId == cardId));

        var item = await check.Db.HKCardItems.AsNoTracking()
            .Include(i => i.Materials).FirstAsync(i => i.HKCardId == cardId);
        Assert.NotNull(item.HKCardNodeId);
        Assert.NotEmpty(item.Materials);
    }

    [Fact]
    public async Task UpdateAsync_Autonomous_ScenarioSwitchIsNotApplied()
    {
        await using var s = await AuthorScopeAsync();
        var created = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, "Режим"));

        // Попытка выдать автономную карту за карту объекта нормирования.
        // Значения принадлежат созданию: сервис применяет только разрешённые
        // поля и восстанавливает сохранённые, а не принимает подменённые.
        var impostor = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == created.Id);
        impostor.IsAutonomous = false;
        impostor.ObjectLevel = HKObjectLevel.Node;
        impostor.NodeId = Guid.NewGuid();

        await s.HK.UpdateAsync(impostor);

        var stored = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == created.Id);
        Assert.True(stored.IsAutonomous);
        Assert.Equal(HKObjectLevel.Undefined, stored.ObjectLevel);
        Assert.Null(stored.NodeId);
    }

    [Fact]
    public async Task UpdateAsync_Autonomous_ApprovedCard_IsRefused()
    {
        await using var s = await AuthorScopeAsync();
        var created = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, "Утверждённая"));

        // Статус меняется напрямую: смена статуса здесь — условие сценария,
        // а не проверяемая операция.
        var approved = await s.Db.HKCards.FirstAsync(c => c.Id == created.Id);
        approved.Status = HKCardStatus.Approved;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var card = await s.Db.HKCards.FirstAsync(c => c.Id == created.Id);
        card.Purpose = "Правка утверждённой";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.HK.UpdateAsync(card));
        Assert.Contains("статус", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAsync_Autonomous_OtherBranch_IsRefused()
    {
        await using var s = await AuthorScopeAsync();
        var created = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, "Граница"));

        var moved = await s.Db.HKCards.FirstAsync(c => c.Id == created.Id);
        moved.BranchId = _fixture.BranchB;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var card = await s.Db.HKCards.FirstAsync(c => c.Id == created.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.HK.UpdateAsync(card));
    }

    // ── Жизненный цикл: незавершённый workflow ──

    [Fact]
    public async Task ChangeStatusAsync_Autonomous_CannotBypassStageWorkflow()
    {
        // Общий переключатель статуса обошёл бы оба согласования: отправку,
        // принятие и возврат. Для карты с этапами он закрыт целиком, а не
        // только для утверждения.
        await using var s = await AuthorScopeAsync();
        var created = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, "Два этапа"));

        foreach (var status in new[]
                 {
                     HKCardStatus.OnReview, HKCardStatus.RevisionRequired, HKCardStatus.Approved
                 })
        {
            var (ok, error) = await s.HK.ChangeStatusAsync(created.Id, status, null, default);

            Assert.False(ok);
            Assert.NotNull(error);
        // Отказ контролируемый и предметный. Формулировка зависит от того,
        // какое условие сработало раньше: карта может быть ещё на рассмотрении
        // этапа, а утверждение закрыто в любом случае.
        Assert.True(
            error!.Contains("командами этапов согласования", StringComparison.OrdinalIgnoreCase)
            || error!.Contains("назначенным согласующим", StringComparison.OrdinalIgnoreCase),
            "Неожиданный отказ общего переключателя статуса: " + error);

        }

        var stored = await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == created.Id);
        Assert.Equal(HKCardStatus.Draft, stored.Status);
    }

    // ── Прежний сценарий не сломан ──

    [Fact]
    public async Task CreateAsync_LegacyScenario_StillUsesObjectCode()
    {
        await using var s = await AuthorScopeAsync();

        var node = new Node { Id = Guid.NewGuid(), Code = "LEG-" + Suffix(), Name = "Узел " + Suffix() };
        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "",
            IsAutonomous = false,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = node.Id,
            BranchId = _fixture.BranchA,
            Purpose = "Прежний сценарий",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await s.HK.CreateAsync(card);

        var year = s.Clock.GetUtcNow().UtcDateTime.Year;
        Assert.Equal($"ХК-{node.Code}-{year}", created.Code);
        Assert.False(created.IsAutonomous);
    }

    // ── Чтение контрактов ──

    [Fact]
    public async Task Registry_ReturnsNameSeparatelyFromCode()
    {
        await using var s = await AuthorScopeAsync();
        var created = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, "Видно в реестре"));

        var page = await s.HK.GetRegistryPageAsync(new HKCardRegistryQuery
        {
            SearchText = "Видно в реестре",
            Page = 1,
            PageSize = 50
        });

        var item = page.Items.Single(i => i.Id == created.Id);
        Assert.Equal("Видно в реестре", item.Name);
        Assert.Equal(created.Code, item.Code);
        Assert.True(item.IsAutonomous);
        Assert.Null(item.ObjectName);
        Assert.Null(item.ObjectCode);
    }

    [Fact]
    public async Task Search_FindsCardByName()
    {
        await using var s = await AuthorScopeAsync();
        var unique = "ПоискУникальный" + Suffix();
        var created = await s.HK.CreateAsync(NewAutonomousDraft(_fixture.BranchA, unique));

        var results = await s.Search.SearchAsync(unique, 20);

        Assert.Contains(results, r => r.EntityType == "HKCard" && r.EntityId == created.Id);
    }

    // ── Вспомогательное ──

    /// <summary>
    /// Читает определение индекса напрямую из каталога PostgreSQL. Через EF
    /// это неудобно: единственный текстовый столбец не имеет имени, которое EF
    /// сопоставляет с типом.
    /// </summary>
    private static async Task<string?> ReadIndexDefinitionAsync(TestScope s, string indexName)
    {
        var connection = s.Db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'HKCards' AND indexname = @name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "name";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);

        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<uint> CurrentRowVersionAsync(TestScope s, Guid cardId)
    {
        s.Db.ChangeTracker.Clear();
        return (await s.Db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId)).RowVersion;
    }

    /// <summary>
    /// Автономная карта с собственным деревом, конечной строкой и материалом.
    /// Создаётся напрямую, потому что проверяет сохранность того, что создаёт
    /// редактор дерева, а не сам сценарий этого PR.
    /// </summary>
    private async Task<Guid> CreateAutonomousCardWithTreeAsync()
    {
        await using var s = await AuthorScopeAsync();

        var card = NewAutonomousDraft(_fixture.BranchA, "Карта с деревом");
        card.Code = "TREE-" + Suffix();
        card.Version = "v0101";
        card.AuthorId = s.User.CurrentUserId!.Value;
        s.Db.HKCards.Add(card);

        var root = new HKCardNode
        {
            Id = Guid.NewGuid(), HKCardId = card.Id, ParentId = null,
            Level = HKObjectLevel.EquipmentModel, Name = "Изделие", SortOrder = 0
        };
        var branch = new HKCardNode
        {
            Id = Guid.NewGuid(), HKCardId = card.Id, ParentId = root.Id,
            Level = HKObjectLevel.Node, Name = "Узел", SortOrder = 0
        };
        s.Db.HKCardNodes.AddRange(root, branch);

        var au = new AssemblyUnit { Id = Guid.NewGuid(), Code = "AU-" + Suffix(), Name = "СЕ " + Suffix() };
        s.Db.AssemblyUnits.Add(au);
        await s.Db.SaveChangesAsync();

        var material = new GsmMaterial { Id = Guid.NewGuid(), Name = "М " + Suffix(), IsDraft = false };
        s.Db.GsmMaterials.Add(material);
        await s.Db.SaveChangesAsync();

        var item = new HKCardItem
        {
            Id = Guid.NewGuid(), HKCardId = card.Id, HKCardNodeId = branch.Id,
            AssemblyUnitId = au.Id, Quantity = 1, Volume = 1m, SortOrder = 0
        };
        item.Materials.Add(new HKCardItemMaterial
        {
            Id = Guid.NewGuid(), HKCardItemId = item.Id,
            GsmMaterialId = material.Id, Category = GsmCategory.Primary
        });
        s.Db.HKCardItems.Add(item);
        await s.Db.SaveChangesAsync();

        return card.Id;
    }
}