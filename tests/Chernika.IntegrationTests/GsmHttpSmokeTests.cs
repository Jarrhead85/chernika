using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chernika.Domain.Entities;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Воспроизводимый HTTP smoke по НАСТОЯЩЕМУ приложению.
/// <para>
/// Существующие тесты проверяют сервисы и контроллеры по отдельности и потому
/// не доказывают, что запрос проходит реальные маршруты, привязку моделей,
/// политики авторизации и middleware. Этот набор закрывает тот зазор.
/// </para>
/// <para>
/// <b>Что проверяется осознанно.</b> HTTP 200 сам по себе ничего не значит:
/// HTTP 200 на неизвестном маршруте также корректен. Поэтому утверждения
/// построены на маршруте, Content-Type и СОДЕРЖИМОМ ответа, а не на коде.
/// </para>
/// <para>
/// База — отдельная <c>chernika_http_test</c>. Write-тесты физически не могут
/// задеть рабочую <c>chernika</c>: фабрика отказывается стартовать на другой
/// базе.
/// </para>
/// </summary>
/// <para>
/// Хост поднимается ОДИН на весь класс: xUnit создаёт экземпляр класса на каждый
/// тест, и фабрика в конструкторе поднимала бы 11 приложений заново, попутно
/// падая на повторной вставке филиала. Общий fixture и пересоздание базы при
/// инициализации решают оба случая.
/// </para>
[Collection("Database")]
public class GsmHttpSmokeTests : IClassFixture<ChernikaApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Контрактные записи теста повторяют публичные DTO приложения. Анонимные
    // объекты здесь не годятся: вывод типов для null в анонимном типе даёт
    // неоднозначность, а читаемость проверяемого контракта падает.
    private sealed record MaterialWrite(
        string Name, string? Nd, bool InGostNomenclature, string? IntendedUse,
        bool SuitabilityGround, bool SuitabilityAir, bool SuitabilitySea,
        string? NatoIndex, string? Note, string? GroupName, IReadOnlyList<string>? SubgroupNames);

    private sealed record RelationWrite(
        Guid PrimaryGsmMaterialId, Guid RelatedGsmMaterialId, string RelationType, string? Note);

    private sealed record MaterialRefWrite(Guid GsmMaterialId, Domain.Enums.GsmCategory Category);

    private sealed record ItemWrite(
        Guid? AssemblyUnitId, int SortOrder, decimal Quantity, decimal Volume,
        string UnitOfMeasure, string? Periodicity, string? Notes,
        IReadOnlyList<MaterialRefWrite> Materials);

    private sealed record HkCardWrite(
        Guid BranchId, string ObjectLevel,
        Guid? ComplexId, Guid? EquipmentModelId, Guid? AggregateId, Guid? NodeId,
        string? Purpose, string? NormativeBasis, string? Notes,
        string? RequestOrganization, string? RequestSenderFullName,
        DateTime? RequestReceivedDate, string? RequestDetails,
        string? IncomingLetterNumber, string? OutgoingLetterNumber,
        DateTime? EffectiveDate, DateTime? ExpirationDate,
        string? Status, IReadOnlyList<ItemWrite>? Items);

    private sealed record HkCardTextUpdate(
        string ObjectLevel,
        Guid? ComplexId, Guid? EquipmentModelId, Guid? AggregateId, Guid? NodeId,
        string? Purpose, string? NormativeBasis, string? Notes,
        string? RequestOrganization, string? RequestSenderFullName,
        DateTime? RequestReceivedDate, string? RequestDetails,
        string? IncomingLetterNumber, string? OutgoingLetterNumber,
        DateTime? EffectiveDate, DateTime? ExpirationDate, uint RowVersion);

    private sealed record IcObjectRef(Domain.Enums.IndividualCardObjectLevel ObjectLevel, Guid ObjectId);

    private readonly ChernikaApiFactory _factory;
    private readonly List<Guid> _created = new();

    public GsmHttpSmokeTests(ChernikaApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Уборка выполняется и при падении теста. База у HTTP-тестов своя, но
    /// оставлять за собой строки всё равно нельзя: иначе повторный прогон внутри
    /// одного класса увидит мусор предыдущего теста.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_created.Count == 0) return;

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await GsmTestDataCleaner.CleanupAsync(db, _created);
    }

    // ── 1. Марка ГСМ: чтение, создание, изменение ─────────────────────────

    [Fact]
    public async Task Material_OverHttp_CanBeCreatedReadAndUpdated_WithoutLegacyFields()
    {
        var admin = _factory.CreateAs(_factory.NormAdminUserName);
        var nd = "ГОСТ 21743-76 " + Guid.NewGuid().ToString("N")[..6];

        // Создание.
        var created = await admin.PostAsJsonAsync("/api/gsmmaterials",
            new MaterialWrite(
                "Марка HTTP " + Guid.NewGuid().ToString("N")[..6], nd, true,
                "Для дизельных двигателей", false, false, false,
                null, "Проверка HTTP", "Моторные масла", new[] { "Азотные", "Ясные" }));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        AssertJson(created);

        var dto = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = dto.GetProperty("id").GetGuid();
        _created.Add(id);

        // Ответ не содержит удалённых live-полей. Проверяется по всему телу,
        // а не по одному свойству: вложенный объект тоже считается утечкой.
        var body = await created.Content.ReadAsStringAsync();
        foreach (var legacy in new[] { "\"Type\"", "\"Gost\"", "\"Description\"", "\"type\"", "\"gost\"", "\"description\"" })
        {
            Assert.DoesNotContain(legacy, body, StringComparison.Ordinal);
        }

        // Классификация доехала: несколько подгрупп — норма для новой модели.
        Assert.Equal("Моторные масла", dto.GetProperty("groupName").GetString());
        Assert.Equal(2, dto.GetProperty("subgroupNames").GetArrayLength());
        Assert.Equal(nd, dto.GetProperty("nd").GetString());

        // Чтение по маршруту списка.
        var list = await admin.GetAsync("/api/gsmmaterials");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        AssertJson(list);
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.Contains(id.ToString(), listBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Gost\"", listBody, StringComparison.Ordinal);

        // Чтение одной марки.
        var one = await admin.GetAsync($"/api/gsmmaterials/{id}");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        var oneDto = await one.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(id, oneDto.GetProperty("id").GetGuid());
        Assert.Equal(nd, oneDto.GetProperty("nd").GetString());

        // Изменение: Nd и классификация меняются, прежних полей в ответе нет.
        var nd2 = "ГОСТ 12345-99 " + Guid.NewGuid().ToString("N")[..6];
        var updated = await admin.PutAsJsonAsync($"/api/gsmmaterials/{id}",
            new MaterialWrite(
                oneDto.GetProperty("name").GetString()!, nd2,
                oneDto.GetProperty("inGostNomenclature").GetBoolean(),
                oneDto.GetProperty("intendedUse").GetString(), false, false, false,
                null, "Проверка HTTP (правка)", "Моторные масла", new[] { "Азотные" }));
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);

        var reread = await admin.GetAsync($"/api/gsmmaterials/{id}");
        var after = await reread.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(nd2, after.GetProperty("nd").GetString());
        Assert.Equal(1, after.GetProperty("subgroupNames").GetArrayLength());
    }

    [Fact]
    public async Task Material_OverHttp_UnknownId_Returns404_NotFallback200()
    {
        var admin = _factory.CreateAs(_factory.NormAdminUserName);

        var response = await admin.GetAsync($"/api/gsmmaterials/{Guid.NewGuid()}");

        // Существующий маршрут с отсутствующим ресурсом обязан дать 404.
        // Ответ 200 здесь означал бы, что маршрут не найден и отдан fallback.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── 2. Направленные связи ГСМ ────────────────────────────────────────

    [Fact]
    public async Task Relation_OverHttp_CanBeCreatedReadAndUpdated()
    {
        var admin = _factory.CreateAs(_factory.NormAdminUserName);
        var primary = await CreateMaterialAsync(admin, "Группа связи", "Альфа");
        var related = await CreateMaterialAsync(admin, "Группа связи", "Бета");
        _created.Add(primary);
        _created.Add(related);

        var created = await admin.PostAsJsonAsync("/api/gsmmaterialrelations",
            new RelationWrite(primary, related, "Duplicate", "Проверка HTTP"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        AssertJson(created);
        var dto = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        var relationId = dto.GetProperty("id").GetGuid();

        var read = await admin.GetAsync($"/api/gsmmaterialrelations/{relationId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(primary, dto.GetProperty("primaryGsmMaterialId").GetGuid());
        Assert.Equal(related, dto.GetProperty("relatedGsmMaterialId").GetGuid());

        var updated = await admin.PutAsJsonAsync($"/api/gsmmaterialrelations/{relationId}",
            new RelationWrite(primary, related, "Reserve", "Проверка HTTP (правка)"));
        Assert.True(
            updated.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK,
            "изменение связи вернуло " + updated.StatusCode);
    }

    [Fact]
    public async Task Relation_OverHttp_DuplicatePair_SelfReference_AndWrongForeign_AreRefused()
    {
        var admin = _factory.CreateAs(_factory.NormAdminUserName);
        var primary = await CreateMaterialAsync(admin, "Группа отказа", "Альфа");
        var related = await CreateMaterialAsync(admin, "Группа отказа", "Бета");
        var third = await CreateMaterialAsync(admin, "Группа отказа", "Гамма");
        _created.Add(primary);
        _created.Add(related);
        _created.Add(third);

        var first = await admin.PostAsJsonAsync("/api/gsmmaterialrelations",
            new RelationWrite(primary, related, "Duplicate", null));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // 1. Повтор активной пары. Необработанная ошибка привела бы к 500 —
        //    здесь нужен контролируемый отказ.
        var duplicate = await admin.PostAsJsonAsync("/api/gsmmaterialrelations",
            new RelationWrite(primary, related, "Reserve", null));
        await AssertControlledAsync(duplicate, "повтор активной пары");

        // 2. Самоссылка.
        var selfRef = await admin.PostAsJsonAsync("/api/gsmmaterialrelations",
            new RelationWrite(primary, primary, "Duplicate", null));
        await AssertControlledAsync(selfRef, "самоссылка");

        // 3. Связь Foreign требует InGostNomenclature = false у связанной марки.
        var wrongForeign = await admin.PostAsJsonAsync("/api/gsmmaterialrelations",
            new RelationWrite(primary, third, "Foreign", null));
        await AssertControlledAsync(wrongForeign, "Foreign при InGostNomenclature = true");

        // Связи не создалось: пара осталась ровно одна.
        var list = await admin.GetAsync($"/api/gsmmaterialrelations?primaryGsmMaterialId={primary}");
        var listDto = await list.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(1, listDto.GetProperty("totalCount").GetInt32());
    }

    // ── 3. ХК: чтение и текстовая правка ──────────────────────────────────

    [Fact]
    public async Task Hk_OverHttp_TextEdit_KeepsExistingMaterialRows()
    {
        var admin = _factory.CreateAs(_factory.NormAdminUserName);
        var material = await CreateMaterialAsync(admin, "Группа ХК", "Азотные");
        _created.Add(material);

        var (cardId, nodeId) = await CreateDraftCardAsync(admin);
        _created.Add(nodeId);
        _created.Add(cardId);

        // Строка материала и сам пункт создаются напрямую: в контракте API НЕТ
        // endpoint'а для назначения материалов (HKCardItemService в Chernika.Api
        // не зарегистрирован, Create/Update принимают только поля карты). Без
        // существующей строки сценарий «текстовая правка не трогает материалы»
        // невозможен, поэтому база наполняется так же, как это делает Web-клиент.
        var rowId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.HKCardItems.Add(new HKCardItem
            {
                Id = itemId,
                HKCardId = cardId,
                AssemblyUnitId = await EnsureAssemblyUnitAsync(db),
                SortOrder = 1,
                Quantity = 1,
                Volume = 10,
                UnitOfMeasure = "кг",
            });
            db.HKCardItemMaterials.Add(new HKCardItemMaterial
            {
                Id = rowId,
                HKCardItemId = itemId,
                GsmMaterialId = material,
                Category = Domain.Enums.GsmCategory.Primary,
            });
            await db.SaveChangesAsync();
        }

        var before = await admin.GetAsync($"/api/hkcards/{cardId}");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        AssertJson(before);
        var beforeDto = await before.Content.ReadFromJsonAsync<JsonElement>(Json);

        // Материал лежит в группе своей категории: контракт отдаёт материалы
        // по категориям, а не плоским списком. Идентификатора СТРОКИ
        // (HKCardItemMaterial.Id) в публичном DTO нет — его сохранность
        // проверяется в БД, а распределение по категориям — по HTTP.
        var beforePrimary = ReadCategory(beforeDto, "primaryMaterials");
        Assert.Equal(1, beforePrimary.GetArrayLength());
        Assert.Equal(material, beforePrimary[0].GetProperty("id").GetGuid());

        var rowVersion = beforeDto.GetProperty("rowVersion").GetUInt32();

        // Текстовая правка карты.
        var updated = await admin.PutAsJsonAsync($"/api/hkcards/{cardId}", new
        {
            objectLevel = Domain.Enums.HKObjectLevel.Node,
            complexId = (Guid?)null,
            equipmentModelId = (Guid?)null,
            aggregateId = (Guid?)null,
            nodeId,
            purpose = "Правка назначения по HTTP",
            normativeBasis = (string?)null,
            notes = "Проверка HTTP: текстовая правка",
            requestOrganization = (string?)null,
            requestSenderFullName = (string?)null,
            requestReceivedDate = (DateTime?)null,
            requestDetails = (string?)null,
            incomingLetterNumber = (string?)null,
            outgoingLetterNumber = (string?)null,
            effectiveDate = (DateTime?)null,
            expirationDate = (DateTime?)null,
            rowVersion,
        });
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);

        // По HTTP: та же марка в той же категории, правка применилась.
        var after = await admin.GetAsync($"/api/hkcards/{cardId}");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        var afterDto = await after.Content.ReadFromJsonAsync<JsonElement>(Json);

        var afterPrimary = ReadCategory(afterDto, "primaryMaterials");
        Assert.Equal(1, afterPrimary.GetArrayLength());
        Assert.Equal(material, afterPrimary[0].GetProperty("id").GetGuid());
        foreach (var other in new[] { "duplicateMaterials", "reserveMaterials", "foreignMaterials" })
            Assert.Equal(0, ReadCategory(afterDto, other).GetArrayLength());

        Assert.Equal("Проверка HTTP: текстовая правка", afterDto.GetProperty("notes").GetString());

        // По БД: идентификатор строки, марка и категория не изменились.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.HKCardItemMaterials.AsNoTracking()
                .SingleAsync(m => m.Id == rowId);

            Assert.Equal(material, row.GsmMaterialId);
            Assert.Equal(Domain.Enums.GsmCategory.Primary, row.Category);
            Assert.Equal(itemId, row.HKCardItemId);
            Assert.Equal(1, await db.HKCardItemMaterials.CountAsync(m => m.HKCardItemId == itemId));
        }
    }

    // ── 4. Марка с несколькими подгруппами доступна для назначения ───────

    [Fact]
    public async Task Material_WithTwoSubgroups_IsSelectable_AndRelationIsNotAutoApplied()
    {
        var admin = _factory.CreateAs(_factory.NormAdminUserName);
        var primary = await CreateMaterialAsync(admin, "Группа мульти", "Азотные", "Ясные");
        var related = await CreateMaterialAsync(admin, "Группа мульти", "Бета");
        _created.Add(primary);
        _created.Add(related);

        // Мультиподгруппная марка доступна для выбора. До PR-5 она была запрещена
        // в новых строках ХК, поэтому проверяется именно этот маршрут. Поиск
        // идёт по имени и НД марки, поэтому запрашивается по имени.
        var primaryName = await ReadMaterialNameAsync(admin, primary);
        var options = await admin.GetAsync(
            $"/api/gsmmaterialrelations/material-options?search={Uri.EscapeDataString(primaryName)}");
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        AssertJson(options);
        var optionsBody = await options.Content.ReadAsStringAsync();
        Assert.Contains(primary.ToString(), optionsBody, StringComparison.OrdinalIgnoreCase);

        // Связь создана ДО назначения основной марки.
        var relation = await admin.PostAsJsonAsync("/api/gsmmaterialrelations",
            new RelationWrite(primary, related, "Duplicate", null));
        Assert.Equal(HttpStatusCode.Created, relation.StatusCode);

        var (cardId, nodeId) = await CreateDraftCardAsync(admin);
        _created.Add(nodeId);
        _created.Add(cardId);

        // Назначение основной марки — тем же путём, что использует Web-клиент.
        // В API нет endpoint'а для назначения материалов (HKCardItemService там
        // не зарегистрирован), поэтому строка наполняется напрямую: проверяется
        // ЧТЕНИЕ результата по HTTP, а не несуществующий маршрут записи.
        var itemId = Guid.NewGuid();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.HKCardItems.Add(new HKCardItem
            {
                Id = itemId,
                HKCardId = cardId,
                AssemblyUnitId = await EnsureAssemblyUnitAsync(db),
                SortOrder = 1,
                Quantity = 1,
                Volume = 10,
                UnitOfMeasure = "кг",
            });
            db.HKCardItemMaterials.Add(new HKCardItemMaterial
            {
                Id = Guid.NewGuid(),
                HKCardItemId = itemId,
                GsmMaterialId = primary,
                Category = Domain.Enums.GsmCategory.Primary,
            });
            await db.SaveChangesAsync();
        }

        var read = await admin.GetAsync($"/api/hkcards/{cardId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        AssertJson(read);
        var cardDto = await read.Content.ReadFromJsonAsync<JsonElement>(Json);

        // Мультиподгруппная марка принята в строку без отказа.
        var primaries = cardDto.GetProperty("items")[0].GetProperty("primaryMaterials");
        Assert.Equal(1, primaries.GetArrayLength());
        Assert.Equal(primary, primaries[0].GetProperty("id").GetGuid());
        Assert.Equal(2, primaries[0].GetProperty("subgroupNames").GetArrayLength());

        // Связанная марка в строку не подставлена: только явный выбор пользователя.
        foreach (var category in new[] { "duplicateMaterials", "reserveMaterials", "foreignMaterials" })
        {
            var group = cardDto.GetProperty("items")[0].GetProperty(category);
            Assert.Equal(0, group.GetArrayLength());
            Assert.DoesNotContain(
                group.EnumerateArray(),
                m => m.GetProperty("id").GetGuid() == related);
        }
    }

    /// <summary>Имя марки по HTTP-маршруту: тесты не знают его заранее.</summary>
    private static async Task<string> ReadMaterialNameAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/gsmmaterials/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return dto.GetProperty("name").GetString()!;
    }

    /// <summary>Сборочная единица для пункта ХК. Создаётся один раз на прогон.</summary>
    private static async Task<Guid> EnsureAssemblyUnitAsync(AppDbContext db)
    {
        var existing = await db.AssemblyUnits.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Code == "AU-HTTP");
        if (existing is not null) return existing.Id;

        var id = Guid.NewGuid();
        db.AssemblyUnits.Add(new AssemblyUnit
        {
            Id = id,
            Code = "AU-HTTP",
            Name = "СЕ HTTP-тестов",
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ── 5. Выключенный модуль ИК: контролируемый отказ, чтение сохранено ──

    [Fact]
    public async Task IndividualCards_OverHttp_WhenModuleDisabled_RefuseWrites_WithoutWriting()
    {
        var admin = _factory.CreateAs(_factory.SystemAdminUserName);
        var nodeId = await CreateNodeDirect();
        _created.Add(nodeId);

        var (nodeCountBefore, cardCountBefore) = await CountIcAndNodeAsync();

        // Изменяющие endpoints. Проверяются по маршруту, а не «хоть что-нибудь
        //»: каждый должен дать контролируемый отказ, и ни один — 500.
        var attempts = new (string Name, Func<Task<HttpResponseMessage>> Call)[]
        {
            ("preflight", () => admin.PostAsJsonAsync("/api/individualcards/preflight",
                new { objectLevel = Domain.Enums.HKObjectLevel.Node, objectId = nodeId })),
            ("drafts", () => admin.PostAsJsonAsync("/api/individualcards/drafts",
                new { objectLevel = Domain.Enums.HKObjectLevel.Node, objectId = nodeId })),
            ("recalculate", () => admin.PostAsJsonAsync(
                $"/api/individualcards/drafts/{Guid.NewGuid()}/recalculate",
                new { individualCardId = Guid.NewGuid(), coefficientIds = Array.Empty<Guid>() })),
            ("archive", () => admin.PostAsJsonAsync(
                $"/api/individualcards/{Guid.NewGuid()}/archive",
                new { individualCardId = Guid.NewGuid(), reason = "проверка" })),
            ("generate", () => admin.PostAsJsonAsync(
                $"/api/individualcards/generate/{Guid.NewGuid()}", new { })),
        };

        foreach (var (name, call) in attempts)
        {
            var response = await call();
            Assert.True(
                response.StatusCode is HttpStatusCode.Conflict
                    or HttpStatusCode.BadRequest
                    or HttpStatusCode.Forbidden,
                name + ": неожиданный статус " + (int)response.StatusCode);
        }

        // Ничего не записано. Сравниваются те же величины, что сняты до
        // попыток: ранее здесь сравнивались счётчики РАЗНЫХ сущностей, и проверка
        // проходила вопреки записи.
        var (nodeCountAfter, cardCountAfter) = await CountIcAndNodeAsync();
        Assert.Equal(nodeCountBefore, nodeCountAfter);
        Assert.Equal(cardCountBefore, cardCountAfter);
    }

    [Fact]
    public async Task Coefficients_OverHttp_WhenModuleDisabled_RefuseWrites()
    {
        var admin = _factory.CreateAs(_factory.SystemAdminUserName);

        var type = await admin.PostAsJsonAsync("/api/coefficienttypes", new { name = "Тип HTTP" + Guid.NewGuid().ToString("N")[..6] });
        await AssertControlledAsync(type, "создание типа коэффициента");

        var coefficient = await admin.PostAsJsonAsync("/api/coefficients", new
        {
            coefficientTypeId = Guid.NewGuid(),
            name = "Коэффициент HTTP",
            conditionDescription = (string?)null,
            value = 1.5m,
            isActive = true,
            sortOrder = 10,
        });
        await AssertControlledAsync(coefficient, "создание коэффициента");
    }

    [Fact]
    public async Task IndividualCards_OverHttp_HistoricalRead_RemainsAvailable()
    {
        // Принятая граница: чтение сохранённых документов разрешено, запись
        // закрыта. Если бы чтение тоже отказывало, исторические данные стали бы
        // недоступны — а это не то, что было решено.
        var admin = _factory.CreateAs(_factory.SystemAdminUserName);

        var list = await admin.GetAsync("/api/individualcards");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        AssertJson(list);

        var missing = await admin.GetAsync($"/api/individualcards/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ── 6. Права: Reference.View без Reference.Edit ────────────────────────

    [Fact]
    public async Task ReadOnlyUser_OverHttp_CanRead_ButCannotWriteMaterialsOrRelations()
    {
        var reader = _factory.CreateAs(_factory.ReadOnlyUserName);

        // Право на чтение есть — и оно проверяется настоящим IPermissionService.
        var list = await reader.GetAsync("/api/gsmmaterials");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        AssertJson(list);

        var relations = await reader.GetAsync("/api/gsmmaterialrelations");
        Assert.Equal(HttpStatusCode.OK, relations.StatusCode);

        // Права на запись нет. Отказ должен прийти ИЗ ПОЛИТИКИ, а не из
        // сервиса: значит 403, а не 400 и не 500.
        var createMaterial = await reader.PostAsJsonAsync("/api/gsmmaterials", new
        {
            name = "Марка читателя",
            nd = "ГОСТ 1",
            inGostNomenclature = true,
            intendedUse = (string?)null,
            suitAbilityGround = false,
            suitAbilityAir = false,
            suitAbilitySea = false,
            natoIndex = (string?)null,
            note = (string?)null,
            groupName = "Группа",
            subgroupNames = new[] { "Подгруппа" },
        });
        Assert.Equal(HttpStatusCode.Forbidden, createMaterial.StatusCode);

        var admin = _factory.CreateAs(_factory.NormAdminUserName);
        var primary = await CreateMaterialAsync(admin, "Группа прав", "Альфа");
        var related = await CreateMaterialAsync(admin, "Группа прав", "Бета");
        _created.Add(primary);
        _created.Add(related);

        var createRelation = await reader.PostAsJsonAsync("/api/gsmmaterialrelations", new
        {
            primaryGsmMaterialId = primary,
            relatedGsmMaterialId = related,
            relationType = "Duplicate",
            note = (string?)null,
        });
        Assert.Equal(HttpStatusCode.Forbidden, createRelation.StatusCode);

        // Ни одна из отклонённых попыток не создала строк.
        var relationList = await admin.GetAsync($"/api/gsmmaterialrelations?primaryGsmMaterialId={primary}");
        var relationDto = await relationList.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(0, relationDto.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Anonymous_OverHttp_Gets401_NotFallback200()
    {
        // Аутентификация пропущена намеренно: защищённый маршрут обязан
        // ответить 401. Ответ 200 означал бы, что маршрут не найден и отдан
        // fallback-страницей.
        var anonymous = _factory.CreateAnonymous();

        var response = await anonymous.GetAsync("/api/gsmmaterials");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Помощники ─────────────────────────────────────────────────────────

    private static void AssertJson(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        Assert.True(
            mediaType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase),
            "ожидался JSON, получен Content-Type: '" + mediaType + "'");
    }

    /// <summary>
    /// Контролируемый отказ: JSON вместо text/plain и НЕ 500.
    /// <para>
    /// Тело ответа входит в сообщение об ошибке намеренно: «ожидался Conflict,
    /// получен InternalServerError» без причины бесполезны при разборе.
    /// </para>
    /// </summary>
    private static async Task AssertControlledAsync(HttpResponseMessage response, string what)
    {
        var body = await response.Content.ReadAsStringAsync();
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "(нет Content-Type)";

        Assert.True(
            mediaType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase),
            what + ": ожидался JSON, получен Content-Type '" + mediaType + "'. Тело: " + Truncate(body));

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest
                or HttpStatusCode.Conflict
                or HttpStatusCode.UnprocessableEntity
                or HttpStatusCode.Forbidden,
            what + ": неожиданный статус " + (int)response.StatusCode + ". Тело: " + Truncate(body));
    }

    private static string Truncate(string value) =>
        value.Length <= 400 ? value : value[..400] + "…";

    /// <summary>Материалы карты по категории: контракт группирует их по полям.</summary>
    private static JsonElement ReadCategory(JsonElement card, string property)
    {
        var items = card.GetProperty("items");
        Assert.True(items.GetArrayLength() >= 1, "у карты нет строк");
        return items[0].GetProperty(property);
    }

    private async Task<Guid> CreateMaterialAsync(
        HttpClient client, string group, string subgroup, params string[] extraSubgroups)
    {
        var response = await client.PostAsJsonAsync("/api/gsmmaterials", new
        {
            name = "Марка " + Guid.NewGuid().ToString("N")[..6],
            nd = "НД " + Guid.NewGuid().ToString("N")[..6],
            inGostNomenclature = true,
            intendedUse = (string?)null,
            suitAbilityGround = false,
            suitAbilityAir = false,
            suitAbilitySea = false,
            natoIndex = (string?)null,
            note = (string?)null,
            groupName = group,
            subgroupNames = new[] { subgroup }.Concat(extraSubgroups).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return dto.GetProperty("id").GetGuid();
    }

    private async Task<(Guid cardId, Guid nodeId)> CreateDraftCardAsync(HttpClient client)
    {
        var nodeId = await CreateNodeDirect();
        var code = "HK-H" + Guid.NewGuid().ToString("N")[..6];

        var response = await client.PostAsJsonAsync("/api/hkcards", new
        {
            branchId = Guid.Parse(_factory.BranchId),
            objectLevel = Domain.Enums.HKObjectLevel.Node,
            complexId = (Guid?)null,
            equipmentModelId = (Guid?)null,
            aggregateId = (Guid?)null,
            nodeId,
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
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            "создание черновика ХК вернуло " + (int)response.StatusCode
            + ". Тело: " + Truncate(await response.Content.ReadAsStringAsync()));

        var dto = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return (dto.GetProperty("id").GetGuid(), nodeId);
    }

    private async Task<Guid> CreateNodeDirect()
    {
        var id = Guid.NewGuid();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Nodes.Add(new Node
        {
            Id = id,
            Code = "N-H" + Guid.NewGuid().ToString("N")[..6],
            Name = "Узел HTTP",
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<(int Nodes, int Cards)> CountIcAndNodeAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (
            await db.Nodes.IgnoreQueryFilters().CountAsync(),
            await db.IndividualCards.IgnoreQueryFilters().CountAsync());
    }
}
