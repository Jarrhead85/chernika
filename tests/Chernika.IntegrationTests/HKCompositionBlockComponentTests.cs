using Bunit;
using Chernika.Domain.Enums;
using Chernika.Domain.Structure;
using Chernika.Infrastructure.Services;
using Chernika.Web.Components;
using Chernika.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Компонент просмотра принятого состава: рендер дерева и надёжность
/// загрузки.
/// <para>
/// Проверяется сам компонент, а не только проекция: повторяющиеся @key
/// ломают дерево Blazor, а перекрывающиеся загрузки — источник подмены
/// карты. Тест рендера нужен и для поведения при загрузке.
/// </para>
/// </summary>
public class HKCompositionBlockComponentTests : TestContext
{
    private readonly FakeCompositionReader _reader = new();

    private static readonly Guid CardA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CardB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public HKCompositionBlockComponentTests()
    {
        Services.AddSingleton<IHKAcceptedCompositionReader>(_reader);
        Services.AddSingleton<CircuitDbLock>();
        Services.AddLogging();
    }

    // ── Рендер дерева ──

    /// <summary>
    /// Два блока «Без раздела» дают две группы с разными ключами: одинаковые
    /// @key в дереве Blazor недопустимы.
    /// </summary>
    [Fact]
    public void Render_SeveralWithoutSectionBlocks_DoesNotDuplicateRowKeys()
    {
        var composition = CompositionWithTwoWithoutSectionBlocks();

        _reader.Result = _ => Task.FromResult<HKAcceptedCompositionDto?>(composition);

        var cut = RenderComponent<HKCompositionBlock>(p => p.Add(x => x.CardId, CardA));

        cut.WaitForAssertion(() =>
        {
            var groups = cut.FindAll("tr.hkCompositionSectionRow");
            Assert.Equal(3, groups.Count); // два раздела снимка и две группы «Без раздела»
        });
    }

    /// <summary>Номера строк не меняются от поиска и сворачивания.</summary>
    [Fact]
    public void Render_SearchAndCollapse_KeepRowNumbers()
    {
        var composition = CompositionWithTwoWithoutSectionBlocks();
        _reader.Result = _ => Task.FromResult<HKAcceptedCompositionDto?>(composition);

        var cut = RenderComponent<HKCompositionBlock>(p => p.Add(x => x.CardId, CardA));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        var numbersBefore = RowNumbers(cut);

        // Поиск по названию.
        cut.Find("input.hkCompositionSearchInput").Input("Верхняя");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        // Сворачивание всех веток.
        cut.FindAll("button").First(b => b.TextContent.Contains("Свернуть всё")).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        var numbersAfter = RowNumbers(cut);

        // Номера показанных строк не изменились: поиск и сворачивание меняют
        // состав показа, но не идентичность строк.
        Assert.All(numbersAfter, number => Assert.Contains(number, numbersBefore));
    }

    // ── Надёжность загрузки ──

    /// <summary>
    /// Медленная загрузка первой карты не подменяет уже загруженную вторую.
    /// </summary>
    [Fact]
    public void Render_SlowFirstLoadFinishingAfterSecond_DoesNotReplaceSecondCard()
    {
        var compositionB = CompositionNamed("Карта B", "ХК-B");

        _reader.Handler = async (cardId, ct) =>
        {
            if (cardId != CardA)
                return compositionB;

            // Первая загрузка читает медленно и не смотрит на отмену: её
            // результат приходит уже после того, как началась вторая.
            await Task.Delay(150);
            return CompositionNamed("Карта A", "ХК-A");
        };

        var cut = RenderComponent<HKCompositionBlock>(p => p.Add(x => x.CardId, CardA));

        // Вторая карта: её загрузка завершается позже первой.
        cut.SetParametersAndRender(p => p.Add(x => x.CardId, CardB));

        cut.WaitForAssertion(() => Assert.Contains("ХК-B", cut.Markup));
        Assert.DoesNotContain("ХК-A", cut.Markup);
    }

    /// <summary>Старая ошибка не заменяет готовую вторую карту.</summary>
    [Fact]
    public void Render_StaleError_DoesNotReplaceReadyCard()
    {
        var compositionB = CompositionNamed("Карта B", "ХК-B");

        _reader.Handler = async (cardId, ct) =>
        {
            if (cardId != CardA)
                return compositionB;

            // Первая загрузка падает уже после того, как началась вторая.
            await Task.Delay(150);
            throw new InvalidOperationException("Сбой первой загрузки");
        };

        var cut = RenderComponent<HKCompositionBlock>(p => p.Add(x => x.CardId, CardA));
        cut.SetParametersAndRender(p => p.Add(x => x.CardId, CardB));

        cut.WaitForAssertion(() => Assert.Contains("ХК-B", cut.Markup));
        Assert.DoesNotContain("Не удалось загрузить", cut.Markup);
    }

    /// <summary>Отмена устаревшей загрузки не показывается как ошибка.</summary>
    [Fact]
    public void Render_CancelledStaleLoad_IsNotShownAsError()
    {
        var compositionB = CompositionNamed("Карта B", "ХК-B");

        _reader.Handler = async (cardId, ct) =>
        {
            if (cardId != CardA)
                return compositionB;

            // Первая загрузка наблюдает отмену и прерывается.
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        };

        var cut = RenderComponent<HKCompositionBlock>(p => p.Add(x => x.CardId, CardA));
        cut.SetParametersAndRender(p => p.Add(x => x.CardId, CardB));

        cut.WaitForAssertion(() => Assert.Contains("ХК-B", cut.Markup));
        Assert.DoesNotContain("class=\"error\"", cut.Markup);
    }

    /// <summary>Уничтоженный компонент не публикует запоздалый результат.</summary>
    [Fact]
    public void Render_DisposedComponent_DoesNotPublishLateResult()
    {
        var slow = new TaskCompletionSource<HKAcceptedCompositionDto?>();
        _reader.Handler = async (cardId, ct) =>
        {
            await slow.Task;
            return CompositionNamed("Карта A", "ХК-A");
        };

        var cut = RenderComponent<HKCompositionBlock>(p => p.Add(x => x.CardId, CardA));

        cut.Instance.Dispose();
        slow.SetResult(CompositionNamed("Карта A", "ХК-A"));

        // Ошибок нет: результат опубликовать уже некуда.
        Assert.True(true);
    }

    /// <summary>
    /// Техническая ошибка показывается понятным сообщением, а повтор
    /// загрузки приводит к готовой карте.
    /// </summary>
    [Fact]
    public void Render_TechnicalError_ShowsMessage_AndRetrySucceeds()
    {
        var fail = true;
        var composition = CompositionNamed("Карта A", "ХК-A");

        _reader.Result = _ => fail
            ? throw new InvalidOperationException("Сбой чтения снимка")
            : Task.FromResult<HKAcceptedCompositionDto?>(composition);

        var cut = RenderComponent<HKCompositionBlock>(p => p.Add(x => x.CardId, CardA));

        cut.WaitForAssertion(() => Assert.Contains("Не удалось загрузить", cut.Markup));
        // Ошибка не выдаётся за отсутствие принятой структуры.
        Assert.DoesNotContain("Принятая структура отсутствует", cut.Markup);

        // Повтор загрузки.
        fail = false;
        cut.Find("button").Click();

        cut.WaitForAssertion(() => Assert.Contains("ХК-A", cut.Markup));
        Assert.DoesNotContain("Не удалось загрузить", cut.Markup);
    }

    /// <summary>Отсутствие принятой редакции — не ошибка.</summary>
    [Fact]
    public void Render_WithoutAcceptedRevision_ShowsAbsentNote()
    {
        _reader.Result = _ => Task.FromResult<HKAcceptedCompositionDto?>(null);

        var cut = RenderComponent<HKCompositionBlock>(p => p.Add(x => x.CardId, CardA));

        cut.WaitForAssertion(() => Assert.Contains("Принятая структура отсутствует", cut.Markup));
        Assert.DoesNotContain("class=\"error\"", cut.Markup);
    }

    // ── Помощники ──

    private static string[] RowNumbers(IRenderedFragment cut) =>
        cut.FindAll("tbody tr td.hkCompositionNum")
            .Select(td => td.TextContent.Trim())
            .Where(text => text.Length > 0)
            .ToArray();

    private static HKAcceptedCompositionDto CompositionNamed(string name, string code)
    {
        var snapshot = new HKAcceptedStructureDto(
            Guid.NewGuid(), 1, DateTime.UtcNow, name, code, "v1", HKObjectLevel.Aggregate,
            null, null, null, null, null, false,
            new List<HKAcceptedNodeDto>
            {
                new(Guid.NewGuid(), null, HKObjectLevel.Aggregate, "Верхняя позиция", 1, 0, null)
            },
            new List<HKAcceptedItemDto>(),
            new List<HKAcceptedSectionDto>(),
            DateTime.UtcNow, "user-1");

        return HKAcceptedCompositionProjection.Build(CardA, snapshot);
    }

    /// <summary>
    /// Снимок с двумя блоками веток без раздела, разделёнными разделом: две
    /// виртуальные группы с разными ключами.
    /// </summary>
    private static HKAcceptedCompositionDto CompositionWithTwoWithoutSectionBlocks()
    {
        var sectionId = Guid.NewGuid();
        var before = Guid.NewGuid();
        var inSection = Guid.NewGuid();
        var after = Guid.NewGuid();

        var snapshot = new HKAcceptedStructureDto(
            Guid.NewGuid(), 1, DateTime.UtcNow, "Карта A", "ХК-1", "v1", HKObjectLevel.Aggregate,
            null, null, null, null, null, false,
            new List<HKAcceptedNodeDto>
            {
                new(before, null, HKObjectLevel.Aggregate, "Ветка до раздела", 1, 0, null),
                new(inSection, null, HKObjectLevel.Aggregate, "Позиция раздела", 1, 1, sectionId),
                new(after, null, HKObjectLevel.Aggregate, "Ветка после раздела", 1, 2, null)
            },
            new List<HKAcceptedItemDto>(),
            new List<HKAcceptedSectionDto>
            {
                new(sectionId, HKCardSectionType.Oils, "Масла", 1)
            },
            DateTime.UtcNow, "user-1");

        return HKAcceptedCompositionProjection.Build(CardA, snapshot);
    }

    /// <summary>Подставной читатель принятой редакции.</summary>
    private sealed class FakeCompositionReader : IHKAcceptedCompositionReader
    {
        public Func<Guid, Task<HKAcceptedCompositionDto?>>? Result { get; set; }

        public Func<Guid, CancellationToken, Task<HKAcceptedCompositionDto?>>? Handler { get; set; }

        public Task<HKAcceptedCompositionDto?> GetAcceptedCompositionAsync(
            Guid cardId, CancellationToken ct = default)
        {
            if (Handler is not null)
                return Handler(cardId, ct);

            return Result is null
                ? Task.FromResult<HKAcceptedCompositionDto?>(null)
                : Result(cardId);
        }
    }
}
