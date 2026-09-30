using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// PR-4 — второй справочник направленных связей марок ГСМ.
/// <para>
/// Схема (частичный UNIQUE активной пары, CHECK-самоссылки, CHECK набора типов,
/// DB-триггеры Foreign с двух сторон) создана в PR-2 и здесь НЕ изменяется:
/// тесты проверяют, что сервис опирается на существующие инварианты, а не
/// обходит их.
/// </para>
/// </summary>
[Collection("Database")]
public class GsmRelationReferenceIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public GsmRelationReferenceIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void AsNormAdmin(TestScope s) => s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);

    // ── 1. Создание каждого типа, направленность, самоссылка ───────────────

    [Theory]
    [InlineData(GsmRelationType.Duplicate)]
    [InlineData(GsmRelationType.Reserve)]
    [InlineData(GsmRelationType.DuplicateAndReserve)]
    [InlineData(GsmRelationType.Foreign)]
    public async Task Create_EachRelationType_PersistsFieldsAndIsActive(GsmRelationType type)
    {
        // Для Foreign связанная марка обязана быть вне номенклатуры по ГОСТ.
        var a = await CreateMaterialAsync("Основная " + Suffix());
        var b = await CreateMaterialAsync("Связанная " + Suffix(), inGost: false);

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var view = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = type,
            Note = $"Примечание связи {type}",
        });

        Assert.NotEqual(Guid.Empty, view.Id);
        Assert.Equal(type, view.RelationType);
        Assert.Equal($"Примечание связи {type}", view.Note);
        Assert.False(view.IsDeleted);

        // Запись читается обратно из БД: имя марки и примечание связи сохранены.
        var reread = await s.GsmMaterials.GetRelationEditViewAsync(view.Id);
        Assert.NotNull(reread);
        Assert.Equal(a, reread!.PrimaryGsmMaterialId);
        Assert.Equal(b, reread.RelatedGsmMaterialId);
        Assert.Equal($"Примечание связи {type}", reread.Note);
    }

    [Fact]
    public async Task DirectedPair_ReverseOrder_IsSeparateAllowedRelation()
    {
        var a = await CreateMaterialAsync("Прямая " + Suffix());
        var b = await CreateMaterialAsync("Обратная " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var forward = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
        });

        // B → A отличается от A → B и разрешена.
        var backward = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = b,
            RelatedGsmMaterialId = a,
            RelationType = GsmRelationType.Duplicate,
        });

        Assert.NotEqual(forward.Id, backward.Id);
        Assert.Equal(a, forward.PrimaryGsmMaterialId);
        Assert.Equal(b, backward.PrimaryGsmMaterialId);
    }

    [Fact]
    public async Task Create_SelfReference_IsRejected()
    {
        var a = await CreateMaterialAsync("Самоссылка " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = a,
                RelationType = GsmRelationType.Duplicate,
            }));

        Assert.Contains("сама с собой", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_UnknownOrDraftOrDeletedMaterial_IsRejected()
    {
        var alive = await CreateMaterialAsync("Живая " + Suffix());
        var deleted = await CreateMaterialAsync("Удалённая " + Suffix());
        var draftId = await CreateDraftMaterialAsync();

        await using (var setup = _fixture.CreateScope())
        {
            setup.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
            Assert.True(await setup.GsmMaterials.DeleteAsync(deleted));
        }

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        // Несуществующий Guid.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = alive,
                RelatedGsmMaterialId = Guid.NewGuid(),
                RelationType = GsmRelationType.Duplicate,
            }));

        // Soft-deleted марка.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = alive,
                RelatedGsmMaterialId = deleted,
                RelationType = GsmRelationType.Duplicate,
            }));

        // Draft-марка.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = alive,
                RelatedGsmMaterialId = draftId,
                RelationType = GsmRelationType.Duplicate,
            }));
    }

    // ── 2. Единственность активной пары ────────────────────────────────────

    [Fact]
    public async Task Create_SecondActivePairWithDifferentType_IsRejected()
    {
        var a = await CreateMaterialAsync("Первая " + Suffix());
        var b = await CreateMaterialAsync("Вторая " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = b,
                RelationType = GsmRelationType.Reserve,
            }));

        Assert.Contains("уже существует", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Активная запись ровно одна.
        await using var check = _fixture.CreateScope();
        Assert.Equal(1, await check.Db.GsmMaterialRelations
            .CountAsync(r => r.PrimaryGsmMaterialId == a && r.RelatedGsmMaterialId == b && !r.IsDeleted));
    }

    [Fact]
    public async Task Update_ChangesTypeOfSingleRecord_KeepsIdAndNote()
    {
        var a = await CreateMaterialAsync("Обновляемая " + Suffix());
        var b = await CreateMaterialAsync("Целевая " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var created = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
            Note = "Примечание сохраняется",
        });

        // Смена типа — обновление одной записи, а не создание второй.
        var updated = await s.GsmMaterials.UpdateRelationAsync(created.Id, new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Reserve,
            Note = "Примечание сохраняется",
        });

        Assert.NotNull(updated);
        Assert.Equal(created.Id, updated!.Id);
        Assert.Equal(GsmRelationType.Reserve, updated.RelationType);
        Assert.Equal("Примечание сохраняется", updated.Note);

        await using var check = _fixture.CreateScope();
        var rows = await check.Db.GsmMaterialRelations
            .AsNoTracking()
            .Where(r => r.PrimaryGsmMaterialId == a && r.RelatedGsmMaterialId == b && !r.IsDeleted)
            .ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task DuplicatePair_BypassingService_IsBlockedByPartialUniqueIndex()
    {
        var a = await CreateMaterialAsync("Обход " + Suffix());
        var b = await CreateMaterialAsync("Обход " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
        });

        // Прямая вставка в обход сервиса: последний рубеж — частичный UNIQUE PR-2.
        await using var raw = _fixture.CreateScope();
        raw.Db.GsmMaterialRelations.Add(new GsmMaterialRelation
        {
            Id = Guid.NewGuid(),
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Reserve,
            IsDeleted = false,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => raw.Db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
    }

    // ── 3. Проекция в первый справочник (только чтение) ────────────────────

    [Fact]
    public async Task FirstReference_DuplicateAndReserve_AppearsInBothColumnsOnce()
    {
        // Уникальные имена: без них нельзя отличить попадание чужой связи.
        var suffix = Suffix();
        var a = await CreateMaterialAsync("Марка A " + suffix);
        var b = await CreateMaterialAsync("Марка B " + suffix);
        var c = await CreateMaterialAsync("Марка C " + suffix);
        var d = await CreateMaterialAsync("Марка D " + suffix);

        string NameOf(Guid id) => id == a ? $"Марка A {suffix}"
            : id == b ? $"Марка B {suffix}"
            : id == c ? $"Марка C {suffix}"
            : $"Марка D {suffix}";

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        // A → B: DuplicateAndReserve — одна запись, видная в обеих колонках A.
        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.DuplicateAndReserve,
        });
        // A → C: только дублирующая.
        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = c,
            RelationType = GsmRelationType.Duplicate,
        });
        // B → A: обратная связь не должна подмешиваться в колонки A.
        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = b,
            RelatedGsmMaterialId = a,
            RelationType = GsmRelationType.Reserve,
        });
        // D → A: тоже не относится к исходящим связям A.
        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = d,
            RelatedGsmMaterialId = a,
            RelationType = GsmRelationType.Duplicate,
        });

        var row = await LoadSummaryAsync(s, a);

        Assert.Equal(
            new[] { NameOf(b), NameOf(c) }.OrderBy(n => n, StringComparer.Ordinal),
            row.DuplicateNames.OrderBy(n => n, StringComparer.Ordinal));

        // DuplicateAndReserve попадает и в резервные, ровно один раз.
        Assert.Equal(
            new[] { NameOf(b) }.OrderBy(n => n, StringComparer.Ordinal),
            row.ReserveNames.OrderBy(n => n, StringComparer.Ordinal));

        Assert.Empty(row.ForeignNames);
    }

    [Fact]
    public async Task FirstReference_Foreign_AppearsOnlyInThirdColumn()
    {
        var a = await CreateMaterialAsync("Зарубежная " + Suffix());
        var b = await CreateMaterialAsync("Зарубежный аналог " + Suffix(), inGost: false);

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Foreign,
        });

        var row = await LoadSummaryAsync(s, a);
        Assert.Single(row.ForeignNames);
        Assert.Empty(row.DuplicateNames);
        Assert.Empty(row.ReserveNames);
    }

    [Fact]
    public async Task FirstReference_SoftDeletedRelation_NotShownAsActive()
    {
        var a = await CreateMaterialAsync("Удаляемая связь " + Suffix());
        var b = await CreateMaterialAsync("Удаляемая связь " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var relation = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
        });

        Assert.Single((await LoadSummaryAsync(s, a)).DuplicateNames);

        Assert.True(await s.GsmMaterials.DeleteRelationAsync(relation.Id));

        var after = await LoadSummaryAsync(s, a);
        Assert.Empty(after.DuplicateNames);
        Assert.Empty(after.ReserveNames);
        Assert.Empty(after.ForeignNames);
    }

    // ── 4. Правило Foreign с обеих сторон ──────────────────────────────────

    [Fact]
    public async Task Foreign_IsRejected_WhenRelatedMaterialIsInGostNomenclature()
    {
        var a = await CreateMaterialAsync("Основная " + Suffix());
        var b = await CreateMaterialAsync("В номенклатуре " + Suffix(), inGost: true);

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = b,
                RelationType = GsmRelationType.Foreign,
            }));

        Assert.Contains("номенклатур", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Роли не перепутаны: A в номенклатуру не включена, а отказ вызван B.
        Assert.True(await s.Db.GsmMaterials.AsNoTracking().AnyAsync(m => m.Id == b && m.InGostNomenclature));
    }

    [Fact]
    public async Task Foreign_Allowed_WhenRelatedMaterialNotInGostNomenclature_AndBlocksLaterInclusion()
    {
        var a = await CreateMaterialAsync("Основная " + Suffix());
        var b = await CreateMaterialAsync("Не в номенклатуре " + Suffix(), inGost: false);

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var relation = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Foreign,
        });

        // Позднее включение B в номенклатуру по ГОСТ отклоняется сервисом.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.UpdateAsync(b, new GsmMaterialWriteRequest
            {
                Name = "Не в номенклатуре " + Suffix(),
                InGostNomenclature = true,
                GroupName = "Группа",
                SubgroupNames = new List<string> { "Подгруппа" },
            }));

        Assert.Contains("зарубежным аналогом", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Удаление Foreign-связи снимает запрет.
        Assert.True(await s.GsmMaterials.DeleteRelationAsync(relation.Id));

        var updated = await s.GsmMaterials.UpdateAsync(b, new GsmMaterialWriteRequest
        {
            Name = "Теперь в номенклатуре " + Suffix(),
            InGostNomenclature = true,
            GroupName = "Группа",
            SubgroupNames = new List<string> { "Подгруппа" },
        });
        Assert.NotNull(updated);
    }

    [Fact]
    public async Task Foreign_IsRejected_ByDatabaseTrigger_WhenServiceIsBypassed()
    {
        var a = await CreateMaterialAsync("Триггер " + Suffix());
        var b = await CreateMaterialAsync("Триггер " + Suffix(), inGost: true);

        await using var raw = _fixture.CreateScope();
        raw.Db.GsmMaterialRelations.Add(new GsmMaterialRelation
        {
            Id = Guid.NewGuid(),
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Foreign,
            IsDeleted = false,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => raw.Db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, pg.SqlState);
    }

    [Fact]
    public async Task Update_ChangingTypeToForeign_IsValidated()
    {
        var a = await CreateMaterialAsync("Смена типа " + Suffix());
        var b = await CreateMaterialAsync("Смена типа " + Suffix(), inGost: true);

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var relation = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.UpdateRelationAsync(relation.Id, new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = b,
                RelationType = GsmRelationType.Foreign,
            }));

        // Тип не изменился.
        var reread = await s.GsmMaterials.GetRelationEditViewAsync(relation.Id);
        Assert.Equal(GsmRelationType.Duplicate, reread!.RelationType);
    }

    // ── 5. Soft-delete связи ───────────────────────────────────────────────

    [Fact]
    public async Task DeleteRelation_IsSoft_AndPairCanBeRecreated()
    {
        var a = await CreateMaterialAsync("Пересоздание " + Suffix());
        var b = await CreateMaterialAsync("Пересоздание " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var first = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
        });

        Assert.True(await s.GsmMaterials.DeleteRelationAsync(first.Id));

        // Строка физически сохранена, помечена удалённой.
        await using var raw = _fixture.CreateScope();
        var stored = await raw.Db.GsmMaterialRelations.AsNoTracking().FirstAsync(r => r.Id == first.Id);
        Assert.True(stored.IsDeleted);

        // Повторное удаление идемпотентно.
        Assert.False(await s.GsmMaterials.DeleteRelationAsync(first.Id));

        // Пару можно создать заново с новым Guid.
        var second = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Reserve,
        });
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task DeleteRelation_UnknownId_ReturnsFalse()
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        Assert.False(await s.GsmMaterials.DeleteRelationAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task UpdateRelation_DeletedRelation_IsRejected()
    {
        var a = await CreateMaterialAsync("Удалённая правка " + Suffix());
        var b = await CreateMaterialAsync("Удалённая правка " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var relation = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
        });
        Assert.True(await s.GsmMaterials.DeleteRelationAsync(relation.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.UpdateRelationAsync(relation.Id, new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = b,
                RelationType = GsmRelationType.Reserve,
            }));
    }

    // ── 6. Soft-delete марки при активной связи ───────────────────────────

    [Theory]
    [InlineData(true)]  // марка A — Primary
    [InlineData(false)] // марка A — Related
    public async Task DeleteMaterial_ParticipatingInActiveRelation_IsRejected(bool asPrimary)
    {
        var a = await CreateMaterialAsync("Марка A " + Suffix());
        var b = await CreateMaterialAsync("Марка B " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = asPrimary ? a : b,
            RelatedGsmMaterialId = asPrimary ? b : a,
            RelationType = GsmRelationType.Duplicate,
        });

        // Ни у A, ни у B нет ссылок из ХК — запрет всё равно действует.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => s.GsmMaterials.DeleteAsync(a));
        Assert.Contains("связ", ex.Message, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.GsmMaterials.DeleteAsync(b));
    }

    [Fact]
    public async Task DeleteMaterial_WithOnlySoftDeletedRelation_IsAllowed()
    {
        var a = await CreateMaterialAsync("Освободившаяся " + Suffix());
        var b = await CreateMaterialAsync("Освободившаяся " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var relation = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
        });
        Assert.True(await s.GsmMaterials.DeleteRelationAsync(relation.Id));

        Assert.True(await s.GsmMaterials.DeleteAsync(a));
    }

    // ── 9. Связи не трогают строки ХК ─────────────────────────────────────

    [Fact]
    public async Task RelationLifecycle_DoesNotChangeHkItemMaterialsOrSnapshots()
    {
        var a = await CreateMaterialAsync("ХК стабильность " + Suffix());
        var b = await CreateMaterialAsync("ХК стабильность " + Suffix());
        var (nodeId, unitId) = await CreateNodeAndUnitAsync();

        Guid cardId;
        await using (var setup = _fixture.CreateScope())
        {
            AsNormAdmin(setup);
            cardId = (await setup.HK.CreateAsync(new HKCard
            {
                ObjectLevel = HKObjectLevel.Node,
                NodeId = nodeId,
                Purpose = "Проверка неизменности",
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
                        Materials = new List<HKCardItemMaterial>
                        {
                            new() { Id = Guid.NewGuid(), GsmMaterialId = a, Category = GsmCategory.Primary },
                        },
                    },
                },
            })).Id;
        }

        Guid materialIdBefore;
        long snapshotsBefore;
        await using (var read = _fixture.CreateScope())
        {
            var row = await read.Db.HKCardItemMaterials.AsNoTracking()
                .SingleAsync(r => r.GsmMaterialId == a);
            materialIdBefore = row.Id;
            snapshotsBefore = await read.Db.IndividualCardItemMaterialSnapshots
                .AsNoTracking().CountAsync();
        }

        await using (var s = _fixture.CreateScope())
        {
            AsNormAdmin(s);
            var relation = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = b,
                RelationType = GsmRelationType.Duplicate,
            });
            await s.GsmMaterials.UpdateRelationAsync(relation.Id, new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = b,
                RelationType = GsmRelationType.Reserve,
            });
            Assert.True(await s.GsmMaterials.DeleteRelationAsync(relation.Id));
        }

        await using (var verify = _fixture.CreateScope())
        {
            var rows = await verify.Db.HKCardItemMaterials.AsNoTracking()
                .Where(r => r.GsmMaterialId == a || r.GsmMaterialId == b)
                .ToListAsync();

            // Строка ХК не изменилась: тот же Id, тот же материал, тот же статус.
            var row = Assert.Single(rows);
            Assert.Equal(materialIdBefore, row.Id);
            Assert.Equal(a, row.GsmMaterialId);
            Assert.Equal(GsmCategory.Primary, row.Category);

            // Снимки ИК не переписаны, Guid марок сохранены.
            Assert.Equal(snapshotsBefore, await verify.Db.IndividualCardItemMaterialSnapshots
                .AsNoTracking().CountAsync());
            Assert.True(await verify.Db.GsmMaterials.AsNoTracking().AnyAsync(m => m.Id == a));
            Assert.True(await verify.Db.GsmMaterials.AsNoTracking().AnyAsync(m => m.Id == b));
        }
    }

    // ── 10. Аудит атомарен ────────────────────────────────────────────────

    [Fact]
    public async Task CreateRelation_WritesAuditAtomically()
    {
        var a = await CreateMaterialAsync("Аудит " + Suffix());
        var b = await CreateMaterialAsync("Аудит " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var relation = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
        });

        var audit = await s.Db.AuditLogs.AsNoTracking()
            .Where(x => x.EntityType == "GsmMaterialRelation" && x.EntityId == relation.Id.ToString())
            .ToListAsync();

        Assert.Contains(audit, x => x.Action == "Create");
    }

    [Fact]
    public async Task FailedCreateRelation_LeavesNoAuditAndNoRelation()
    {
        var a = await CreateMaterialAsync("Откат " + Suffix());
        var b = await CreateMaterialAsync("Откат " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
        });

        var auditsBefore = await CountRelationAuditsAsync();

        // Конфликт пары: ни связи, ни записи аудита.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = b,
                RelationType = GsmRelationType.Reserve,
            }));

        Assert.Equal(auditsBefore, await CountRelationAuditsAsync());
        Assert.Equal(1, await s.Db.GsmMaterialRelations
            .CountAsync(r => r.PrimaryGsmMaterialId == a && r.RelatedGsmMaterialId == b && !r.IsDeleted));
    }

    [Fact]
    public async Task FailedAudit_RollsBackRelationChange()
    {
        var a = await CreateMaterialAsync("Сбой аудита " + Suffix());
        var b = await CreateMaterialAsync("Сбой аудита " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        // Сбой на вставке связи — запись связи и аудита откатываются вместе.
        FailingCommandInterceptor.ArmAt("INSERT INTO \"GsmMaterialRelations\"", occurrence: 1);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
                {
                    PrimaryGsmMaterialId = a,
                    RelatedGsmMaterialId = b,
                    RelationType = GsmRelationType.Duplicate,
                }));
            Assert.True(FailingCommandInterceptor.Fired, "контролируемый сбой не сработал");
        }
        finally
        {
            FailingCommandInterceptor.Disarm();
        }

        await using var check = _fixture.CreateScope();
        Assert.Equal(0, await check.Db.GsmMaterialRelations
            .CountAsync(r => r.PrimaryGsmMaterialId == a && r.RelatedGsmMaterialId == b));
        Assert.Equal(0, await check.Db.AuditLogs
            .CountAsync(x => x.EntityType == "GsmMaterialRelation"
                && x.EntityDisplayName != null && x.EntityDisplayName.Contains(a.ToString())));
    }

    // ── 11. Выбор марки для связи ─────────────────────────────────────────

    [Fact]
    public async Task RelationSelection_OffersAnySubgroupCount_ButNotDeletedOrDraft()
    {
        var single = await CreateMaterialAsync("Одна подгруппа " + Suffix(), subgroups: new[] { "Подгруппа" });
        var multi = await CreateMaterialAsync("Две подгруппы " + Suffix(), subgroups: new[] { "Альфа", "Бета" });
        var legacy = await CreateLegacyMaterialAsync("Без классификации " + Suffix());
        var deleted = await CreateMaterialAsync("Удалённая выбор " + Suffix());
        var draft = await CreateDraftMaterialAsync();

        await using (var setup = _fixture.CreateScope())
        {
            setup.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
            Assert.True(await setup.GsmMaterials.DeleteAsync(deleted));
        }

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        // Поиск по имени: общая тестовая БД содержит материалы других тестов,
        // поэтому выдача без поиска ограничена и не покрывает нужные марки.
        var names = new[] { single, multi, legacy, deleted, draft };
        var found = new List<GsmRelationMaterialOption>();
        foreach (var id in names)
        {
            // IgnoreQueryFilters: удалённая марка скрыта обычным фильтром, а её
            // имя нужно, чтобы проверить, что в выдачу выбора она не попадает.
            var name = await s.Db.GsmMaterials.AsNoTracking().IgnoreQueryFilters()
                .Where(m => m.Id == id)
                .Select(m => m.Name).FirstAsync();
            found.AddRange(await s.GsmMaterials.GetRelationMaterialOptionsAsync(name));
        }

        var ids = found.Select(m => m.Id).ToHashSet();

        Assert.Contains(single, ids);
        Assert.Contains(multi, ids);
        Assert.Contains(legacy, ids);
        Assert.DoesNotContain(deleted, ids);
        Assert.DoesNotContain(draft, ids);

        // Право создавать ссылку в строке ХК остаётся отдельным правилом.
        var hkOptions = await s.GsmMaterials.GetRelationMaterialOptionsAsync();
        var forHk = await s.GsmMaterials.GetActiveForSelectionAsync();
        var hkIds = forHk.Select(m => m.Id).ToHashSet();
        Assert.NotNull(hkOptions);
        Assert.Contains(single, hkIds);
        Assert.DoesNotContain(multi, hkIds);
        Assert.DoesNotContain(legacy, hkIds);
    }

    [Fact]
    public async Task RelationSelection_IsBoundedAndSearchable()
    {
        for (var i = 0; i < 3; i++)
            await CreateMaterialAsync($"Поиск связи {i} {Suffix()}");

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var all = await s.GsmMaterials.GetSelectableForRelationAsync();
        Assert.True(all.Count <= 200, "выдача справочника выбора не ограничена");

        var found = await s.GsmMaterials.GetSelectableForRelationAsync("Поиск связи");
        Assert.NotEmpty(found);
    }

    // ── Чтение второго справочника ────────────────────────────────────────

    [Fact]
    public async Task RelationList_PaginatesAndFiltersByTypeAndSearch()
    {
        var a = await CreateMaterialAsync("Список " + Suffix());
        var b = await CreateMaterialAsync("Список " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Reserve,
            Note = "Заметка для поиска",
        });

        var byType = await s.GsmMaterials.GetRelationsPagedAsync(new GsmRelationQuery
        {
            RelationType = GsmRelationType.Reserve,
            Search = "Заметка для поиска",
        });

        Assert.Equal(1, byType.TotalCount);
        var row = Assert.Single(byType.Items);
        Assert.Equal(a, row.PrimaryGsmMaterialId);
        Assert.Equal(b, row.RelatedGsmMaterialId);
        Assert.Equal("Заметка для поиска", row.Note);
        Assert.False(row.IsDeleted);
        Assert.True(row.PrimaryName.StartsWith("Список", StringComparison.Ordinal));

        // Фильтр по другому типу ничего не возвращает.
        var byOther = await s.GsmMaterials.GetRelationsPagedAsync(new GsmRelationQuery
        {
            RelationType = GsmRelationType.Duplicate,
            Search = "Заметка для поиска",
        });
        Assert.Equal(0, byOther.TotalCount);

        // Фильтр по основной марке.
        var byPrimary = await s.GsmMaterials.GetRelationsPagedAsync(new GsmRelationQuery
        {
            PrimaryGsmMaterialId = a,
        });
        Assert.Equal(1, byPrimary.TotalCount);
    }

    [Fact]
    public async Task RelationList_ShowDeleted_ExposesRemovedRelationWithMaterialNames()
    {
        var a = await CreateMaterialAsync("История " + Suffix());
        var b = await CreateMaterialAsync("История " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var relation = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
        });
        Assert.True(await s.GsmMaterials.DeleteRelationAsync(relation.Id));

        // Общая БД накапливает связи других тестов, поэтому выборка ограничена
        // основной маркой этой пары.
        var active = await s.GsmMaterials.GetRelationsPagedAsync(new GsmRelationQuery
        {
            PrimaryGsmMaterialId = a,
        });
        Assert.Equal(0, active.TotalCount);

        // История удалённых: имена марок читаются корректно.
        var history = await s.GsmMaterials.GetRelationsPagedAsync(new GsmRelationQuery
        {
            ShowDeleted = true,
            PrimaryGsmMaterialId = a,
        });

        var row = Assert.Single(history.Items);
        Assert.True(row.IsDeleted);
        Assert.StartsWith("История", row.PrimaryName, StringComparison.Ordinal);
        Assert.StartsWith("История", row.RelatedName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelationList_SoftDeletedMaterialNamesStillShown()
    {
        // Имена марок в истории остаются корректными, даже если марки удалены,
        // но для НОВОЙ связи такие марки не предлагаются (проверено выше).
        var a = await CreateMaterialAsync("Скрытая " + Suffix());
        var b = await CreateMaterialAsync("Скрытая " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
        });

        // Обходим запрет soft-delete напрямую в БД, чтобы проверить отображение.
        await using (var raw = _fixture.CreateScope())
        {
            await raw.Db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE ""GsmMaterials"" SET ""IsDeleted"" = true WHERE ""Id"" = {b}");
        }

        var list = await s.GsmMaterials.GetRelationsPagedAsync(new GsmRelationQuery
        {
            PrimaryGsmMaterialId = a,
        });
        var row = Assert.Single(list.Items);
        Assert.StartsWith("Скрытая", row.RelatedName, StringComparison.Ordinal);
        Assert.True(row.RelatedIsDeleted);
    }

    // ── 8. Права ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Operator_WithoutReferenceEdit_CannotWriteRelations_ButCanRead()
    {
        var a = await CreateMaterialAsync("Права " + Suffix());
        var b = await CreateMaterialAsync("Права " + Suffix());

        await using var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = b,
                RelationType = GsmRelationType.Duplicate,
            }));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.GsmMaterials.DeleteRelationAsync(Guid.NewGuid()));

        // Чтение обоих справочников разрешено.
        Assert.NotNull(await s.GsmMaterials.GetRelationsPagedAsync(new GsmRelationQuery()));
        Assert.NotNull(await s.GsmMaterials.GetSelectableForRelationAsync());
    }

    [Fact]
    public async Task NormAdmin_CanCrudRelations()
    {
        var a = await CreateMaterialAsync("CRUD связи " + Suffix());
        var b = await CreateMaterialAsync("CRUD связи " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var created = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
            Note = "CRUD",
        });
        Assert.NotEqual(Guid.Empty, created.Id);

        var updated = await s.GsmMaterials.UpdateRelationAsync(created.Id, new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.DuplicateAndReserve,
            Note = "CRUD обновлено",
        });
        Assert.NotNull(updated);
        Assert.Equal("CRUD обновлено", updated!.Note);

        Assert.True(await s.GsmMaterials.DeleteRelationAsync(created.Id));
    }

    [Fact]
    public async Task UpdateRelation_UnknownId_ReturnsNull()
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var result = await s.GsmMaterials.UpdateRelationAsync(Guid.NewGuid(), new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = Guid.NewGuid(),
            RelatedGsmMaterialId = Guid.NewGuid(),
            RelationType = GsmRelationType.Duplicate,
        });
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateRelation_ChangingEnds_AppliesSamePairAndForeignChecks()
    {
        var a = await CreateMaterialAsync("Концы A " + Suffix());
        var b = await CreateMaterialAsync("Концы B " + Suffix());
        var c = await CreateMaterialAsync("Концы C " + Suffix(), inGost: true);

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var relation = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
        });

        // Новая пара A → C уже занята? Нет — но C в номенклатуре, а тип Foreign.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.UpdateRelationAsync(relation.Id, new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = c,
                RelationType = GsmRelationType.Foreign,
            }));

        // Смена концов на свободную пару проходит и не трогает строки ХК.
        var moved = await s.GsmMaterials.UpdateRelationAsync(relation.Id, new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = b,
            RelatedGsmMaterialId = a,
            RelationType = GsmRelationType.Reserve,
        });
        Assert.NotNull(moved);
        Assert.Equal(b, moved!.PrimaryGsmMaterialId);
        Assert.Equal(a, moved.RelatedGsmMaterialId);
    }

    [Fact]
    public async Task UpdateRelation_TargetPairAlreadyActive_IsRejected()
    {
        var a = await CreateMaterialAsync("Занятая " + Suffix());
        var b = await CreateMaterialAsync("Занятая " + Suffix());
        var c = await CreateMaterialAsync("Занятая " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
        });
        var moving = await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = b,
            RelatedGsmMaterialId = c,
            RelationType = GsmRelationType.Duplicate,
        });

        // Попытка перевести moving на уже занятую пару A → C.
        var free = await CreateMaterialAsync("Занятая " + Suffix());
        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = free,
            RelationType = GsmRelationType.Duplicate,
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.UpdateRelationAsync(moving.Id, new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = free,
                RelationType = GsmRelationType.Duplicate,
            }));
        Assert.Contains("уже существует", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RelationNote_MaxLengthIsValidated()
    {
        var a = await CreateMaterialAsync("Длинное примечание " + Suffix());
        var b = await CreateMaterialAsync("Длинное примечание " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
            {
                PrimaryGsmMaterialId = a,
                RelatedGsmMaterialId = b,
                RelationType = GsmRelationType.Duplicate,
                Note = new string('x', 1001),
            }));
        Assert.Contains("1000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelationNote_BelongsToRelation_AndIsNotMaterialNote()
    {
        var a = await CreateMaterialAsync("Разделение примечаний " + Suffix());
        var b = await CreateMaterialAsync("Разделение примечаний " + Suffix());

        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);

        await s.GsmMaterials.CreateRelationAsync(new GsmRelationWriteRequest
        {
            PrimaryGsmMaterialId = a,
            RelatedGsmMaterialId = b,
            RelationType = GsmRelationType.Duplicate,
            Note = "Примечание связи",
        });

        // Примечание марки не затрагивается и наоборот.
        var material = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == a);
        Assert.NotEqual("Примечание связи", material.Note);

        var relation = await s.Db.GsmMaterialRelations.AsNoTracking()
            .FirstAsync(r => r.PrimaryGsmMaterialId == a && r.RelatedGsmMaterialId == b);
        Assert.Equal("Примечание связи", relation.Note);
    }

    // ── Фикстуры ──────────────────────────────────────────────────────────

    /// <summary>
    /// Строка первого справочника для марки. Поиск идёт по имени: общая тестовая
    /// БД накапливает материалы других тестов, и опора на «первые 200 строк» была
    /// бы нестабильной. <c>ShowDeleted = null</c> означает «все марки, включая
    /// удалённые»: при <c>true</c> фильтр оставляет ТОЛЬКО удалённые.
    /// </summary>
    private async Task<GsmMaterialSummary> LoadSummaryAsync(TestScope s, Guid materialId)
    {
        var material = await s.Db.GsmMaterials.AsNoTracking().FirstAsync(m => m.Id == materialId);

        var result = await s.GsmMaterials.GetPagedAsync(new GsmMaterialQuery
        {
            PageSize = 200,
            ShowDeleted = null,
            Search = material.Name,
        });

        var row = result.Items.FirstOrDefault(m => m.Id == materialId);
        Assert.NotNull(row);
        return row!;
    }

    private async Task<int> CountRelationAuditsAsync()
    {
        await using var s = _fixture.CreateScope();
        return await s.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.EntityType == "GsmMaterialRelation");
    }

    private async Task<Guid> CreateMaterialAsync(
        string name, bool inGost = false, string[]? subgroups = null)
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var view = await s.GsmMaterials.CreateAsync(new GsmMaterialWriteRequest
        {
            Name = name,
            InGostNomenclature = inGost,
            GroupName = "Группа " + Suffix(),
            SubgroupNames = (subgroups ?? new[] { "Подгруппа" }).ToList(),
        });
        return view.Id;
    }

    private async Task<Guid> CreateLegacyMaterialAsync(string name)
    {
        var id = await CreateMaterialAsync(name);
        await using var s = _fixture.CreateScope();
        var rows = await s.Db.GsmMaterialClassifications.Where(c => c.GsmMaterialId == id).ToListAsync();
        s.Db.GsmMaterialClassifications.RemoveRange(rows);
        await s.Db.SaveChangesAsync();
        return id;
    }

    /// <summary>Черновик предложения: опубликованной марки не является.</summary>
    private async Task<Guid> CreateDraftMaterialAsync()
    {
        await using var s = _fixture.CreateScope();
        AsNormAdmin(s);
        var id = Guid.NewGuid();
        s.Db.GsmMaterials.Add(new GsmMaterial
        {
            Id = id,
            Name = "Черновик " + Suffix(),
            Type = string.Empty,
            IsDeleted = false,
            IsDraft = true,
        });
        await s.Db.SaveChangesAsync();
        return id;
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
}
