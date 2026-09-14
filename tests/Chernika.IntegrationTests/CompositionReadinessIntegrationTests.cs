using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class CompositionReadinessIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public CompositionReadinessIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private TestScope Scope() => _fixture.CreateScope();

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    private void SetUser(TestScope s, ApplicationUser user) =>
        s.User.CurrentUserId = Guid.Parse(user.Id);

    private async Task<Guid> CreateModelAsync(TestScope s)
    {
        var model = new EquipmentModel
        {
            Id = Guid.NewGuid(),
            Index = "EM-" + Suffix(),
            Name = "Изделие " + Suffix(),
            IsDeleted = false,
        };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();
        return model.Id;
    }

    private async Task<Guid> CreateApprovedHkAsync(
        TestScope s, Guid modelId, HKCardStatus status = HKCardStatus.Approved,
        DateTime? effective = null, DateTime? expiration = null, Guid? branchId = null)
    {
        var hk = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "HK-" + Suffix(),
            Version = "v" + Suffix()[..4],
            Status = status,
            ObjectLevel = HKObjectLevel.EquipmentModel,
            EquipmentModelId = modelId,
            BranchId = branchId ?? _fixture.BranchA,
            CreatedAt = DateTime.UtcNow,
            ApprovedDate = status == HKCardStatus.Approved ? DateTime.UtcNow : null,
            EffectiveDate = effective,
            ExpirationDate = expiration,
        };
        s.Db.HKCards.Add(hk);
        await s.Db.SaveChangesAsync();
        return hk.Id;
    }

    private async Task<Guid> CreateApprovedCompositionAsync(TestScope s, Guid modelId)
    {
        var pc = new ProductComposition
        {
            Id = Guid.NewGuid(),
            EquipmentModelId = modelId,
            Version = "v" + Suffix()[..4],
            Status = ProductCompositionStatus.Approved,
            IsActive = true,
            ApprovedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        s.Db.ProductCompositions.Add(pc);
        await s.Db.SaveChangesAsync();
        return pc.Id;
    }

    [Fact]
    public async Task MissingCurrentHk_ReturnsExactRussianTextAndRegistryTarget()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateModelAsync(s);

        var readiness = await s.Equipment.GetCompositionNormativeReadinessAsync(
            HKObjectLevel.EquipmentModel, modelId);

        Assert.Equal(CompositionReadinessStatus.MissingHK, readiness.Status);
        Assert.Equal("Отсутствует действующая ХК", readiness.Message);
        Assert.Equal("Отсутствует", readiness.StatusDisplay);
        Assert.Null(readiness.CurrentApprovedHKCardId);
        Assert.Equal("Перейти в реестр ХК", readiness.HKActionLabel);
        Assert.StartsWith("/реестр-хк", readiness.HKNavigationTarget);
        Assert.DoesNotContain("/хк/создать", readiness.HKNavigationTarget);
    }

    [Fact]
    public async Task MissingComposition_RoutesToCompositionRegistryWithContext()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateModelAsync(s);
        await CreateApprovedHkAsync(s, modelId);

        var readiness = await s.Equipment.GetCompositionNormativeReadinessAsync(
            HKObjectLevel.EquipmentModel, modelId);

        Assert.Equal(CompositionReadinessStatus.MissingComposition, readiness.Status);
        Assert.Equal("Состав не зафиксирован", readiness.Message);
        Assert.Equal("Перейти к составам", readiness.CompositionActionLabel);
        Assert.StartsWith("/составы?level=EquipmentModel", readiness.CompositionNavigationTarget);
        Assert.Contains(Uri.EscapeDataString(readiness.ObjectCode), readiness.CompositionNavigationTarget);
    }

    [Fact]
    public async Task ValidApprovedHkAndActiveComposition_IsReadyWithExactHkLink()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateModelAsync(s);
        var hkId = await CreateApprovedHkAsync(s, modelId, effective: DateTime.UtcNow.AddDays(-1));
        var compositionId = await CreateApprovedCompositionAsync(s, modelId);

        var readiness = await s.Equipment.GetCompositionNormativeReadinessAsync(
            HKObjectLevel.EquipmentModel, modelId);

        Assert.Equal(CompositionReadinessStatus.Ready, readiness.Status);
        Assert.Equal("Готово", readiness.StatusDisplay);
        Assert.Equal(hkId, readiness.CurrentApprovedHKCardId);
        Assert.Equal("Открыть ХК", readiness.HKActionLabel);
        Assert.Equal($"/хк/{hkId}", readiness.HKNavigationTarget);
        Assert.Equal(compositionId, readiness.CurrentCompositionId);
        Assert.Equal("Открыть состав", readiness.CompositionActionLabel);
        Assert.Equal($"/составы/изделия/{modelId}/версии/{compositionId}", readiness.CompositionNavigationTarget);
    }

    [Fact]
    public async Task ExpiredHk_IsNotCurrent()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateModelAsync(s);
        await CreateApprovedHkAsync(s, modelId, expiration: DateTime.UtcNow.AddDays(-5));

        var readiness = await s.Equipment.GetCompositionNormativeReadinessAsync(
            HKObjectLevel.EquipmentModel, modelId);

        Assert.Equal(CompositionReadinessStatus.MissingHK, readiness.Status);
        Assert.Null(readiness.CurrentApprovedHKCardId);
    }

    [Fact]
    public async Task ArchivedHk_IsNotCurrent()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateModelAsync(s);
        await CreateApprovedHkAsync(s, modelId, status: HKCardStatus.Archived);

        var readiness = await s.Equipment.GetCompositionNormativeReadinessAsync(
            HKObjectLevel.EquipmentModel, modelId);

        Assert.Equal(CompositionReadinessStatus.MissingHK, readiness.Status);
    }

    [Fact]
    public async Task ForeignBranchHk_NotTreatedAsCurrentForBranchUser()
    {
        await using var s = Scope();
        var modelId = await CreateModelAsync(s);
        // ХК чужого филиала (BranchB)
        await CreateApprovedHkAsync(s, modelId, branchId: _fixture.BranchB);
        SetUser(s, _fixture.NormAdminA);

        var readiness = await s.Equipment.GetCompositionNormativeReadinessAsync(
            HKObjectLevel.EquipmentModel, modelId);

        Assert.Equal(CompositionReadinessStatus.MissingHK, readiness.Status);
        Assert.Null(readiness.CurrentApprovedHKCardId);
    }

    [Fact]
    public async Task ReadinessDto_ExposesNoGuidInUserTexts()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var modelId = await CreateModelAsync(s);
        await CreateApprovedHkAsync(s, modelId);

        var readiness = await s.Equipment.GetCompositionNormativeReadinessAsync(
            HKObjectLevel.EquipmentModel, modelId);

        Assert.DoesNotContain(Guid.NewGuid().ToString("D")[..8], readiness.Message);
        Assert.DoesNotContain(modelId.ToString("D"), readiness.Message);
        Assert.DoesNotContain("НК", readiness.Message);
        Assert.Contains("ХК", readiness.HKActionLabel);
    }
}
