using System.Text.RegularExpressions;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Карта обращений к переходным колонкам <c>GsmMaterial.Type/Gost/Description</c>.
/// <para>
/// Это проверка, которой заменён отчёт о расхождении <c>Gost</c>↔<c>Nd</c>.
/// Тот отчёт измерял состояние ДАННЫХ, а спрашивал про КОД: расхождение может
/// быть нулевым при живом потребителе legacy-полей и ненулевым при полностью
/// переключённом коде. Готовность к удалению колонок доказывается картой
/// обращений и тестами на каждого потребителя, а не числом расхождений.
/// </para>
/// <para>
/// <b>Что здесь проверяется и что — нет.</b> Текстовый анализ принципиально не
/// может доказать отсутствие потребителей: свойство <c>Description</c> есть ещё
/// у двенадцати сущностей, и отличить <c>branch.Description</c> от
/// <c>material.Description</c> по тексту нельзя. Поэтому проверяется только
/// однозначный случай — обращение через навигационное свойство типа
/// <c>GsmMaterial</c>. Авторитетная проверка готовности — другая и простая:
/// удалить три свойства и собрать решение; компилятор покажет всех потребителей
/// точно. Это и есть процедура фазы B.
/// </para>
/// </summary>
public class GsmLegacyColumnConsumerMapTests
{
    /// <summary>
    /// Подтверждённые потребители (однозначные обращения).
    /// <para>
    /// После фазы B PR-6 список <b>пуст</b>. Последний потребитель —
    /// <c>IndividualCardService</c>, строящий снимки ИК, — адаптирован в этой же
    /// фазе: снимок наполняется живым <c>Nd</c> и первой подгруппой по алфавиту,
    /// а историческое чтение снимков не изменилось.
    /// </para>
    /// <para>
    /// Любое новое обращение роняет тест. Возвращать удалённые колонки молча
    /// нельзя, а удалять их повторно уже нечего.
    /// </para>
    /// </summary>
    private static readonly (string Path, string Reads, string Status)[] KnownConsumers = [];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Chernika.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// Исходники для проверки. Исключены исторические миграции EF (не
    /// исполняются и не читают свойства сущности; переписывать их запрещено),
    /// obj/bin и сам тест.
    /// </summary>
    private static IEnumerable<string> SourceFiles(string root)
    {
        foreach (var folder in new[] { "src", "tests" })
        {
            var top = Path.Combine(root, folder);
            if (!Directory.Exists(top)) continue;

            foreach (var file in Directory.EnumerateFiles(top, "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                    continue;

                var relative = file.Replace('\\', '/');
                if (relative.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase)
                    || relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                    || relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                    || relative.EndsWith(nameof(GsmLegacyColumnConsumerMapTests) + ".cs", StringComparison.Ordinal))
                    continue;

                // Относительный путь от корня репозитория: он должен совпадать с
                // путями в карте, иначе объяснение срабатываний не найдётся.
                yield return Path.GetRelativePath(root, file).Replace('\\', '/');
            }
        }
    }

    /// <summary>
    /// Однозначное обращение к переходному полю марки: путь до поля проходит
    /// через навигационное свойство с именем <c>GsmMaterial</c>. Строка с
    /// именем свойства в комментарии не считается обращением.
    /// </summary>
    private static readonly Regex LegacyMemberAccess = new(
        @"\.GsmMaterial\.(Type|Gost|Description)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void LegacyColumns_HaveNoUnaccountedQualifiedConsumers()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var relative in SourceFiles(root))
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            var inBlockComment = false;

            foreach (var raw in File.ReadAllLines(full))
            {
                var line = raw.TrimEnd('\r').Trim();

                if (inBlockComment)
                {
                    if (line.Contains("*/", StringComparison.Ordinal)) inBlockComment = false;
                    continue;
                }

                if (line.StartsWith("/*", StringComparison.Ordinal)
                    && !line.Contains("*/", StringComparison.Ordinal))
                {
                    inBlockComment = true;
                    continue;
                }

                if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                if (!LegacyMemberAccess.IsMatch(line)) continue;

                offenders.Add(relative + ": " + line);
            }
        }

        var explained = offenders
            .Where(o => KnownConsumers.Any(k => o.StartsWith(k.Path, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var unexpected = offenders.Except(explained).ToList();

        Assert.True(
            unexpected.Count == 0,
            "Найдены неучтённые обращения к переходным полям марки. "
            + "Перед удалением колонок перевести на Nd/классификацию и внести в карту:\n"
            + string.Join("\n", unexpected.Take(40)));
    }

    [Fact]
    public void KnownConsumers_StillExist()
    {
        // Карта не должна молча устареть: если потребитель переименован или
        // обращение ушло, тест обязан это заметить. Иначе утверждение выше
        // описывало бы несуществующий код и снимало бы стоп-сигнал.
        var root = RepoRoot();
        foreach (var (path, _, _) in KnownConsumers)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), "Потребитель из карты не найден: " + path);
        }
    }

    [Fact]
    public void GsmMaterialEntity_NoLongerDeclaresLegacyColumns()
    {
        // Обратная к прежней проверка: после фазы B колонок в сущности быть не
        // должно. Возврат любой из них — сознательный откат перехода, который
        // обязан быть явным, а не побочным эффектом правки.
        var root = RepoRoot();
        var entity = File.ReadAllText(Path.Combine(
            root, "src", "Chernika.Domain", "Entities", "GsmMaterial.cs"));

        Assert.DoesNotContain("public string Type { get; set; }", entity, StringComparison.Ordinal);
        Assert.DoesNotContain("public string? Gost { get; set; }", entity, StringComparison.Ordinal);
        Assert.DoesNotContain("public string? Description { get; set; }", entity, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportQuery_HasNoDivergenceMethodLeft()
    {
        // Отчёт о расхождении удалён как неверный критерий. Проверяется
        // ОТСУТСТВИЕ МЕТОДА, а не отсутствие упоминания: в сервисе осталась
        // комментарий-замена, объясняющий, почему отчёт удалён, и искать его
        // текст бессмысленно.
        var root = RepoRoot();
        var service = File.ReadAllText(Path.Combine(
            root, "src", "Chernika.Infrastructure", "Services", "GsmMaterialService.cs"));

        Assert.DoesNotContain(
            "Task<List<GsmTransitionDivergence>>",
            service,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "public async Task<List<GsmTransitionDivergence>>",
            service,
            StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            root, "src", "Chernika.Domain", "Models", "GsmTransitionDivergence.cs")));
    }
}
