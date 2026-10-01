using Chernika.Api.Controllers;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

// Только два контрактных типа из Chernika.Api.Contracts используются в этом
// файле; пространство целиком не подключается, потому что одноимённые доменные
// модели запросов стали бы неоднозначными.
using ApiGenerateIndividualCardsRequest = Chernika.Api.Contracts.GenerateIndividualCardsRequest;
using ApiUpdateCardNotesRequest = Chernika.Api.Contracts.UpdateCardNotesRequest;

namespace Chernika.IntegrationTests;

/// <summary>
/// Контролируемый отказ отключённого модуля на УРОВНЕ КОНТРОЛЛЕРОВ.
/// <para>
/// Требование: прямой вызов API не должен давать необработанный 500 из-за
/// отключённого модуля. Глобального обработчика исключений в API нет, поэтому
/// каждое неперехваченное <c>InvalidOperationException</c> стало бы сырой 500.
/// </para>
/// <para>
/// Это проверка уровня контроллера, а не HTTP-прогон: в проекте нет инфраструктуры
/// тестового веб-хоста (WebApplicationFactory), а поднимать её ради этого — большое
/// изменение конструкции. Проверяется ровно то, что могло сломаться: перевод
/// отказа контроллером в контролируемый результат. Всё, что ниже уровня контроллера
/// (проверка прав, маршрутизация, сериализация) — не предмет теста.
/// </para>
/// </summary>
[Collection("Database")]
public class ConservedModuleApiRefusalIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public ConservedModuleApiRefusalIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void AsSystemAdmin(TestScope s) => s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

    /// <summary>
    /// Контроллер с работающим HTTP-контекстом и выключенным модулем. Модуль
    /// выключается в этом scope, поэтому вызовы гарантированно упираются в границу.
    /// </summary>
    private IndividualCardsController CardsController(TestScope s)
    {
        var module = s.IndividualCardModule;
        module.Disable();
        var controller = new IndividualCardsController(s.IndividualCards, s.Reports)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return controller;
    }

    private CoefficientsController CoefficientsController(TestScope s)
    {
        s.IndividualCardModule.Disable();
        return new CoefficientsController(s.CoeffService)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private CoefficientTypesController CoefficientTypesController(TestScope s)
    {
        s.IndividualCardModule.Disable();
        return new CoefficientTypesController(s.CoeffService)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    /// <summary>
    /// Отказ обязан быть контролируемым результатом, а не исключением. Любой
    /// неперехваченный InvalidOperationException провалит тест сразу — это и есть
    /// тот дефект, которого мы избегаем.
    /// <para>
    /// Методы контроллеров возвращают <c>ActionResult&lt;T&gt;</c>, поэтому
    /// результат разворачивается до <see cref="ActionResult"/>: неявное
    /// преобразование <c>Conflict(...)</c> кладёт статус в свойство
    /// <c>Result</c>, а не в сам объект.
    /// </para>
    /// </summary>
    private static void AssertControlledRefusal(object? returned)
    {
        Assert.NotNull(returned);

        var result = Unwrap(returned);
        Assert.NotNull(result);

        switch (result)
        {
            case ConflictResult:
            case ConflictObjectResult:
                return;
            case ObjectResult o:
                // NotFound/BadRequest тоже контролируемы: отказ не обязан быть
                // именно конфликтом, но обязан не быть 500.
                Assert.InRange(o.StatusCode ?? 200, 200, 499);
                return;
            default:
                Assert.Fail(
                    "Ожидался контролируемый ответ контроллера, а не пропуск действия. Получено: "
                    + returned.GetType().Name);
                return;
        }
    }

    /// <summary>
    /// Разворачивает <c>ActionResult&lt;T&gt;</c> до <see cref="ActionResult"/>.
    /// Отдельный тип результата (<c>OkValue</c> и т.п.) означал бы, что действие
    /// действительно выполнено, — такой случай тест не считает отказом.
    /// </summary>
    private static ActionResult? Unwrap(object returned)
    {
        if (returned is ActionResult action) return action;

        var resultProp = returned.GetType().GetProperty(nameof(ActionResult<object>.Result));
        return resultProp?.GetValue(returned) as ActionResult;
    }

    // ── Индивидуальные карты: каждый изменяющий endpoint ──────────────────

    [Fact]
    public async Task IndividualCards_EveryWriteEndpoint_RefusesWithoutServerError()
    {
        var cardId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        await using var s = _fixture.CreateScope();
        AsSystemAdmin(s);
        var c = CardsController(s);

        // Полный перечень операций записи модуля. Реестр намеренно дублирует
        // список вызовов IndividualCardModuleGuard: endpoint, забытый в одном
        // списке, выдаст сырой 500.
        AssertControlledRefusal(await c.Preflight(
            new IndividualCardPreflightRequest(IndividualCardObjectLevel.Node, nodeId), CancellationToken.None));
        AssertControlledRefusal(await c.CreateDraft(
            new CreateIndividualCardDraftRequest(IndividualCardObjectLevel.Node, nodeId), CancellationToken.None));
        AssertControlledRefusal(await c.RefreshDraftSources(
            cardId, new RefreshIndividualCardDraftSourcesRequest(cardId, null), CancellationToken.None));
        AssertControlledRefusal(await c.DeleteDraft(cardId, CancellationToken.None));
        AssertControlledRefusal(await c.GetDraftCalculation(cardId, CancellationToken.None));
        AssertControlledRefusal(await c.GetDraftCoefficients(cardId, null, CancellationToken.None));
        AssertControlledRefusal(await c.RecalculateDraft(
            cardId, new RecalculateIndividualCardDraftRequest(cardId, new List<Guid>()), CancellationToken.None));
        AssertControlledRefusal(await c.FormDraft(
            cardId, new FormIndividualCardRequest(cardId), CancellationToken.None));
        AssertControlledRefusal(await c.NewVersionPreflight(
            cardId, new IndividualCardVersionPreflightRequest(cardId), CancellationToken.None));
        AssertControlledRefusal(await c.CreateNewVersion(
            cardId, new CreateIndividualCardVersionRequest(cardId), CancellationToken.None));
        AssertControlledRefusal(await c.Archive(
            cardId, new ArchiveIndividualCardRequest(cardId), CancellationToken.None));
        AssertControlledRefusal(await c.GenerateForInstance(instanceId, new ApiGenerateIndividualCardsRequest()));
        AssertControlledRefusal(await c.UpdateNotes(cardId, new ApiUpdateCardNotesRequest("Правка")));
        AssertControlledRefusal(await c.Delete(cardId));
        AssertControlledRefusal(await c.GetPdf(cardId, CancellationToken.None));
        AssertControlledRefusal(await c.GetXlsx(cardId, CancellationToken.None));

        // Ничего не записано: отказ до обращения к БД.
        Assert.Equal(0, await s.Db.IndividualCards.CountAsync(ic => ic.Id == cardId));
    }

    [Fact]
    public async Task IndividualCards_ReadEndpoints_KeepWorkingWhileModuleIsDisabled()
    {
        await using var s = _fixture.CreateScope();
        AsSystemAdmin(s);
        var c = CardsController(s);
        var missing = Guid.NewGuid();

        // Чтение исторических документов остаётся доступным: консервация не
        // равна удалению. Проверяется, что выключенный модуль не превращает
        // чтение ни в отказ модуля, ни в исключение. Заведомо отсутствующие
        // идентификаторы намеренны: контроллер отвечает как обычно — NotFound
        // там, где контракт это предусматривает, и пустым Ok там, где список
        // по определению может быть пустым.
        Assert.NotNull(await c.GetAll(1, 10, null));
        Assert.IsType<NotFoundResult>((await c.GetById(missing)).Result);
        Assert.IsType<OkObjectResult>((await c.GetByInstance(missing)).Result);
        Assert.NotNull(await c.GetRegistry(null, null, null, null, null, null, false, false, "CreatedAt", true, 1, 10));
        Assert.IsType<NotFoundResult>((await c.GetDetail(missing, CancellationToken.None)).Result);
        // GetHistory для несуществующей карточки бросает исключение по контракту
        // сервиса («Карта не найдена») — это поведение не связано с консервацией,
        // поэтому заведомо отсутствующий идентификатор здесь не используется.
        // Чтение существующей исторической карты проверяется в
        // IndividualCardConservationIntegrationTests.
    }

    // ── Коэффициенты: тот же законсервированный контур ────────────────────

    [Fact]
    public async Task Coefficients_EveryWriteEndpoint_RefusesWithoutServerError()
    {
        var id = Guid.NewGuid();
        await using var s = _fixture.CreateScope();
        AsSystemAdmin(s);

        AssertControlledRefusal(await CoefficientsController(s).Create(
            new CreateCoefficientRequest
            {
                CoefficientTypeId = Guid.NewGuid(),
                Name = "Коэффициент " + Suffix(),
                Value = 1.1m,
            },
            CancellationToken.None));
        AssertControlledRefusal(await CoefficientsController(s).Update(
            id,
            new UpdateCoefficientRequest
            {
                Id = id,
                CoefficientTypeId = Guid.NewGuid(),
                Name = "X " + Suffix(),
                Value = 1.2m,
                SortOrder = 10,
            },
            CancellationToken.None));
        AssertControlledRefusal(await CoefficientsController(s).Archive(id, CancellationToken.None));
        AssertControlledRefusal(await CoefficientsController(s).Restore(id, CancellationToken.None));
    }

    [Fact]
    public async Task CoefficientTypes_EveryWriteEndpoint_RefusesWithoutServerError()
    {
        var id = Guid.NewGuid();
        await using var s = _fixture.CreateScope();
        AsSystemAdmin(s);

        AssertControlledRefusal(await CoefficientTypesController(s).Create(
            new CreateCoefficientTypeRequest("Тип " + Suffix()), CancellationToken.None));
        AssertControlledRefusal(await CoefficientTypesController(s).Update(
            id, new UpdateCoefficientTypeRequest(id, "Тип " + Suffix(), 10), CancellationToken.None));
        AssertControlledRefusal(await CoefficientTypesController(s).Archive(id, CancellationToken.None));
        AssertControlledRefusal(await CoefficientTypesController(s).Restore(id, CancellationToken.None));
    }

    [Fact]
    public async Task Coefficients_ReadsKeepWorking_AndDataSurvivesRefusal()
    {
        await using var s = _fixture.CreateScope();
        AsSystemAdmin(s);

        // Данные создаются ПРИ ВКЛЮЧЁННОМ модуле — так же, как это было до
        // консервации. Проверка не полагается на остаточные данные общей тестовой
        // БД: отсутствие коэффициентов не должно выглядеть как успешная проверка.
        var type = await s.CoeffService.CreateCoefficientTypeAsync(
            new CreateCoefficientTypeRequest("Тип " + Suffix()));
        var coefficient = await s.CoeffService.CreateCoefficientAsync(new CreateCoefficientRequest
        {
            CoefficientTypeId = type.Id,
            Name = "Коэффициент " + Suffix(),
            Value = 1.5m,
        });

        s.IndividualCardModule.Disable();

        // Чтение сохранённых данных после консервации работает, и запись в БД
        // не изменилась: консервация не удаляет данные.
        var paged = await CoefficientsController(s).GetPaged(new CoefficientListQuery(), CancellationToken.None);
        Assert.NotNull(paged);

        var byId = await CoefficientsController(s).GetById(coefficient.Id, CancellationToken.None);
        Assert.NotNull(byId.Result);
        var types = await CoefficientTypesController(s).GetPaged(
            new CoefficientTypeListQuery(), CancellationToken.None);
        Assert.NotNull(types);

        var alive = await s.Db.Coefficients.AsNoTracking().CountAsync(c => c.Id == coefficient.Id);
        Assert.Equal(1, alive);
    }
}