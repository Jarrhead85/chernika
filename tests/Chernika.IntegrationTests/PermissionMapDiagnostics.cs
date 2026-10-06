using System.Text.RegularExpressions;
using Chernika.Domain;
using Chernika.Infrastructure.Services;
using Chernika.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Chernika.IntegrationTests;

/// <summary>
/// Диагностика: достоверная карта полномочий (раздел 1 PR).
/// <para>
/// Карта строится из РАНТАЙМА, а не разбором исходников: политики берутся у
/// провайдера реального хоста, страницы — из атрибута на скомпилированном
/// компоненте. Разбор текста надёжен только для объявления политик, поэтому
/// он применяется там и только там.
/// </para>
/// <para>
/// Тест ничего не утверждает о покрытии: он строит карту и пишет её в
/// logs\permission_map.txt. Утверждения о полноте появится вместе с
/// исправлениями, иначе получился бы запрещённый «зелёный» тест.
/// </para>
/// </summary>
public class PermissionMapDiagnostics
{
    private readonly ITestOutputHelper _output;
    private const string ApiProgram = @"D:\Chernika\src\Chernika.Api\Program.cs";
    private const string WebProgram = @"D:\Chernika\src\Chernika.Web\Program.cs";

    public PermissionMapDiagnostics(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task BuildRuntimeMap()
    {
        var apiPolicies = ParsePolicies(File.ReadAllText(ApiProgram));
        var webPolicies = ParsePolicies(File.ReadAllText(WebProgram));

        using var api = new ChernikaApiFactory();
        await api.InitializeAsync();

        var provider = api.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>();

        var apiOps = new List<(string Endpoint, string Policy, string Codes)>();
        var noPolicyOps = new List<string>();

        foreach (var ep in endpoints.Endpoints)
        {
            var display = ep.DisplayName ?? "(без имени)";
            var authorize = ep.Metadata.GetMetadata<IAuthorizeData>();

            if (authorize?.Policy is not { Length: > 0 } policyName)
            {
                noPolicyOps.Add(display);
                continue;
            }

            var codes = await ResolveCodes(provider, policyName);
            apiOps.Add((display, policyName, codes));
        }

        var pageOps = new List<(string Page, string Policy, string Codes)>();
        foreach (var type in typeof(IndividualPermissionDecision).Assembly.GetTypes())
        {
            var attr = type.GetCustomAttributes(typeof(AuthorizeAttribute), false)
                .Cast<AuthorizeAttribute>()
                .FirstOrDefault(a => !string.IsNullOrEmpty(a.Policy));

            if (attr?.Policy is not { } policy)
                continue;

            pageOps.Add((type.Name, policy, await ResolveCodes(provider, policy)));
        }

        var lines = new List<string>
        {
            "ДОСТОВЕРНАЯ КАРТА ПОЛОМОЧИЙ (из рантайма)",
            "========================================",
            "",
            $"политик объявлено в Api: {apiPolicies.Count}",
            $"политик объявлено в Web: {webPolicies.Count}",
            "",
            "--- РАСХОЖДЕНИЕ ОБЪЯВЛЕНИЙ ---",
        };

        foreach (var only in apiPolicies.Keys.Except(webPolicies.Keys).OrderBy(x => x))
            lines.Add($"  есть только в Api: {only} -> {string.Join(", ", apiPolicies[only])}");

        foreach (var only in webPolicies.Keys.Except(apiPolicies.Keys).OrderBy(x => x))
            lines.Add($"  есть только в Web: {only} -> {string.Join(", ", webPolicies[only])}");

        lines.Add("");
        lines.Add($"--- ОПЕРАЦИИ API: {apiOps.Count} с политикой, {noPolicyOps.Count} без политики ---");
        lines.Add("");
        lines.Add("БЕЗ ПОЛИТИКИ (только аутентификация — индивидуальный запрет их НЕ закрывает):");

        foreach (var op in noPolicyOps.OrderBy(x => x))
            lines.Add("  " + op);

        lines.Add("");
        lines.Add("С ПОЛИТИКОЙ:");

        foreach (var (endpoint, policy, codes) in apiOps.OrderBy(x => x.Policy))
            lines.Add($"  {policy,-24} {codes,-70} {endpoint}");

        lines.Add("");
        lines.Add($"--- СТРАНИЦЫ WEB: {pageOps.Count} ---");

        foreach (var (page, policy, codes) in pageOps.OrderBy(x => x.Policy))
            lines.Add($"  {policy,-24} {codes,-70} {page}");

        lines.Add("");
        lines.Add("--- ПОКРЫТИЕ ПРАВ ПО ЭТОЙ КАРТЕ ---");
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var codes in apiOps.Select(x => x.Codes).Concat(pageOps.Select(x => x.Codes)))
            foreach (var code in SplitCodes(codes))
                covered.Add(code);

        // Активные права считаются ТЕМ ЖЕ правилом, что и форма выдачи: иначе отчёт
// расходился бы с интерфейсом и показывал бы устаревшие общие коды как действующие.
var active = PermissionCatalog.All.Where(d => IndividualPermissionDecision.IsActive(d.Code)).ToList();
        var conserved = PermissionCatalog.All
            .Where(d => PermissionCatalog.IsConserved(d.Code)).ToList();
        var deprecated = PermissionCatalog.All
            .Where(d => PermissionCatalog.IsDeprecated(d.Code)).ToList();

        lines.Add($"  активных прав: {active.Count}, из них под защитой: {active.Count(d => covered.Contains(d.Code))}");
        lines.Add($"  законсервированных ИК: {conserved.Count}");
        lines.Add($"  устаревших общих (без своей операции): {deprecated.Count}"
            + string.Concat(deprecated.Select(d => $"\n    {d.Code} — {d.Name}")));

        lines.Add("");
        lines.Add("АКТИВНЫЕ ПРАВА, НЕ ПОПАВШИЕ НИ В ОДНУ ПОЛИТИКУ:");

        foreach (var def in active.Where(d => !covered.Contains(d.Code)).OrderBy(d => d.Code))
            lines.Add($"  {def.Code} — {def.Name} [{def.Module}]");

        Write(lines);
        _output.WriteLine(string.Join("\n", lines.Take(60)));
    }

    private static async Task<string> ResolveCodes(IAuthorizationPolicyProvider provider, string policyName)
    {
        var policy = await provider.GetPolicyAsync(policyName);

        if (policy is null)
            return "(политика не найдена у провайдера этого хоста)";

        var codes = policy.Requirements
            .OfType<PermissionRequirement>()
            .SelectMany(r => r.PermissionCodes)
            .ToList();

        return codes.Count == 0 ? "(без требования PermissionRequirement)" : string.Join(", ", codes);
    }

    private static IEnumerable<string> SplitCodes(string codes) =>
        codes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => c.StartsWith("HK.") || c.StartsWith("Reference.")
                     || c.StartsWith("Composition.") || c.StartsWith("IndividualCard.")
                     || c.StartsWith("Task.") || c.StartsWith("Notification.")
                     || c.StartsWith("Audit.") || c.StartsWith("Report.")
                     || c.StartsWith("Users.") || c.StartsWith("Permissions.")
                     || c.StartsWith("System."));

    private static Dictionary<string, List<string>> ParsePolicies(string programText)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (Match m in Regex.Matches(programText,
            @"AddPolicy\(""(?<name>[^""]+)""\s*,\s*policy\s*=>\s*policy\.AddRequirements\(new PermissionRequirement\((?<codes>[^)]*)\)\)",
            RegexOptions.Singleline))
        {
            var codes = Regex.Matches(m.Groups["codes"].Value, @"PermissionCodes\.(\w+)")
                .Select(c => c.Groups[1].Value)
                .ToList();

            result[m.Groups["name"].Value] = codes;
        }

        return result;
    }

    private void Write(List<string> lines)
    {
        var dir = AppContext.BaseDirectory;
        string? root = null;

        for (var i = 0; i < 12 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Chernika.sln")))
            {
                root = dir;
                break;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        if (root is null)
        {
            _output.WriteLine("корень репозитория не найден");
            return;
        }

        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllLines(Path.Combine(logs, "permission_map.txt"), lines);
    }
}