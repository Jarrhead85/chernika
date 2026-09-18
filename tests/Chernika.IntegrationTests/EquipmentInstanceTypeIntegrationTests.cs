using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class EquipmentInstanceTypeIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public EquipmentInstanceTypeIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private TestScope Scope() => _fixture.CreateScope();

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    private void SetUser(TestScope s, ApplicationUser user) =>
        s.User.CurrentUserId = Guid.Parse(user.Id);

    private async Task<EquipmentType> CreateTypeAsync(TestScope s)
    {
        var type = new EquipmentType
        {
            Id = Guid.NewGuid(),
            TypeGroup = "Колёсная техника",
            Name = "Автомобиль " + Suffix(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        s.Db.EquipmentTypes.Add(type);
        await s.Db.SaveChangesAsync();
        return type;
    }

    private async Task<EquipmentModel> CreateModelAsync(TestScope s, Guid? typeId = null)
    {
        var model = new EquipmentModel
        {
            Id = Guid.NewGuid(),
            Index = "EM-" + Suffix(),
            Name = "Изделие " + Suffix(),
            EquipmentTypeId = typeId,
            IsDeleted = false,
        };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();
        return model;
    }

    private static EquipmentInstance NewInstance(Guid modelId, Guid? typeId = null, string? brand = null) => new()
    {
        Id = Guid.Empty,
        SerialNumber = "S-" + Suffix(),
        Index = "I-" + Suffix(),
        Name = "Экземпляр " + Suffix(),
        EquipmentModelId = modelId,
        EquipmentTypeId = typeId,
        Brand = brand,
        Modification = "мод",
    };

    [Fact]
    public async Task CreateInstance_AutoFillsTypeFromModel()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var type = await CreateTypeAsync(s);
        var model = await CreateModelAsync(s, type.Id);

        var created = await s.Equipment.CreateInstanceAsync(NewInstance(model.Id, brand: "Урал"));

        Assert.Equal(type.Id, created.EquipmentTypeId);
        Assert.Equal("Урал", created.Brand);
        Assert.Equal("мод", created.Modification);

        var reloaded = await s.Equipment.GetInstanceAsync(created.Id);
        Assert.Equal(type.Id, reloaded!.EquipmentTypeId);
    }

    [Fact]
    public async Task CreateInstance_TypeWithoutModelType_StaysAsProvided()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var type = await CreateTypeAsync(s);
        var model = await CreateModelAsync(s); // без вида техники

        var created = await s.Equipment.CreateInstanceAsync(NewInstance(model.Id, type.Id));

        Assert.Equal(type.Id, created.EquipmentTypeId);
    }

    [Fact]
    public async Task CreateInstance_TypeMismatchWithModel_Fails()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var modelType = await CreateTypeAsync(s);
        var otherType = await CreateTypeAsync(s);
        var model = await CreateModelAsync(s, modelType.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Equipment.CreateInstanceAsync(NewInstance(model.Id, otherType.Id)));
    }

    [Fact]
    public async Task CreateInstance_WithoutTypes_Succeeds()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var model = await CreateModelAsync(s);

        var created = await s.Equipment.CreateInstanceAsync(NewInstance(model.Id));

        Assert.Null(created.EquipmentTypeId);
    }

    [Fact]
    public async Task InstancesPaged_FiltersByEquipmentType()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var type = await CreateTypeAsync(s);
        var model = await CreateModelAsync(s, type.Id);
        var instance = await s.Equipment.CreateInstanceAsync(NewInstance(model.Id));
        var otherModel = await CreateModelAsync(s);
        await s.Equipment.CreateInstanceAsync(NewInstance(otherModel.Id));

        var page = await s.Equipment.GetInstancesPagedAsync(new EquipmentInstanceQuery
        {
            Page = 1,
            PageSize = 50,
            EquipmentTypeId = type.Id,
        });

        Assert.Contains(page.Items, i => i.Id == instance.Id);
        Assert.All(page.Items, i => Assert.Equal(type.Id, i.EquipmentTypeId));
    }

    [Fact]
    public async Task InstancesPaged_SearchByBrand_ReturnsInstance()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var model = await CreateModelAsync(s);
        var brand = "Бренд-" + Suffix();
        var instance = await s.Equipment.CreateInstanceAsync(NewInstance(model.Id, brand: brand));

        var page = await s.Equipment.GetInstancesPagedAsync(new EquipmentInstanceQuery
        {
            Page = 1,
            PageSize = 50,
            Search = brand,
        });

        Assert.Contains(page.Items, i => i.Id == instance.Id);
    }

    [Fact]
    public async Task UpdateInstance_CanChangeBrandAndModification()
    {
        await using var s = Scope();
        SetUser(s, _fixture.SystemAdminUser);
        var model = await CreateModelAsync(s);
        var created = await s.Equipment.CreateInstanceAsync(NewInstance(model.Id, brand: "Старый"));

        created.Brand = "Новый";
        created.Modification = "нм";
        await s.Equipment.UpdateInstanceAsync(created);

        var reloaded = await s.Equipment.GetInstanceAsync(created.Id);
        Assert.Equal("Новый", reloaded!.Brand);
        Assert.Equal("нм", reloaded.Modification);
    }
}
