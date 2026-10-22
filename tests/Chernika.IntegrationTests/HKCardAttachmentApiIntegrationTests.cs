using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// HTTP-контракт операций с PDF-основанием карты (PR № 10).
/// <para>
/// Раньше контроллер выполнял загрузку и удаление вложения мимо сервиса:
/// проверялся только общий статус карты, поэтому файл можно было подменить
/// под текущим рассмотрением реквизитов, а сброс согласования не происходил
/// вовсе. Теперь оба эндпоинта вызывают те же сервисные операции, что и
/// страница редактора, и проверяются на реальном хосте.
/// </para>
/// </summary>
public class HKCardAttachmentApiIntegrationTests : IClassFixture<ChernikaApiFactory>
{
    private readonly ChernikaApiFactory _factory;

    public HKCardAttachmentApiIntegrationTests(ChernikaApiFactory factory) => _factory = factory;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static byte[] PdfBytes(string marker = "test") =>
        Encoding.ASCII.GetBytes($"%PDF-1.4 {marker}");

    /// <summary>Карта в указанном состоянии с указанными этапами.</summary>
    private async Task<Guid> SeedCardAsync(
        HKCardStatus status = HKCardStatus.Draft,
        HKApprovalStageState? detailsState = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "ХК-API-" + Suffix(),
            Name = "API " + Suffix(),
            Version = "1.0",
            Status = status,
            ObjectLevel = HKObjectLevel.Undefined,
            IsAutonomous = true,
            BranchId = Guid.Parse(_factory.BranchId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.HKCards.Add(card);

        if (detailsState is not null)
        {
            foreach (var stage in new[] { HKApprovalStage.Details, HKApprovalStage.Composition })
            {
                db.HKCardApprovals.Add(new HKCardApproval
                {
                    Id = Guid.NewGuid(),
                    HKCardId = card.Id,
                    Stage = stage,
                    State = stage == HKApprovalStage.Details
                        ? detailsState.Value
                        : HKApprovalStageState.Draft,
                    UpdatedAtUtc = DateTime.UtcNow,
                });
            }
        }

        await db.SaveChangesAsync();
        return card.Id;
    }

    private async Task<uint> VersionAsync(Guid cardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.HKCards.AsNoTracking().FirstAsync(c => c.Id == cardId)).RowVersion;
    }

    private async Task<int> AttachmentCountAsync(Guid cardId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.HKCardAttachments.CountAsync(a => a.HKCardId == cardId);
    }

    private static MultipartFormDataContent PdfForm(byte[] content, string fileName = "scan.pdf", uint? rowVersion = null)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(part, "file", fileName);
        if (rowVersion is not null)
            form.Add(new StringContent(rowVersion.Value.ToString()), "rowVersion");
        return form;
    }

    [Fact]
    public async Task UploadAttachment_IntoDraft_Succeeds_AndReturnsState()
    {
        var cardId = await SeedCardAsync();
        var client = _factory.CreateAs(_factory.NormAdminUserName);

        var response = await client.PostAsync(
            $"/api/HKCards/{cardId}/attachment", PdfForm(PdfBytes("api")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await AttachmentCountAsync(cardId));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("cardRowVersion", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("detailsState", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadAttachment_WhileDetailsOnReview_IsRefused_SameRuleAsThePage()
    {
        var cardId = await SeedCardAsync(HKCardStatus.OnReview, HKApprovalStageState.OnReview);
        var client = _factory.CreateAs(_factory.NormAdminUserName);

        var response = await client.PostAsync(
            $"/api/HKCards/{cardId}/attachment", PdfForm(PdfBytes("api")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await AttachmentCountAsync(cardId));
        Assert.Contains("согласован", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadAttachment_WithStaleRowVersion_ReturnsConflict()
    {
        var cardId = await SeedCardAsync();
        var stale = await VersionAsync(cardId);

        // Версия устарела: карта изменилась после её чтения.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var card = await db.HKCards.FirstAsync(c => c.Id == cardId);
            card.Notes = "изменено после чтения версии";
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateAs(_factory.NormAdminUserName);
        var response = await client.PostAsync(
            $"/api/HKCards/{cardId}/attachment", PdfForm(PdfBytes("api"), rowVersion: stale));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await AttachmentCountAsync(cardId));
    }

    [Fact]
    public async Task DeleteAttachment_OnAcceptedCard_IsRefused()
    {
        var cardId = await SeedCardAsync(HKCardStatus.Approved);
        var client = _factory.CreateAs(_factory.NormAdminUserName);

        var response = await client.DeleteAsync($"/api/HKCards/{cardId}/attachment");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetAttachment_Metadata_DoesNotExposeStorageKey()
    {
        var cardId = await SeedCardAsync();
        var client = _factory.CreateAs(_factory.NormAdminUserName);

        await client.PostAsync($"/api/HKCards/{cardId}/attachment", PdfForm(PdfBytes("api")));
        var response = await client.GetAsync($"/api/HKCards/{cardId}/attachment");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("StorageKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hk/", body, StringComparison.OrdinalIgnoreCase);
    }
}
