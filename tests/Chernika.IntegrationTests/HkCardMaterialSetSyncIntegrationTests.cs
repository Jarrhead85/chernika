using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Gate 13 — сохранение набора материалов в <c>HKCardService.UpdateAsync</c>.
///
/// Сценарий воспроизводит путь <c>HKEdit.razor</c>: карточка загружена тем же
/// <c>AppDbContext</c>, которым затем вызывается сервис. Входной граф уже
/// отслеживается EF, поэтому сервис не может считать его независимым от
/// сохранённого состояния.
///
/// До правки сценарий 1 падал с <c>DbUpdateConcurrencyException</c>, а набор
/// материалов пересоздавался целиком (новые <c>Guid</c> у каждой строки).
/// </summary>
[Collection("Database")]
public class HkCardMaterialSetSyncIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HkCardMaterialSetSyncIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void AsNormAdmin(TestScope s) => s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

    // ── 1. Текстовая правка карточки ──────────────────────────────────────

    [Fact]
    public async Task TextOnlyEdit_WithTrackedCard_PersistsAndReadsBack()
    {
        var materialId = await CreateMaterialAsync("Текст " + Suffix(), "Подгруппа");
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();
        var cardId = await CreateCardAsync(nodeId, unitId, materialId);

        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            var card = await s.HK.GetByIdAsync(cardId);
            Assert.NotNull(card);

            card!.Notes = "Правка примечания";
            card.Purpose = "Уточнённое назначение";

            var updated = await s.HK.UpdateAsync(card);
            Assert.NotNull(updated);
        }

        // Значения читаются в новом scope: запись действительно дошла до БД.
        await using (var s2 = _fixture.CreateScope())
        {
            var saved = await s2.HK.GetByIdAsync(cardId);
            Assert.NotNull(saved);
            Assert.Equal("Правка примечания", saved!.Notes);
            Assert.Equal("Уточнённое назначение", saved.Purpose);
        }
    }

    // ── 2. Повторное сохранение без изменений ──────────────────────────────

    [Fact]
    public async Task NoOpResave_KeepsMaterialRowIdentity_AndDoesNotDuplicate()
    {
        var materialId = await CreateMaterialAsync("Нет изменений " + Suffix(), "Подгруппа");
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();
        var cardId = await CreateCardAsync(nodeId, unitId, materialId);

        Guid originalRowId;
        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            var card = await s.HK.GetByIdAsync(cardId);
            originalRowId = card!.Items.Single().Materials.Single().Id;

            await s.HK.UpdateAsync(card);
        }

        await using (var s2 = _fixture.CreateScope())
        {
            var rows = await s2.Db.HKCardItemMaterials.AsNoTracking()
                .Where(m => m.GsmMaterialId == materialId)
                .ToListAsync();

            // Строка не пересоздана и не задвоена.
            var row = Assert.Single(rows);
            Assert.Equal(originalRowId, row.Id);
        }
    }

    // ── 3. Добавление, удаление и изменение материала ──────────────────────

    [Fact]
    public async Task MaterialSetChange_ProducesExactlyDesiredRows_AndKeepsUntouchedIdentity()
    {
        var keptId = await CreateMaterialAsync("Сохраняемая " + Suffix(), "Подгруппа");
        var droppedId = await CreateMaterialAsync("Удаляемая " + Suffix(), "Подгруппа");
        var addedId = await CreateMaterialAsync("Добавляемая " + Suffix(), "Подгруппа");

        var (nodeId, unitId) = await CreateNodeAndUnitAsync();
        var cardId = await CreateCardAsync(nodeId, unitId, keptId, droppedId);

        Guid keptRowId;
        Guid itemId;
        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            var card = await s.HK.GetByIdAsync(cardId);
            var item = card!.Items.Single();
            itemId = item.Id;
            keptRowId = item.Materials.Single(m => m.GsmMaterialId == keptId).Id;

            // Удаляем одну строку материала, добавляем другую — в том же
            // отслеживаемом графе, как это делает UI.
            var toRemove = item.Materials.Single(m => m.GsmMaterialId == droppedId);
            item.Materials.Remove(toRemove);
            item.Materials.Add(new HKCardItemMaterial
            {
                Id = Guid.NewGuid(),
                HKCardItemId = item.Id,
                GsmMaterialId = addedId,
                Category = GsmCategory.Primary,
            });

            await s.HK.UpdateAsync(card);
        }

        await using (var s2 = _fixture.CreateScope())
        {
            var rows = await s2.Db.HKCardItemMaterials.AsNoTracking()
                .Where(m => m.HKCardItemId == itemId)
                .ToListAsync();

            Assert.Equal(2, rows.Count);

            // Неизменённая строка сохранила идентичность.
            var kept = Assert.Single(rows, r => r.GsmMaterialId == keptId);
            Assert.Equal(keptRowId, kept.Id);

            // Удалённой строки нет, новая присутствует ровно один раз.
            Assert.DoesNotContain(rows, r => r.GsmMaterialId == droppedId);
            Assert.Single(rows, r => r.GsmMaterialId == addedId);
        }
    }

    [Fact]
    public async Task MaterialCategoryChange_IsPersistedWithoutRecreatingRow()
    {
        var materialId = await CreateMaterialAsync("Категория " + Suffix(), "Подгруппа");
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();
        var cardId = await CreateCardAsync(nodeId, unitId, materialId);

        Guid rowId;
        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            var card = await s.HK.GetByIdAsync(cardId);
            var row = card!.Items.Single().Materials.Single();
            rowId = row.Id;
            row.Category = GsmCategory.Reserve;

            await s.HK.UpdateAsync(card);
        }

        await using (var s2 = _fixture.CreateScope())
        {
            var saved = await s2.Db.HKCardItemMaterials.AsNoTracking()
                .SingleAsync(m => m.Id == rowId);

            Assert.Equal(GsmCategory.Reserve, saved.Category);
        }
    }

    [Fact]
    public async Task MaterialReplacement_ChangesGsmMaterial_KeepingRowIdentity()
    {
        var originalId = await CreateMaterialAsync("Исходная " + Suffix(), "Подгруппа");
        var replacementId = await CreateMaterialAsync("Замена " + Suffix(), "Подгруппа");
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();
        var cardId = await CreateCardAsync(nodeId, unitId, originalId);

        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            var card = await s.HK.GetByIdAsync(cardId);
            var row = card!.Items.Single().Materials.Single();
            row.GsmMaterialId = replacementId;

            await s.HK.UpdateAsync(card);
        }

        await using (var s2 = _fixture.CreateScope())
        {
            var rows = await s2.Db.HKCardItemMaterials.AsNoTracking()
                .Where(m => m.GsmMaterialId == originalId || m.GsmMaterialId == replacementId)
                .ToListAsync();

            var row = Assert.Single(rows);
            Assert.Equal(replacementId, row.GsmMaterialId);
        }
    }

    // ── 4. Несколько строк и материалов: атомарность и аудит ──────────────

    [Fact]
    public async Task MultipleItemsAndMaterials_SaveWithoutGraphConflicts_AndKeepAudit()
    {
        var m1 = await CreateMaterialAsync("Первая " + Suffix(), "Подгруппа");
        var m2 = await CreateMaterialAsync("Вторая " + Suffix(), "Подгруппа");
        var m3 = await CreateMaterialAsync("Третья " + Suffix(), "Подгруппа");

        var (nodeId, unitId) = await CreateNodeAndUnitAsync();
        var cardId = await CreateCardAsync(nodeId, unitId, m1, m2);

        // Вторая строка ХК добавляется в том же отслеживаемом графе.
        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            var card = await s.HK.GetByIdAsync(cardId);
            card!.Items.Add(new HKCardItem
            {
                Id = Guid.NewGuid(),
                HKCardId = card.Id,
                AssemblyUnitId = unitId,
                Quantity = 2,
                Volume = 5m,
                UnitOfMeasure = "л",
                SortOrder = 2,
                Materials = new List<HKCardItemMaterial>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        GsmMaterialId = m3,
                        Category = GsmCategory.Primary,
                    },
                },
            });

            await s.HK.UpdateAsync(card);
        }

        await using (var s2 = _fixture.CreateScope())
        {
            var card = await s2.HK.GetByIdAsync(cardId);
            Assert.NotNull(card);
            Assert.Equal(2, card!.Items.Count);
            Assert.Equal(3, card.Items.SelectMany(i => i.Materials).Count());

            // Аудит: создание карточки плюс обновление при пересохранении.
            var audit = await s2.Db.AuditLogs.AsNoTracking()
                .Where(a => a.EntityType == "HKCard" && a.EntityId == cardId.ToString())
                .ToListAsync();
            Assert.Contains(audit, a => a.Action == "Updated");
            Assert.Contains(audit, a => a.Action == "Created");
        }
    }

    [Fact]
    public async Task FailedSave_DoesNotLeavePartialMaterialSet()
    {
        var keptId = await CreateMaterialAsync("Остаётся " + Suffix(), "Подгруппа");
        var addedId = await CreateMaterialAsync("Добавляется " + Suffix(), "Подгруппа");
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();
        var cardId = await CreateCardAsync(nodeId, unitId, keptId);

        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            var card = await s.HK.GetByIdAsync(cardId);
            card!.Items.Single().Materials.Add(new HKCardItemMaterial
            {
                Id = Guid.NewGuid(),
                HKCardItemId = card.Items.Single().Id,
                GsmMaterialId = addedId,
                Category = GsmCategory.Primary,
            });

            FailingCommandInterceptor.ArmAt("UPDATE \"HKCards\"", occurrence: 1);
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() => s.HK.UpdateAsync(card));
                Assert.True(FailingCommandInterceptor.Fired, "контролируемый сбой не сработал");
            }
            finally
            {
                FailingCommandInterceptor.Disarm();
            }
        }

        // Ни добавленная строка, ни удаление прежних не должны были остаться.
        await using (var s2 = _fixture.CreateScope())
        {
            var rows = await s2.Db.HKCardItemMaterials.AsNoTracking()
                .Where(m => m.GsmMaterialId == keptId || m.GsmMaterialId == addedId)
                .ToListAsync();

            var row = Assert.Single(rows);
            Assert.Equal(keptId, row.GsmMaterialId);
        }
    }

    // ── 5. Detached вход ──────────────────────────────────────────────────

    [Fact]
    public async Task DetachedInput_GivesSameResult_AndCreatesRowsForNewMaterials()
    {
        var keptId = await CreateMaterialAsync("Detached " + Suffix(), "Подгруппа");
        var addedId = await CreateMaterialAsync("Detached добав " + Suffix(), "Подгруппа");
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();
        var cardId = await CreateCardAsync(nodeId, unitId, keptId);

        // Detached-граф собирается вручную, как из другого контекста.
        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            var source = await s.HK.GetByIdAsync(cardId);
            s.Db.ChangeTracker.Clear();

            var detached = new HKCard
            {
                Id = source!.Id,
                RowVersion = source.RowVersion,
                ObjectLevel = source.ObjectLevel,
                NodeId = source.NodeId,
                Purpose = source.Purpose,
                NormativeBasis = source.NormativeBasis,
                Items = source.Items.Select(i => new HKCardItem
                {
                    Id = i.Id,
                    HKCardId = i.HKCardId,
                    AssemblyUnitId = i.AssemblyUnitId,
                    Quantity = i.Quantity,
                    Volume = i.Volume,
                    UnitOfMeasure = i.UnitOfMeasure,
                    SortOrder = i.SortOrder,
                    Materials = i.Materials.Select(m => new HKCardItemMaterial
                    {
                        Id = m.Id,
                        HKCardItemId = m.HKCardItemId,
                        GsmMaterialId = m.GsmMaterialId,
                        Category = m.Category,
                    }).ToList(),
                }).ToList(),
            };

            var item = detached.Items.Single();
            item.Materials.Add(new HKCardItemMaterial
            {
                Id = Guid.NewGuid(),
                HKCardItemId = item.Id,
                GsmMaterialId = addedId,
                Category = GsmCategory.Primary,
            });

            await s.HK.UpdateAsync(detached);
        }

        await using (var s2 = _fixture.CreateScope())
        {
            var card = await s2.HK.GetByIdAsync(cardId);
            var rows = card!.Items.SelectMany(i => i.Materials).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.GsmMaterialId == keptId);
            Assert.Contains(rows, r => r.GsmMaterialId == addedId);
        }
    }

    // ── Фикстуры ──────────────────────────────────────────────────────────

    private async Task<Guid> CreateMaterialAsync(string name, string subgroup)
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var view = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = name,
            GroupName = "Группа " + Suffix(),
            SubgroupNames = new List<string> { subgroup },
        });
        return view.Id;
    }

    private async Task<(Guid NodeId, Guid UnitId)> CreateNodeAndUnitAsync()
    {
        await using var s = _fixture.CreateScope();
        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Suffix(), Name = "Узел " + Suffix() };
        var unit = new AssemblyUnit { Id = Guid.NewGuid(), Code = "AU-" + Suffix(), Name = "СЕ " + Suffix() };
        s.Db.Nodes.Add(node);
        s.Db.AssemblyUnits.Add(unit);
        await s.Db.SaveChangesAsync();
        return (node.Id, unit.Id);
    }

    private async Task<Guid> CreateCardAsync(Guid nodeId, Guid unitId, params Guid[] materialIds)
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var created = await s.HK.CreateAsync(new HKCard
        {
            ObjectLevel = HKObjectLevel.Node,
            NodeId = nodeId,
            Purpose = "Gate 13",
            NormativeBasis = "ГОСТ",
            Items = new List<HKCardItem>
            {
                new()
                {
                    AssemblyUnitId = unitId,
                    Quantity = 1,
                    Volume = 1m,
                    UnitOfMeasure = "кг",
                    SortOrder = 1,
                    Materials = materialIds
                        .Select(id => new HKCardItemMaterial
                        {
                            Id = Guid.NewGuid(),
                            GsmMaterialId = id,
                            Category = GsmCategory.Primary,
                        })
                        .ToList(),
                },
            },
        });
        return created.Id;
    }
}
