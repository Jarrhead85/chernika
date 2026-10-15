using System.Net;
using System.Net.Http.Json;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain;
using Chernika.Infrastructure.Data;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// HTTP-контракт команд согласования этапов.
/// <para>
/// Проверяется реальный хост приложения: маршрутизация, политики авторизации,
/// привязка моделей и коды ответа. Права не подменяются — их считает тот же
/// <c>IPermissionService</c> по тем же назначениям ролей.
/// </para>
/// <para>
/// Карты и этапы создаются напрямую в БД: предмет проверки — код ответа HTTP,
/// а не путь создания карты.
/// </para>
/// </summary>
public class HKApprovalStageHttpIntegrationTests : IClassFixture<ChernikaApiFactory>
{
    private readonly ChernikaApiFactory _factory;

    public HKApprovalStageHttpIntegrationTests(ChernikaApiFactory factory) => _factory = factory;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private async Task<Guid> SeedCardAsync(Guid? branchId = null, bool withStages = false)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var branch = branchId is null
            ? await db.Branches.FirstAsync()
            : await db.Branches.FirstAsync(b => b.Id == branchId.Value);

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "ХК-HTTP-" + Suffix(),
            Name = "HTTP " + Suffix(),
            Version = "1.0",
            Status = HKCardStatus.Draft,
            ObjectLevel = HKObjectLevel.Undefined,
            IsAutonomous = true,
            BranchId = branch.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.HKCards.Add(card);

        if (withStages)
        {
            foreach (var stage in new[] { HKApprovalStage.Details, HKApprovalStage.Composition })
            {
                db.HKCardApprovals.Add(new HKCardApproval
                {
                    Id = Guid.NewGuid(),
                    HKCardId = card.Id,
                    Stage = stage,
                    State = stage == HKApprovalStage.Details
                        ? HKApprovalStageState.OnReview
                        : HKApprovalStageState.Draft,
                    AssignedApproverUserId = _factory.NormAdminUserName,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
        }

        await db.SaveChangesAsync();
        return card.Id;
    }

    private async Task<Guid> SeedOtherBranchAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var other = new Branch
        {
            Id = Guid.NewGuid(),
            Name = "Филиал " + Suffix(),
            Code = "F" + Suffix()[..6]
        };
        db.Branches.Add(other);
        await db.SaveChangesAsync();
        return other.Id;
    }

    private async Task<int> CountStagesAsync(Guid cardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.HKCardApprovals.CountAsync(a => a.HKCardId == cardId);
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync();

    /// <summary>
    /// Пользователь без действующего права просмотра.
    /// <para>
    /// У штатной роли читателя право просмотра есть, поэтому «читатель»
    /// не годится: без явного индивидуального запрета проверка была бы
    /// тавтологией — отказа ожидалось бы, а доступ выдавался бы по праву.
    /// </para>
    /// </summary>
    private async Task<string> SeedUserWithoutViewAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var users = sp.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var roles = sp.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole>>();

        var name = "http_noview_" + Suffix();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = name,
            FullName = "Без просмотра " + Suffix(),
            BranchId = Guid.Parse(_factory.BranchId),
            IsActive = true
        };
        var created = await users.CreateAsync(user);
        if (!created.Succeeded)
            throw new InvalidOperationException(
                string.Join("; ", created.Errors.Select(e => e.Description)));
        // Роль та же, что у обычного читателя: иначе отказ был бы следствием
        // отсутствия роли, а не индивидуального запрета права просмотра.
        var reader = await users.FindByNameAsync(_factory.ReadOnlyUserName);
        var readerRoles = await users.GetRolesAsync(reader!);
        foreach (var role in readerRoles)
            await users.AddToRoleAsync(user, role);

        db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            PermissionCode = PermissionCodes.HKView,
            IsGranted = false,
            GrantedByUserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        sp.GetRequiredService<IPermissionService>()
            .InvalidateCache(user.Id);

        return name;
    }

    /// <summary>GET не создаёт этапы — даже при повторных обращениях.</summary>
    [Fact]
    public async Task GetApprovals_DoesNotCreateOrChangeAnything()
    {
        var cardId = await SeedCardAsync();
        Assert.Equal(0, await CountStagesAsync(cardId));

        var client = _factory.CreateAs(_factory.NormAdminUserName);

        var first = await client.GetAsync($"/api/HKCards/{cardId}/approvals");
        var second = await client.GetAsync($"/api/HKCards/{cardId}/approvals");
        var caps = await client.GetAsync($"/api/HKCards/{cardId}/editing-capabilities");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, caps.StatusCode);

        Assert.Equal(0, await CountStagesAsync(cardId));
    }

    /// <summary>Карта чужого филиала недоступна: 403.</summary>
    [Fact]
    public async Task GetApprovals_OtherBranchCard_ReturnsForbidden()
    {
        var otherBranch = await SeedOtherBranchAsync();
        var cardId = await SeedCardAsync(otherBranch);

        var client = _factory.CreateAs(_factory.NormAdminUserName);
        var response = await client.GetAsync($"/api/HKCards/{cardId}/approvals");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountStagesAsync(cardId));
    }

    /// <summary>Отсутствующая карта — 404, а не 500.</summary>
    [Fact]
    public async Task GetApprovals_UnknownCard_ReturnsNotFound()
    {
        var client = _factory.CreateAs(_factory.NormAdminUserName);

        var approvals = await client.GetAsync($"/api/HKCards/{Guid.NewGuid()}/approvals");
        var caps = await client.GetAsync($"/api/HKCards/{Guid.NewGuid()}/editing-capabilities");

        Assert.Equal(HttpStatusCode.NotFound, approvals.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, caps.StatusCode);
    }

    /// <summary>Устаревшая версия экрана — 409, а не 400.</summary>
    [Fact]
    public async Task Decision_WithStaleRowVersion_ReturnsConflict()
    {
        var cardId = await SeedCardAsync(withStages: true);
        var client = _factory.CreateAs(_factory.NormAdminUserName);

        var response = await client.PostAsJsonAsync($"/api/HKCards/{cardId}/approvals/decision", new
        {
            stage = "details",
            decision = "approve",
            rowVersion = 999999u
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await BodyAsync(response);
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("postgres", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DbUpdateConcurrency", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLSTATE", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Неизвестный этап и неизвестное решение — 400.</summary>
    [Fact]
    public async Task StageCommands_WithInvalidInput_ReturnBadRequest()
    {
        var cardId = await SeedCardAsync(withStages: true);
        var client = _factory.CreateAs(_factory.NormAdminUserName);

        var badStage = await client.PostAsJsonAsync($"/api/HKCards/{cardId}/approvals/decision", new
        {
            stage = "неизвестный",
            decision = "approve",
            rowVersion = 1u
        });
        Assert.Equal(HttpStatusCode.BadRequest, badStage.StatusCode);

        var badDecision = await client.PostAsJsonAsync($"/api/HKCards/{cardId}/approvals/decision", new
        {
            stage = "details",
            decision = "неизвестное",
            rowVersion = 1u
        });
        Assert.Equal(HttpStatusCode.BadRequest, badDecision.StatusCode);
    }

    // ── Чтение принятой структуры ──

    /// <summary>
    /// GET принятой структуры не создаёт и не переключает данные.
    /// До назначения указателя ответ — 204, а не рабочее дерево.
    /// </summary>
    [Fact]
    public async Task GetAcceptedStructure_WithoutPointer_ReturnsNoContent()
    {
        var cardId = await SeedCardAsync();

        var client = _factory.CreateAs(_factory.NormAdminUserName);
        var response = await client.GetAsync($"/api/HKCards/{cardId}/accepted-structure");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>Карта чужого филиала — 403, принятая структура не раскрывается.</summary>
    [Fact]
    public async Task GetAcceptedStructure_OtherBranch_ReturnsForbidden()
    {
        var otherBranch = await SeedOtherBranchAsync();
        var cardId = await SeedCardAsync(otherBranch);

        var client = _factory.CreateAs(_factory.NormAdminUserName);
        var response = await client.GetAsync($"/api/HKCards/{cardId}/accepted-structure");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Отсутствующая карта — 404.</summary>
    [Fact]
    public async Task GetAcceptedStructure_UnknownCard_ReturnsNotFound()
    {
        var client = _factory.CreateAs(_factory.NormAdminUserName);
        var response = await client.GetAsync(
            $"/api/HKCards/{Guid.NewGuid()}/accepted-structure");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Ответ принятой структуры не содержит ни ключа хранения, ни пути файловой
    /// системы.
    /// </summary>
    [Fact]
    public async Task GetAcceptedStructure_DoesNotExposeStorageKey()
    {
        var cardId = await SeedCardAsync();

        var client = _factory.CreateAs(_factory.NormAdminUserName);
        var response = await client.GetAsync($"/api/HKCards/{cardId}/accepted-structure");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("StorageKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AttachmentStorageKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chernika-tests-storage", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Конкурентный проигрыш решения даёт 409, а не 500 и не произвольный 400.
    /// </summary>
    [Fact]
    public async Task Decision_LosingRace_ReturnsConflict()
    {
        var cardId = await SeedCardAsync(withStages: true);

        // Версия заведомо устаревшая: версия карты на экране уже не совпадает.
        var client = _factory.CreateAs(_factory.NormAdminUserName);
        var response = await client.PostAsJsonAsync(
            $"/api/HKCards/{cardId}/approvals/decision",
            new { stage = "details", decision = "return", rowVersion = 1u });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLSTATE", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Чтение без права просмотра — 403, а не тихая выдача этапов по одному
    /// известному идентификатору.
    /// </summary>
    [Fact]
    public async Task GetApprovals_WithoutViewPermission_ReturnsForbidden()
    {
        var cardId = await SeedCardAsync();
        var noView = await SeedUserWithoutViewAsync();
        var client = _factory.CreateAs(noView);

        var response = await client.GetAsync($"/api/HKCards/{cardId}/approvals");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountStagesAsync(cardId));
    }
}