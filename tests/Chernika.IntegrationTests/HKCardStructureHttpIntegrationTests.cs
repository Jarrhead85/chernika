using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// HTTP-контракт атомарного сохранения структуры и итогов принятой редакции.
/// <para>
/// Запрос проходит реальный маршрут: политику <c>EditHK</c>, привязку модели,
/// сериализацию и middleware. Сервисные тесты того же сценария не доказывают,
/// что endpoint вообще торчит наружу и возвращает правильные коды отказов:
/// 409 — конфликт версии, 400 — рассогласование адреса и команды, 404 —
/// отсутствие карты, 204 — отсутствие принятой редакции.
/// </para>
/// <para>
/// <b>Enum в JSON передаются числами.</b> Приложение настраивает
/// System.Text.Json без конвертера строк, и числовой формат — то, во что
/// клиент .NET превращает enum по умолчанию; черновой контракт читается
/// по номерам надёжнее, чем по именам, чью поддержку здесь никто не обещает.
/// </para>
/// </summary>
[Collection("Database")]
public class HKCardStructureHttpIntegrationTests : IClassFixture<ChernikaApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ChernikaApiFactory _factory;
    private readonly List<Guid> _created = new();
    private readonly List<Guid> _units = new();
    private readonly List<Guid> _gsm = new();

    public HKCardStructureHttpIntegrationTests(ChernikaApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Уборка выполняется и при падении теста. Карта удаляется первой:
    /// разделы, позиции, строки и её материалы уходят каскадами, после чего
    /// снимаются созданные тестом справочные записи.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_created.Count == 0 && _units.Count == 0 && _gsm.Count == 0) return;

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.HKCards.Where(c => _created.Contains(c.Id)).ExecuteDeleteAsync();
        await db.GsmMaterialClassifications
            .Where(k => _gsm.Contains(k.GsmMaterialId))
            .ExecuteDeleteAsync();
        await db.GsmMaterials.Where(m => _gsm.Contains(m.Id)).ExecuteDeleteAsync();
        await db.AssemblyUnits.Where(u => _units.Contains(u.Id)).ExecuteDeleteAsync();
    }

    // ── 1. Атомарное сохранение структуры по HTTP ────────────────────────────

    [Fact]
    public async Task SaveStructure_OverHttp_SavesGraphAndLevel_AndConflictsOnStaleVersion()
    {
        var admin = _factory.CreateAs(_factory.SystemAdminUserName);
        var cardId = await CreateDraftAsync(admin);
        var version0 = await ReadRowVersionAsync(cardId);

        // Справочные записи: марка и сборочная единица — только вставкой в БД,
        // иначе тест зависел бы от внешних контрактов справочников.
        var unitId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var materialRowId = Guid.NewGuid();
        var gsmId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AssemblyUnits.Add(new AssemblyUnit
            {
                Id = unitId,
                Code = "АУ-" + Guid.NewGuid().ToString("N")[..6],
                Name = "СЕ структуры HTTP",
            });
            db.GsmMaterials.Add(new GsmMaterial
            {
                Id = gsmId,
                Name = "М-" + Guid.NewGuid().ToString("N")[..6],
                Nd = "ТУ " + Guid.NewGuid().ToString("N")[..6],
            });
            db.GsmMaterialClassifications.Add(new GsmMaterialClassification
            {
                Id = Guid.NewGuid(),
                GsmMaterialId = gsmId,
                GroupName = "Группа " + Guid.NewGuid().ToString("N")[..6],
                SubgroupName = "Подгруппа " + Guid.NewGuid().ToString("N")[..6],
            });
            await db.SaveChangesAsync();
        }
        _units.Add(unitId);
        _gsm.Add(gsmId);

        // Атомарная команда: раздел «Масла», под ним агрегат, у агрегата
        // конечная строка с основной маркой. Раздел ставится ТОЛЬКО у верхней
        // позиции: вложенный контент наследует раздел корня своей ветки.
        var save = await admin.PutAsJsonAsync($"/api/hkcards/{cardId}/structure", new
        {
            cardId,
            sections = new[]
            {
                new { id = sectionId, sectionType = (int)HKCardSectionType.Oils, sortOrder = 0 },
            },
            positions = new[]
            {
                new
                {
                    id = aggregateId,
                    parentId = (Guid?)null,
                    level = (int)HKObjectLevel.Aggregate,
                    name = "Агрегат HTTP",
                    quantity = 2,
                    sortOrder = 0,
                    sectionId = sectionId,
                },
            },
            items = new[]
            {
                new
                {
                    id = itemId,
                    nodeId = (Guid?)aggregateId,
                    assemblyUnitId = unitId,
                    quantity = 1,
                    volume = 500m,
                    unitOfMeasure = "г",
                    periodicity = (string?)null,
                    notes = (string?)null,
                    sortOrder = 0,
                    sectionId = (Guid?)null,
                    materials = new[]
                    {
                        new { id = materialRowId, gsmMaterialId = gsmId, category = (int)GsmCategory.Primary },
                    },
                },
            },
            nodeRootChoice = (string?)null,
            expectedRowVersion = version0,
        });
        Assert.True(save.StatusCode == HttpStatusCode.NoContent,
            "сохранение структуры вернуло " + (int)save.StatusCode
            + ". Тело: " + await save.Content.ReadAsStringAsync());

        // По БД: граф сохранён целиком, уровень выведен из структуры.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var card = await db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId);
            Assert.Equal(HKObjectLevel.EquipmentModel, card.ObjectLevel);

            Assert.Equal(1, await db.HKCardSections.CountAsync(s => s.HKCardId == cardId));
            Assert.Equal(1, await db.HKCardNodes.CountAsync(n => n.HKCardId == cardId));
            Assert.Equal(1, await db.HKCardItems.CountAsync(i => i.HKCardId == cardId));

            var node = await db.HKCardNodes.AsNoTracking().FirstAsync(n => n.HKCardId == cardId);
            Assert.Equal(sectionId, node.SectionId);

            Assert.Equal(1, await db.HKCardItemMaterials.CountAsync(m => m.HKCardItemId == itemId));
        }

        // Версия карты продвинулась: повтор с той же ожидаемой версией — 409.
        var version1 = await ReadRowVersionAsync(cardId);
        Assert.NotEqual(version0, version1);

        var save2 = await admin.PutAsJsonAsync($"/api/hkcards/{cardId}/structure", new
        {
            cardId,
            sections = Array.Empty<object>(),
            positions = Array.Empty<object>(),
            items = Array.Empty<object>(),
            nodeRootChoice = (string?)null,
            expectedRowVersion = version0,
        });
        Assert.Equal(HttpStatusCode.Conflict, save2.StatusCode);
    }

    [Fact]
    public async Task SaveStructure_OverHttp_MismatchedCardId_IsBadRequest()
    {
        var admin = _factory.CreateAs(_factory.SystemAdminUserName);
        var cardId = await CreateDraftAsync(admin);

        // Адрес ведёт на карту, а команда переносит содержимое чужого
        // идентификатора: такая сборка могла бы задеть граф другой карты.
        // Слой HTTP обязан её отклонить до обращения к сервису.
        var save = await admin.PutAsJsonAsync($"/api/hkcards/{cardId}/structure", new
        {
            cardId = Guid.NewGuid(),
            sections = Array.Empty<object>(),
            positions = Array.Empty<object>(),
            items = Array.Empty<object>(),
            nodeRootChoice = (string?)null,
            expectedRowVersion = 0,
        });
        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
    }

    [Fact]
    public async Task SaveStructure_OverHttp_MissingCard_IsNotFound()
    {
        var admin = _factory.CreateAs(_factory.SystemAdminUserName);
        var absentId = Guid.NewGuid();

        var save = await admin.PutAsJsonAsync($"/api/hkcards/{absentId}/structure", new
        {
            cardId = absentId,
            sections = Array.Empty<object>(),
            positions = Array.Empty<object>(),
            items = Array.Empty<object>(),
            nodeRootChoice = (string?)null,
            expectedRowVersion = 0,
        });
        Assert.Equal(HttpStatusCode.NotFound, save.StatusCode);
    }

    // ── 2. Итоги принятой редакции по HTTP ──────────────────────────────────

    [Fact]
    public async Task AcceptedTotals_OverHttp_NoAcceptedRevision_IsNoContent_MissingCard404()
    {
        var admin = _factory.CreateAs(_factory.SystemAdminUserName);
        var cardId = await CreateDraftAsync(admin);

        // Черновик ещё ни разу не согласовывался: 204 — не «пустой объект»,
        // а честное отсутствие принятой редакции. Подстановка расчёта по
        // рабочему дереву означала бы, что черновик выдан за принятую карту.
        var totals = await admin.GetAsync($"/api/hkcards/{cardId}/accepted-totals");
        Assert.Equal(HttpStatusCode.NoContent, totals.StatusCode);

        var absent = await admin.GetAsync($"/api/hkcards/{Guid.NewGuid()}/accepted-totals");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
    }

    // ── Помощники ────────────────────────────────────────────────────────────

    private async Task<Guid> CreateDraftAsync(HttpClient admin)
    {
        var create = await admin.PostAsJsonAsync("/api/hkcards", new
        {
            branchId = Guid.Parse(_factory.BranchId),
            // Создание карты старого сценария закрыто: API принимает только
            // автономную карту, у которой уровень и справочный объект
            // не задаются, а название обязательно.
            name = "Черновик HTTP " + Guid.NewGuid().ToString("N")[..6],
            purpose = "Проверка HTTP",
            normativeBasis = (string?)null,
            notes = (string?)null,
            requestOrganization = (string?)null,
            requestSenderFullName = (string?)null,
            requestReceivedDate = (DateTime?)null,
            requestDetails = (string?)null,
            incomingLetterNumber = (string?)null,
            outgoingLetterNumber = (string?)null,
            effectiveDate = (DateTime?)null,
            expirationDate = (DateTime?)null,
            status = "Draft",
            items = Array.Empty<object>(),
        });
        Assert.True(create.StatusCode == HttpStatusCode.Created,
            "создание черновика ХК вернуло " + (int)create.StatusCode
            + ". Тело: " + await create.Content.ReadAsStringAsync());
        var dto = await create.Content.ReadFromJsonAsync<JsonElement>(Json);
        var cardId = dto.GetProperty("id").GetGuid();
        _created.Add(cardId);
        return cardId;
    }

    private async Task<uint> ReadRowVersionAsync(Guid cardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId)
            .Select(c => c.RowVersion)
            .FirstAsync();
    }
}
