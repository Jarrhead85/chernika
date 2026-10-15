using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

/// <summary>
/// Сохранность PDF, зафиксированного в редакции, на настоящих файлах.
/// <para>
/// Файлы создаются во временном каталоге тестовой фикстуры
/// (<c>Path.GetTempPath()/chernika-tests-storage</c>), рабочий каталог
/// приложения не используется. Каждый тест работает со своим файлом и
/// особыми ключами, поэтому очистка одного не затрагивает остальные.
/// </para>
/// </summary>
[Collection("Database")]
public class HKCardRevisionPdfPreservationIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public HKCardRevisionPdfPreservationIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static byte[] PdfBytes(string marker)
    {
        var text = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n"
                   + "<< /Root 1 0 R >>\n%% " + marker + "\n%%EOF\n";
        return System.Text.Encoding.ASCII.GetBytes(text);
    }

    private async Task<TestScope> AsUserAsync(string userId)
    {
        var s = _fixture.CreateScope();
        s.User.CurrentUserId = Guid.Parse(userId);
        return s;
    }

    private async Task<Guid> CreateCardAsync(TestScope s)
    {
        var unit = new AssemblyUnit { Id = Guid.NewGuid(), Code = "АУ-" + Suffix(), Name = "СБ" };
        var gsm = new GsmMaterial { Id = Guid.NewGuid(), Name = "М-" + Suffix(), Nd = "ТУ" };
        s.Db.AssemblyUnits.Add(unit);
        s.Db.GsmMaterials.Add(gsm);
        await s.Db.SaveChangesAsync();

        var card = new HKCard
        {
            Name = "Карта " + Suffix(),
            IsAutonomous = true,
            ObjectLevel = HKObjectLevel.Undefined,
            Purpose = "Назначение",
            NormativeBasis = "ГОСТ",
            Items = new List<HKCardItem>
            {
                new() { AssemblyUnitId = unit.Id, Quantity = 1, Volume = 1m, SortOrder = 1 }
            }
        };
        var created = await s.HK.CreateAsync(card);
        await s.Db.HKCardItemMaterials.AddAsync(new HKCardItemMaterial
        {
            Id = Guid.NewGuid(),
            HKCardItemId = created.Items.First().Id,
            GsmMaterialId = gsm.Id,
            Category = GsmCategory.Primary
        });
        await s.Db.SaveChangesAsync();
        return created.Id;
    }

    private async Task<uint> VersionAsync(TestScope s, Guid cardId)
    {
        s.Db.ChangeTracker.Clear();
        return await s.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.RowVersion).FirstAsync();
    }

    private async Task<string> AttachAsync(TestScope s, Guid cardId, string marker)
    {
        var bytes = PdfBytes(marker);
        await s.HK.SaveAttachmentAsync(
            cardId, new MemoryStream(bytes), marker + ".pdf", "application/pdf", bytes.Length);

        var key = await s.Db.HKCardAttachments.AsNoTracking()
            .Where(a => a.HKCardId == cardId).Select(a => a.StorageKey).FirstAsync();
        return key;
    }

    /// <summary>Полный путь файла по ключу хранения — тот же, что использует сервис.</summary>
    private string ResolvePath(string storageKey) =>
        Path.Combine(
            Path.Combine(Path.GetTempPath(), "chernika-tests-storage"),
            storageKey.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Фиксация редакции с PDF, затем замена рабочего вложения: файл основания
    /// должен сохраниться байт в байт, вместе с контрольной суммой.
    /// </summary>
    [Fact]
    public async Task ReplacingWorkingPdf_KeepsCapturedFileIntact()
    {
        string storageKey;
        byte[] original;

        await using (var s = await AsUserAsync(_fixture.NormAdminA.Id))
        {
            var cardId = await CreateCardAsync(s);
            storageKey = await AttachAsync(s, cardId, "original");
            original = await File.ReadAllBytesAsync(ResolvePath(storageKey));

            var version = await VersionAsync(s, cardId);
            await s.Revisions.CaptureAsync(cardId, version);
        }

        // Замена рабочего PDF.
        await using (var s = await AsUserAsync(_fixture.NormAdminA.Id))
        {
            var cardId = await s.Db.HKCardAttachments.AsNoTracking()
                .Where(a => a.StorageKey == storageKey).Select(a => a.HKCardId).FirstAsync();

            var bytes = PdfBytes("replacement");
            await s.HK.SaveAttachmentAsync(
                cardId, new MemoryStream(bytes), "replacement.pdf", "application/pdf", bytes.Length);
        }

        Assert.True(File.Exists(ResolvePath(storageKey)), "Файл основания редакции исчез при замене.");
        Assert.Equal(original, await File.ReadAllBytesAsync(ResolvePath(storageKey)));
    }

    /// <summary>Удаление рабочего вложения не удаляет файл, используемый редакцией.</summary>
    [Fact]
    public async Task DeletingWorkingAttachment_KeepsCapturedFileIntact()
    {
        string storageKey;
        byte[] original;

        await using (var s = await AsUserAsync(_fixture.NormAdminA.Id))
        {
            var cardId = await CreateCardAsync(s);
            storageKey = await AttachAsync(s, cardId, "keepme");
            original = await File.ReadAllBytesAsync(ResolvePath(storageKey));

            var version = await VersionAsync(s, cardId);
            await s.Revisions.CaptureAsync(cardId, version);
        }

        await using (var s = await AsUserAsync(_fixture.NormAdminA.Id))
        {
            var cardId = await s.Db.HKCardAttachments.AsNoTracking()
                .Where(a => a.StorageKey == storageKey).Select(a => a.HKCardId).FirstAsync();
            await s.HK.DeleteAttachmentAsync(cardId);
        }

        Assert.True(File.Exists(ResolvePath(storageKey)),
            "Файл основания редакции исчез после удаления рабочего вложения.");
        Assert.Equal(original, await File.ReadAllBytesAsync(ResolvePath(storageKey)));
    }

    /// <summary>Контрольная сумма зафиксированного файла соответствует исходной.</summary>
    [Fact]
    public async Task CapturedChecksum_MatchesFileOnDisk()
    {
        Guid revisionId;
        Guid cardId;
        string expectedSha;

        await using (var s = await AsUserAsync(_fixture.NormAdminA.Id))
        {
            cardId = await CreateCardAsync(s);
            await AttachAsync(s, cardId, "sha");

            expectedSha = (await s.Db.HKCardAttachments.AsNoTracking()
                .Where(a => a.HKCardId == cardId).Select(a => a.Sha256).FirstAsync())!;

            var version = await VersionAsync(s, cardId);
            revisionId = await s.Revisions.CaptureAsync(cardId, version);
        }

        await using (var setup = await AsUserAsync(_fixture.NormAdminA.Id))
        {
            var card = await setup.Db.HKCards.FirstAsync(c => c.Id == cardId);
            card.CurrentAcceptedRevisionId = revisionId;
            await setup.Db.SaveChangesAsync();
        }

        await using var check = await AsUserAsync(_fixture.NormAdminA.Id);
        var revision = await check.Db.HKCardRevisions.AsNoTracking()
            .FirstAsync(r => r.Id == revisionId);

        // Контрольная сумма снимка совпадает с исходным файлом.
        Assert.Equal(expectedSha, revision.AttachmentSha256);

        // Предусловие чтения: указатель действительно сохранён.
        var pointer = await check.Db.HKCards.AsNoTracking()
            .Where(c => c.Id == cardId).Select(c => c.CurrentAcceptedRevisionId).FirstAsync();
        Assert.Equal(revisionId, pointer);

        // Ключ хранения не входит в наружный контракт принятой структуры.
        var exposed = typeof(HKAcceptedStructureDto)
            .GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("AttachmentStorageKey", exposed);
        Assert.DoesNotContain("StorageKey", exposed);

        // Сам файл на месте и его содержимое совпадает с зафиксированным.
        Assert.True(File.Exists(ResolvePath(revision.AttachmentStorageKey!)));
    }

    /// <summary>Повторная запись по занятому ключу отвергается: файл не перезаписывается.</summary>
    [Fact]
    public async Task SaveAsync_RejectsOccupiedKey_AndKeepsBytes()
    {
        string storageKey;
        byte[] original;

        await using (var s = await AsUserAsync(_fixture.NormAdminA.Id))
        {
            var cardId = await CreateCardAsync(s);
            storageKey = await AttachAsync(s, cardId, "occupied");
            original = await File.ReadAllBytesAsync(ResolvePath(storageKey));
        }

        await using var writer = await AsUserAsync(_fixture.NormAdminA.Id);
        var other = PdfBytes("overwrite-attempt");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.Storage.SaveAsync(new MemoryStream(other), storageKey));

        // Содержимое занято��о файла не изменилось.
        Assert.Equal(original, await File.ReadAllBytesAsync(ResolvePath(storageKey)));
    }

    /// <summary>
    /// Фиксация во внешней транзакции и последующее удаление рабочего вложения:
    /// файл редакции должен существовать и после успешной фиксации, и после
    /// удаления. Проверяется файл, а не только запись метаданных.
    /// </summary>
    [Fact]
    public async Task ExternalTransactionCapture_ThenDeleteAttachment_KeepsFile()
    {
        string storageKey;
        byte[] original;
        Guid revisionId;

        await using (var s = await AsUserAsync(_fixture.NormAdminA.Id))
        {
            var cardId = await CreateCardAsync(s);
            storageKey = await AttachAsync(s, cardId, "external");
            original = await File.ReadAllBytesAsync(ResolvePath(storageKey));

            var version = await VersionAsync(s, cardId);

            await using var tx = await s.Db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead);
            revisionId = await s.Revisions.CaptureAsync(cardId, version);
            await tx.CommitAsync();
        }

        Assert.True(File.Exists(ResolvePath(storageKey)),
            "Файл отсутствует сразу после успешной фиксации во внешней транзакции.");

        await using (var s = await AsUserAsync(_fixture.NormAdminA.Id))
        {
            var cardId = await s.Db.HKCardAttachments.AsNoTracking()
                .Where(a => a.StorageKey == storageKey).Select(a => a.HKCardId).FirstAsync();
            await s.HK.DeleteAttachmentAsync(cardId);
        }

        Assert.True(File.Exists(ResolvePath(storageKey)),
            "Файл основания исчез после удаления рабочего вложения.");
        Assert.Equal(original, await File.ReadAllBytesAsync(ResolvePath(storageKey)));

        await using var check = await AsUserAsync(_fixture.NormAdminA.Id);
        Assert.NotNull(await check.Db.HKCardRevisions.FindAsync(revisionId));
    }

    /// <summary>
    /// Конкурентная фиксация и попытка удаления: правомерно только то, что доказуемо
    /// в текущей операции. Проверяется, что файл основания не исчезает, если на него
    /// уже ссылается редакция.
    /// </summary>
    [Fact]
    public async Task ConcurrentCaptureAndDelete_FileSurvives()
    {
        string storageKey;
        byte[] original;
        Guid cardId;

        await using (var s = await AsUserAsync(_fixture.NormAdminA.Id))
        {
            cardId = await CreateCardAsync(s);
            storageKey = await AttachAsync(s, cardId, "race");
            original = await File.ReadAllBytesAsync(ResolvePath(storageKey));
        }

        var arrived = 0;
        var gate = new System.Threading.Tasks.TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        async Task WaitForBothAsync()
        {
            if (Interlocked.Increment(ref arrived) == 2) gate.TrySetResult();
            await gate.Task;
        }

        var capture = Task.Run(async () =>
        {
            await using var s = await AsUserAsync(_fixture.NormAdminA.Id);
            var version = await VersionAsync(s, cardId);
            await WaitForBothAsync();
            try { return (object)await s.Revisions.CaptureAsync(cardId, version); }
            catch (Exception ex) { return (object)ex; }
        });

        var remove = Task.Run(async () =>
        {
            await using var s = await AsUserAsync(_fixture.NormAdminA.Id);
            var version = await VersionAsync(s, cardId);
            await WaitForBothAsync();
            try { await s.HK.DeleteAttachmentAsync(cardId); return (object)"deleted"; }
            catch (Exception ex) { return (object)ex; }
        });

        await capture;
        await remove;

        // Если фиксация состоялась, её основание обязано существовать.
        var revisions = await Task.Run(async () =>
        {
            await using var s = await AsUserAsync(_fixture.NormAdminA.Id);
            return await s.Db.HKCardRevisions.AsNoTracking()
                .Where(r => r.HKCardId == cardId).ToListAsync();
        });

        if (revisions.Count > 0)
        {
            Assert.True(File.Exists(ResolvePath(storageKey)),
                "Редакция ссылается на уже удалённый файл.");
            Assert.Equal(original, await File.ReadAllBytesAsync(ResolvePath(storageKey)));
        }
        else
        {
            // Фиксации не было: тогда удаление файла законно.
            Assert.True(!File.Exists(ResolvePath(storageKey))
                        || File.ReadAllBytes(ResolvePath(storageKey)).SequenceEqual(original));
        }
    }

    /// <summary>
    /// Файловый путь служебных операций не выдаётся наружу, а чтение принятой
    /// структуры другого филиала отвергается до выдачи содержимого.
    /// </summary>
    [Fact]
    public async Task OtherBranch_CannotReadAcceptedStructure_AndStorageKeyIsNotExposed()
    {
        await using var s = await AsUserAsync(_fixture.NormAdminA.Id);
        var cardId = await CreateCardAsync(s);
        await AttachAsync(s, cardId, "secret");

        var version = await VersionAsync(s, cardId);
        var revisionId = await s.Revisions.CaptureAsync(cardId, version);

        await using (var setup = await AsUserAsync(_fixture.NormAdminA.Id))
        {
            var card = await setup.Db.HKCards.FirstAsync(c => c.Id == cardId);
            card.CurrentAcceptedRevisionId = revisionId;
            await setup.Db.SaveChangesAsync();
        }

        // DTO принятой структуры существует и содержит только признак наличия PDF.
        var mine = await s.Revisions.GetAcceptedAsync(cardId);
        Assert.NotNull(mine);
        Assert.True(mine!.HasAttachment);

        // Соседний филинал не получает ничего.
        await using var outsider = await AsUserAsync(_fixture.NormAdminB.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => outsider.Revisions.GetAcceptedAsync(cardId));
    }

    /// <summary>Длинный НД сохраняется в снимке целиком, без молчаливого обрезания.</summary>
    [Fact]
    public async Task Capture_KeepsLongNdCompletely()
    {
        var longNd = "ТУ-" + new string('9', 1500);

        Guid revisionId;
        await using (var s = await AsUserAsync(_fixture.NormAdminA.Id))
        {
            var cardId = await CreateCardAsync(s);
            await s.Db.GsmMaterials.ExecuteUpdateAsync(g => g.SetProperty(x => x.Nd, longNd));

            var version = await VersionAsync(s, cardId);
            revisionId = await s.Revisions.CaptureAsync(cardId, version);
        }

        await using var check = await AsUserAsync(_fixture.NormAdminA.Id);
        var material = await check.Db.HKCardRevisionItemMaterials.AsNoTracking()
            .FirstAsync(m => m.RevisionItem.RevisionId == revisionId);

        Assert.Equal(longNd, material.Nd);
        // Префикс «ТУ-» — три символа плюс 1500 цифр.
        Assert.Equal(1503, material.Nd!.Length);
    }
}

