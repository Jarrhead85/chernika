using Chernika.Domain;
using Chernika.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;
using Xunit;
using Xunit.Abstractions;

namespace Chernika.IntegrationTests;

/// <summary>
/// Проверяемая матрица покрытия полномочий.
/// <para>
/// Схема: код → защищающая операция → гейт сервиса или политика хоста →
/// регрессионные тесты. Перечень исключений задан явно и вручную, с
/// конкретной причиной. Он не пополняется результатом сканирования: иначе
/// любое новое непокрытое право автоматически «объяснялось» само себе и
/// тест оставался зелёным.
/// </para>
/// <para>
/// Покрытие собирается из двух независимых источников. Политики читаются из
/// реального рантайма обоих хостов (эндпоинты Api и атрибуты страниц Web), а
/// сервисные гейты - из тел методов сервисов. Отсутствие защиты нельзя
/// объявлять по одному лишь сканированию Api: право может охраняться сервисом,
/// а страница может требовать политику, которой нет в Api.
/// </para>
/// </summary>
public class PermissionCoverageMatrixTests
{
    private readonly ITestOutputHelper _output;

    public PermissionCoverageMatrixTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Права, у которых нет ни операции под политикой, ни проверки в сервисе.
    /// Заполняется вручную и только с указанием причины.
    /// <para>
    /// Пустой перечень означает, что каждое активное право чем-то защищено.
    /// Код, которому позже появилась операция или сервисная проверка, обязан
    /// быть отсюда удалён - на это нацелена проверка ниже.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> ApprovedWithoutOperation =
        new(StringComparer.Ordinal);

    [Fact]
    public async Task ActivePermissions_AreCoveredByOperationOrApprovedReason()
    {
        var repoRoot = FindRepoRoot();
        var serviceGates = ScanServiceGates(repoRoot);
        var policyGates = await ScanPolicyGatesAsync();

        var active = PermissionCatalog.All
            .Where(d => Chernika.Web.Services.IndividualPermissionDecision.IsActive(d.Code))
            .ToList();

        var allCodes = PermissionCatalog.All.Select(d => d.Code).ToHashSet(StringComparer.Ordinal);

        // Ключ исключения обязан существовать в каталоге: опечатка иначе молча
        // оставляла бы настоящее исключение незамеченным.
        foreach (var key in ApprovedWithoutOperation.Keys)
        {
            Assert.True(allCodes.Contains(key),
                "В перечне исключений неизвестный код полномочия: " + key);

            Assert.False(string.IsNullOrWhiteSpace(ApprovedWithoutOperation[key]),
                "Для исключения должна быть указана причина: " + key);
        }

        var uncovered = new List<string>();
        var lines = new List<string>
        {
            "МАТРИЦА ПОКРЫТИЯ ПОЛОМОЧИЙ",
            "=========================================",
            "",
            $"Активных прав: {active.Count}",
            $"Политик эндпоинтов Api: {policyGates.EndpointPolicies.Count}",
            $"Политик страниц Web: {policyGates.PagePolicies.Count}",
            "",
            "код | политика | сервисный гейт",
        };

        foreach (var def in active.OrderBy(d => d.Code, StringComparer.Ordinal))
        {
            var policies = policyGates.Get(def.Code);
            var gates = serviceGates.TryGetValue(def.Code, out var g)
                ? g.ToArray()
                : Array.Empty<string>();

            var hasCoverage = policies.Length > 0 || gates.Length > 0;
            var approved = ApprovedWithoutOperation.ContainsKey(def.Code);

            lines.Add($"{def.Code} | {(policies.Length > 0 ? string.Join(",", policies) : "-")}"
                + $" | {(gates.Length > 0 ? string.Join(",", gates) : "-")}");

            if (hasCoverage && approved)
            {
                // Операция появилась - исключение устарело и обязано быть
                // пересмотрено, иначе список превратился бы в вечный.
                Assert.Fail(
                    "Право перечислено как не покрытое, но операция или сервисный "
                    + "гейт уже есть. Удалите его из ApprovedWithoutOperation: "
                    + def.Code);
            }

            if (!hasCoverage && !approved)
                uncovered.Add(def.Code);
        }

        lines.Add("");
        lines.Add("=== Явные исключения (заполнены вручную, сканом не пополняются) ===");
        if (ApprovedWithoutOperation.Count == 0)
            lines.Add("  (нет)");
        else
        {
            foreach (var (code, reason) in ApprovedWithoutOperation.OrderBy(x => x.Key, StringComparer.Ordinal))
                lines.Add($"  {code}: {reason}");
        }

        lines.Add("");
        lines.Add("=== Активные права без операции и без сервисного гейта ===");
        if (uncovered.Count == 0)
            lines.Add("  (нет)");
        else
        {
            foreach (var code in uncovered)
                lines.Add($"  {code} - {PermissionCatalog.All.First(d => d.Code == code).Name}");
        }

        var report = string.Join("\n", lines);
        File.WriteAllLines(Path.Combine(LogsDirectory(), "permission_matrix.txt"), lines);
        _output.WriteLine(report);

        Assert.True(
            uncovered.Count == 0,
            "Активные права без защищённой операции и без утверждённой причины: "
            + string.Join(", ", uncovered));
    }

    [Fact]
    public void ExceptionList_DoesNotGrowAutomatically()
    {
        // Отдельная проверка намерения: перечень исключений обязан быть мал и
        // осмыслен. Его рост без разбора - признак того, что список начали
        // использовать как свалку для необъяснённых находок.
        Assert.True(ApprovedWithoutOperation.Count <= 12,
            "Слишком много исключений (" + ApprovedWithoutOperation.Count
            + "): каждое требует разбора, а не накопления.");
    }

    /// <summary>Ищет корень репозитория по файлу решения.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Chernika.Infrastructure"))
                && File.Exists(Path.Combine(dir.FullName, "Chernika.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Корень репозитория не найден.");
    }

    private static string LogsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "logs")))
                return Path.Combine(dir.FullName, "logs");

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Каталог logs не найден.");
    }

    /// <summary>
    /// Собирает сервисные гейты по исходникам сервисов.
    /// <para>
    /// Различаются два вида. Прямой гейт: код передан в проверку прав в той же
    /// строке. Динамический гейт: код выбирается функцией-маппером по статусу
    /// или уровню объекта, поэтому литерал стоит в другом месте того же
    /// сервиса, а требование прав происходит позже. Раньше такие коды
    /// ошибочно попадали в «без покрытия» только потому, что литерал не
    /// оказался в строке вызова.
    /// </para>
    /// <para>
    /// Слабое место такого разбора честно обозначено в отчёте: динамический
    /// гейт подтверждает наличие кода в сервисе, но не доказывает достижимость
    /// маппера. Поэтому такие строки помечены отдельно, а не выдаются за прямые.
    /// </para>
    /// </summary>
    private static Dictionary<string, List<string>> ScanServiceGates(string repoRoot)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var servicesDir = Path.Combine(repoRoot, "src", "Chernika.Infrastructure", "Services");
        if (!Directory.Exists(servicesDir))
            return result;

        // Имя свойства в коде и код в каталоге различаются:
        // PermissionCodes.HKNodeSubmit == "HK.Node.Submit". Без отображения
        // сканер сравнивал бы несопоставимые строки и объявил бы защищёнными
        // несуществующие коды.
        var byMemberName = typeof(PermissionCodes)
            .GetFields(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.GetField)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => new { Name = f.Name, Code = (string)f.GetRawConstantValue()! })
            .Where(x => !string.IsNullOrWhiteSpace(x.Code))
            .ToDictionary(x => x.Name, x => x.Code, StringComparer.Ordinal);

        var demandMarkers = new[]
        {
            "DemandPermissionAsync", "HasPermissionAsync", "DemandAsync",
            "DemandEitherAsync", "DemandExportPermissionAsync", "DemandViewPermissionAsync",
            "EnsureEditDraftAccessAsync", "EnsureCompositionAccessAsync",
            "EnsureCardReadAccessAsync", "EnsureAttachmentAccessAsync",
            "CheckStatusChangePermissionAsync",
        };

        foreach (var file in Directory.EnumerateFiles(servicesDir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            var typeName = Path.GetFileNameWithoutExtension(file);

            var hasDemand = demandMarkers.Any(m => text.Contains(m));
            if (!hasDemand)
                continue;

            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*"))
                    continue;
                if (!trimmed.Contains("PermissionCodes."))
                    continue;

                var direct = demandMarkers.Any(m => trimmed.Contains(m));

                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(
                        trimmed, @"PermissionCodes\.(\w+)"))
                {
                    var member = m.Groups[1].Value;

                    if (!byMemberName.TryGetValue(member, out var code))
                        continue;

                    var gate = (direct ? "прямой " : "динамический ") + typeName + ": " + trimmed;

                    if (!result.TryGetValue(code, out var list))
                    {
                        list = new List<string>();
                        result[code] = list;
                    }

                    if (!list.Contains(gate))
                        list.Add(gate);
                }
            }
        }

        return result;
    }

    private sealed record PolicyGates(
        Func<string, string[]> PoliciesFor,
        IReadOnlyCollection<string> EndpointPolicies,
        IReadOnlyCollection<string> PagePolicies)
    {
        public string[] Get(string code) => PoliciesFor(code);
    }

    /// <summary>
    /// Политики берутся из рантайма: эндпоинты настоящего хоста Api и
    /// атрибуты страниц настоящей сборки Web. Наличие кода в метаданных
    /// само по себе не считается защитой - защиту подтверждает проверка в
    /// сервисе или политика, которая действительно требуется операцией.
    /// </summary>
    private static async Task<PolicyGates> ScanPolicyGatesAsync()
    {
        using var api = new ChernikaApiFactory();
        await api.InitializeAsync();

        var endpoints = api.Services.GetRequiredService<EndpointDataSource>();
        var policyProvider = api.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var byCode = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var endpointPolicies = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var endpoint in endpoints.Endpoints)
        {
            var policyName = endpoint.Metadata.GetMetadata<IAuthorizeData>()?.Policy;
            if (string.IsNullOrEmpty(policyName))
                continue;

            endpointPolicies.Add(policyName);

            var policy = await policyProvider.GetPolicyAsync(policyName);
            if (policy is null)
                continue;

            foreach (var requirement in policy.Requirements.OfType<PermissionRequirement>())
            {
                foreach (var code in requirement.PermissionCodes)
                {
                    if (!byCode.TryGetValue(code, out var set))
                    {
                        set = new SortedSet<string>(StringComparer.Ordinal);
                        byCode[code] = set;
                    }

                    set.Add(policyName + " (Api)");
                }
            }
        }

        var pagePolicies = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var type in typeof(Chernika.Web.Services.IndividualPermissionDecision).Assembly.GetTypes())
        {
            var attribute = type
                .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .FirstOrDefault(a => !string.IsNullOrEmpty(a.Policy));

            if (attribute?.Policy is not { } pagePolicy)
                continue;

            pagePolicies.Add(pagePolicy);

            var policy = await policyProvider.GetPolicyAsync(pagePolicy);
            if (policy is null)
                continue;

            foreach (var requirement in policy.Requirements.OfType<PermissionRequirement>())
            {
                foreach (var code in requirement.PermissionCodes)
                {
                    if (!byCode.TryGetValue(code, out var set))
                    {
                        set = new SortedSet<string>(StringComparer.Ordinal);
                        byCode[code] = set;
                    }

                    set.Add(pagePolicy + " (Web)");
                }
            }
        }

        return new PolicyGates(
            code => byCode.TryGetValue(code, out var set) ? set.ToArray() : Array.Empty<string>(),
            endpointPolicies,
            pagePolicies);
    }
}