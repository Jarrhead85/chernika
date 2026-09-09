using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class HKCardCreateIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardCreateIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    [Fact]
    public async Task CreateAsync_SystemAdminWithBranchAndUnspecifiedDates_Succeeds()
    {
        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.SystemAdminUser.Id);

        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Suffix(), Name = "Узел " + Suffix(), IsDeleted = false };
        s.Db.Nodes.Add(node);
        var au = new AssemblyUnit { Id = Guid.NewGuid(), Code = "AU-" + Suffix(), Name = "СЕ " + Suffix(), IsDeleted = false };
        s.Db.AssemblyUnits.Add(au);
        var m1 = new GsmMaterial { Id = Guid.NewGuid(), Name = "M1 " + Suffix(), Type = "Т", IsDeleted = false, IsDraft = false };
        var m2 = new GsmMaterial { Id = Guid.NewGuid(), Name = "M2 " + Suffix(), Type = "Т", IsDeleted = false, IsDraft = false };
        var m3 = new GsmMaterial { Id = Guid.NewGuid(), Name = "M3 " + Suffix(), Type = "Т", IsDeleted = false, IsDraft = false };
        var m4 = new GsmMaterial { Id = Guid.NewGuid(), Name = "M4 " + Suffix(), Type = "Т", IsDeleted = false, IsDraft = false };
        s.Db.GsmMaterials.AddRange(m1, m2, m3, m4);
        await s.Db.SaveChangesAsync();

        var item = new HKCardItem
        {
            Id = Guid.NewGuid(),
            AssemblyUnitId = au.Id,
            Quantity = 0,
            Volume = 100m,
            UnitOfMeasure = "г",
            SortOrder = 1,
        };
        item.Materials.Add(new HKCardItemMaterial { Id = Guid.NewGuid(), HKCardItemId = item.Id, GsmMaterialId = m1.Id, Category = GsmCategory.Primary });
        item.Materials.Add(new HKCardItemMaterial { Id = Guid.NewGuid(), HKCardItemId = item.Id, GsmMaterialId = m2.Id, Category = GsmCategory.Duplicate });
        item.Materials.Add(new HKCardItemMaterial { Id = Guid.NewGuid(), HKCardItemId = item.Id, GsmMaterialId = m3.Id, Category = GsmCategory.Reserve });
        item.Materials.Add(new HKCardItemMaterial { Id = Guid.NewGuid(), HKCardItemId = item.Id, GsmMaterialId = m4.Id, Category = GsmCategory.Foreign });

        var card = new HKCard
        {
            Id = Guid.NewGuid(),
            Code = "DEBUG",
            Version = "v1",
            Status = HKCardStatus.Draft,
            ObjectLevel = HKObjectLevel.Node,
            NodeId = node.Id,
            BranchId = _fixture.BranchA,
            RequestOrganization = "2ЛП",
            RequestSenderFullName = "Анатолий Анатольевич",
            RequestReceivedDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
            IncomingLetterNumber = "123",
            OutgoingLetterNumber = "456",
            RequestDetails = "NB123456",
            Purpose = "Пример",
            Notes = "Пример",
            EffectiveDate = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
            ExpirationDate = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(30), DateTimeKind.Unspecified),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        card.Items.Add(item);

        var exception = await Record.ExceptionAsync(() => s.HK.CreateAsync(card));
        Assert.True(exception is null, exception?.ToString() ?? "no exception");
    }
}
