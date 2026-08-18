using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class ReferencePermissionsIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public ReferencePermissionsIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Operator_WithReferenceView_CanListEquipmentTypes()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);

        var result = await s.Equipment.GetEquipmentTypesPagedAsync(new EquipmentTypeQuery(), default);

        Assert.NotNull(result);
        Assert.True(result.TotalCount >= 0);
    }

    [Fact]
    public async Task Operator_WithoutReferenceEdit_CannotCreateEquipmentType()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.Equipment.CreateEquipmentTypeAsync(new EquipmentType { Name = "Тестовый вид" }));
    }

    [Fact]
    public async Task NormAdmin_WithReferenceEdit_CanCrudEquipmentType()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var created = await s.Equipment.CreateEquipmentTypeAsync(new EquipmentType { Name = $"CRUD тип {Guid.NewGuid():N}" });
        Assert.NotEqual(Guid.Empty, created.Id);

        created.Name = $"CRUD тип обновлён {Guid.NewGuid():N}";
        var updated = await s.Equipment.UpdateEquipmentTypeAsync(created);
        Assert.Equal(created.Name, updated.Name);

        var deleted = await s.Equipment.DeleteEquipmentTypeAsync(created.Id);
        Assert.True(deleted);

        var restored = await s.Equipment.RestoreEquipmentTypeAsync(created.Id);
        Assert.True(restored);
    }

    [Fact]
    public async Task Operator_CanViewBranches_ButCannotCreateBranch()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);

        var list = await s.Equipment.GetBranchesPagedAsync(new BranchQuery(), default);
        Assert.NotNull(list);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.Equipment.CreateBranchAsync(new Branch { Name = $"Тест филиал {Guid.NewGuid():N}" }));
    }

    [Fact]
    public async Task NormAdmin_WithoutSystemConfig_CannotCreateBranch()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.Equipment.CreateBranchAsync(new Branch { Name = $"Тест филиал {Guid.NewGuid():N}" }));
    }

    [Fact]
    public async Task SystemAdmin_CanCrudBranch()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var created = await s.Equipment.CreateBranchAsync(new Branch { Name = $"Тест филиал {Guid.NewGuid():N}" });
        Assert.NotEqual(Guid.Empty, created.Id);

        created.Name = $"Тест филиал обновлён {Guid.NewGuid():N}";
        var updated = await s.Equipment.UpdateBranchAsync(created);
        Assert.True(updated);

        var deleted = await s.Equipment.DeleteBranchAsync(created.Id);
        Assert.True(deleted.Deleted);

        var restored = await s.Equipment.RestoreBranchAsync(created.Id);
        Assert.True(restored);
    }

    [Fact]
    public async Task EquipmentType_IsDeletedFilter_AppliedInDatabase()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var name = $"Filter тип {Guid.NewGuid():N}";
        var created = await s.Equipment.CreateEquipmentTypeAsync(new EquipmentType { Name = name });
        await s.Equipment.DeleteEquipmentTypeAsync(created.Id);

        var active = await s.Equipment.GetEquipmentTypesPagedAsync(new EquipmentTypeQuery { ShowDeleted = false }, default);
        Assert.DoesNotContain(active.Items, x => x.Id == created.Id);

        var archived = await s.Equipment.GetEquipmentTypesPagedAsync(new EquipmentTypeQuery { ShowDeleted = true }, default);
        Assert.Contains(archived.Items, x => x.Id == created.Id);

        var all = await s.Equipment.GetEquipmentTypesPagedAsync(new EquipmentTypeQuery { ShowDeleted = null, Search = name }, default);
        Assert.Contains(all.Items, x => x.Id == created.Id);
    }

    [Fact]
    public async Task Operator_WithReferenceView_CanListGsmMaterials()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);

        var result = await s.GsmMaterials.GetPagedAsync(new GsmMaterialQuery(), default);

        Assert.NotNull(result);
        Assert.True(result.TotalCount >= 0);
    }

    [Fact]
    public async Task Operator_WithoutReferenceEdit_CannotCreateGsmMaterial()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => s.GsmMaterials.CreateAsync(new GsmMaterial { Name = "Тестовый ГСМ", Type = "Бензин" }));
    }

    [Fact]
    public async Task NormAdmin_WithReferenceEdit_CanCrudGsmMaterial()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var created = await s.GsmMaterials.CreateAsync(new GsmMaterial
        {
            Name = $"CRUD ГСМ {Guid.NewGuid():N}",
            Type = "Бензин",
            Gost = "ГОСТ 123"
        });
        Assert.NotEqual(Guid.Empty, created.Id);

        created.Name = $"CRUD ГСМ обновлён {Guid.NewGuid():N}";
        var updated = await s.GsmMaterials.UpdateAsync(created);
        Assert.True(updated);

        var byId = await s.GsmMaterials.GetByIdAsync(created.Id, default);
        Assert.NotNull(byId);
        Assert.Equal(created.Name, byId.Name);

        var deleted = await s.GsmMaterials.DeleteAsync(created.Id);
        Assert.True(deleted);

        var restored = await s.GsmMaterials.RestoreAsync(created.Id);
        Assert.True(restored);
    }

    [Fact]
    public async Task GsmMaterial_IsDeletedFilter_AppliedInDatabase()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var name = $"Filter ГСМ {Guid.NewGuid():N}";
        var created = await s.GsmMaterials.CreateAsync(new GsmMaterial { Name = name, Type = "Дизель" });
        await s.GsmMaterials.DeleteAsync(created.Id);

        var active = await s.GsmMaterials.GetPagedAsync(new GsmMaterialQuery { ShowDeleted = false }, default);
        Assert.DoesNotContain(active.Items, x => x.Id == created.Id);

        var archived = await s.GsmMaterials.GetPagedAsync(new GsmMaterialQuery { ShowDeleted = true }, default);
        Assert.Contains(archived.Items, x => x.Id == created.Id);

        var all = await s.GsmMaterials.GetPagedAsync(new GsmMaterialQuery { ShowDeleted = null, Search = name }, default);
        Assert.Contains(all.Items, x => x.Id == created.Id);
    }

    [Fact]
    public async Task GsmMaterial_ActiveSelection_ExcludesDeletedAndDrafts()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

        var name = $"Active ГСМ {Guid.NewGuid():N}";
        var created = await s.GsmMaterials.CreateAsync(new GsmMaterial { Name = name, Type = "Масло" });
        await s.GsmMaterials.DeleteAsync(created.Id);

        s.Db.GsmMaterials.Add(new GsmMaterial
        {
            Id = Guid.NewGuid(),
            Name = "Черновик ГСМ",
            Type = "Масло",
            IsDraft = true
        });
        await s.Db.SaveChangesAsync();

        var active = await s.GsmMaterials.GetActiveForSelectionAsync(name);
        Assert.DoesNotContain(active, x => x.Id == created.Id);
        Assert.DoesNotContain(active, x => x.IsDraft);
    }
}
