using Chernika.Domain;
using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Data.Common;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class CompositionVersioningIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public CompositionVersioningIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProductCompositionVersion_ApprovedSource_CreatesLinkedDraftDeepCopy()
    {
        await using var s = _fixture.CreateScope();
        var (sourceId, modelId, _) = await CreateApprovedProductCompositionAsync(s);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, newId, error) = await s.Equipment.CreateProductCompositionVersionAsync(sourceId);
        Assert.True(success, error);
        Assert.NotNull(newId);

        var source = await s.Db.ProductCompositions.AsNoTracking()
            .Include(c => c.Parts).ThenInclude(p => p.Aggregates)
            .SingleAsync(c => c.Id == sourceId);
        var fresh = await s.Db.ProductCompositions.AsNoTracking()
            .Include(c => c.Parts).ThenInclude(p => p.Aggregates)
            .SingleAsync(c => c.Id == newId.Value);

        Assert.Equal(ProductCompositionStatus.Draft, fresh.Status);
        Assert.False(fresh.IsActive);
        Assert.Null(fresh.ApprovedAt);
        Assert.Null(fresh.ApprovedByUserId);
        Assert.Equal(sourceId, fresh.SupersedesProductCompositionId);
        Assert.Equal(modelId, fresh.EquipmentModelId);

        Assert.Equal(source.Parts.Count, fresh.Parts.Count);
        var sourcePart = source.Parts.Single();
        var newPart = fresh.Parts.Single();
        Assert.NotEqual(sourcePart.Id, newPart.Id);
        Assert.Equal(sourcePart.Name, newPart.Name);

        Assert.Single(newPart.Aggregates);
        var newPca = newPart.Aggregates.Single();
        var sourcePca = sourcePart.Aggregates.Single();
        Assert.NotEqual(sourcePca.Id, newPca.Id);
        Assert.Equal(sourcePca.AggregateId, newPca.AggregateId);
        Assert.Equal(sourcePca.Quantity, newPca.Quantity);
        Assert.Equal(newId.Value, newPca.ProductCompositionId);
    }

    [Fact]
    public async Task ProductCompositionVersion_DraftSource_Rejected()
    {
        await using var s = _fixture.CreateScope();
        var (draftId, _, _) = await CreateProductCompositionDraftAsync(s);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, newId, error) = await s.Equipment.CreateProductCompositionVersionAsync(draftId);
        Assert.False(success);
        Assert.Null(newId);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task ComplexCompositionVersion_ArchivedSource_CreatesDraft()
    {
        await using var s = _fixture.CreateScope();
        var (sourceId, complexId, modelId) = await CreateArchivedComplexCompositionAsync(s);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, newId, error) = await s.Equipment.CreateComplexCompositionVersionAsync(sourceId);
        Assert.True(success, error);
        Assert.NotNull(newId);

        var fresh = await s.Db.ComplexCompositions.AsNoTracking()
            .Include(c => c.Items)
            .SingleAsync(c => c.Id == newId.Value);

        Assert.Equal(ProductCompositionStatus.Draft, fresh.Status);
        Assert.False(fresh.IsActive);
        Assert.Equal(sourceId, fresh.SupersedesComplexCompositionId);
        Assert.Equal(complexId, fresh.ComplexId);
        Assert.Single(fresh.Items);
        Assert.Equal(modelId, fresh.Items.Single().EquipmentModelId);
    }

    [Fact]
    public async Task CreateVersion_GeneratesUniqueVersion_ForSameObjectInSameMonth()
    {
        await using var s = _fixture.CreateScope();
        var (sourceId, _, _) = await CreateApprovedProductCompositionAsync(s);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (s1, id1, e1) = await s.Equipment.CreateProductCompositionVersionAsync(sourceId);
        Assert.True(s1, e1);

        await s.Equipment.SubmitForReviewAsync(id1!.Value);
        var approved1 = await s.Equipment.ApproveCompositionAsync(id1.Value, null);
        Assert.True(approved1);

        var (s2, id2, e2) = await s.Equipment.CreateProductCompositionVersionAsync(id1.Value);
        Assert.True(s2, e2);

        var v1 = await s.Db.ProductCompositions.AsNoTracking().SingleAsync(c => c.Id == id1.Value);
        var v2 = await s.Db.ProductCompositions.AsNoTracking().SingleAsync(c => c.Id == id2!.Value);
        Assert.NotEqual(v1.Version, v2.Version);
        var baseVersion = v1.Version.Split('.')[0];
        Assert.StartsWith(baseVersion + ".", v2.Version);
    }

    [Fact]
    public async Task AddAggregateAsync_SetsProductCompositionId_FromPart()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);

        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var pca = await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(partId, aggregate.Id, 3));

        Assert.Equal(compositionId, pca.ProductCompositionId);
        Assert.Equal(partId, pca.PartId);
    }

    [Fact]
    public async Task ProductCompositionAggregate_CrossVersionUniqueIndex_Enforced()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);

        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(partId, aggregate.Id, 1));

        s.Db.ProductCompositionAggregates.Add(new ProductCompositionAggregate
        {
            Id = Guid.NewGuid(),
            ProductCompositionId = compositionId,
            PartId = partId,
            AggregateId = aggregate.Id,
            Quantity = 1,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task ProductCompositionAggregate_PartId_Nullable()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, _) = await CreateProductCompositionDraftAsync(s);

        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.Db.ProductCompositionAggregates.Add(new ProductCompositionAggregate
        {
            Id = Guid.NewGuid(),
            ProductCompositionId = compositionId,
            PartId = null,
            AggregateId = aggregate.Id,
            Quantity = 1,
        });
        await s.Db.SaveChangesAsync();

        var saved = await s.Db.ProductCompositionAggregates.AsNoTracking()
            .SingleAsync(a => a.AggregateId == aggregate.Id);
        Assert.Null(saved.PartId);
        Assert.Equal(compositionId, saved.ProductCompositionId);
    }

    [Fact]
    public async Task CreateProductCompositionVersionWithoutPermission_Throws()
    {
        await using var s = _fixture.CreateScope();
        var (sourceId, _, _) = await CreateApprovedProductCompositionAsync(s);

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.Equipment.CreateProductCompositionVersionAsync(sourceId));
    }

    [Fact]
    public async Task CreateComplexComposition_WithoutGranularPermission_Throws()
    {
        await using var s = _fixture.CreateScope();
        var complex = new Complex { Id = Guid.NewGuid(), Code = "K-" + Guid.NewGuid().ToString("N")[..6], Name = "Комплекс тест" };
        s.Db.Complexes.Add(complex);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.OperatorA.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            s.Equipment.CreateComplexCompositionAsync(new CreateComplexCompositionRequest(complex.Id, null)));
    }

    [Fact]
    public async Task CreateVersion_AuditLog_ContainsSourceAndNewVersion()
    {
        await using var s = _fixture.CreateScope();
        var (sourceId, _, _) = await CreateApprovedProductCompositionAsync(s);
        var source = await s.Db.ProductCompositions.AsNoTracking().SingleAsync(c => c.Id == sourceId);

        Assert.Equal("Создана новая версия состава изделия", AuditDisplayCatalog.GetAction("ProductComposition.NewVersionCreated").Title);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, newId, error) = await s.Equipment.CreateProductCompositionVersionAsync(sourceId);
        Assert.True(success, error);

        var audit = await s.Db.AuditLogs.AsNoTracking()
            .SingleOrDefaultAsync(a => a.EntityType == "ProductComposition" && a.EntityId == newId.ToString() && a.Action == "ProductComposition.NewVersionCreated");
        Assert.NotNull(audit);
        Assert.Contains(source.Version, audit.Details);
    }

    [Fact]
    public async Task SupersedesSelfForeignKeys_AreRestrict()
    {
        await using var s = _fixture.CreateScope();
        Assert.Equal("r", await GetForeignKeyDeleteRuleByColumnAsync(s, "ComplexCompositions", "SupersedesComplexCompositionId"));
        Assert.Equal("r", await GetForeignKeyDeleteRuleByColumnAsync(s, "ProductCompositions", "SupersedesProductCompositionId"));
        Assert.Equal("r", await GetForeignKeyDeleteRuleByColumnAsync(s, "AggregateCompositions", "SupersedesAggregateCompositionId"));
        Assert.Equal("c", await GetForeignKeyDeleteRuleByColumnAsync(s, "ProductCompositionAggregates", "ProductCompositionId"));
    }

    [Fact]
    public async Task DeletingComposition_ReferencedBySuccessor_DatabaseRestricts()
    {
        await using var s = _fixture.CreateScope();
        var (sourceId, _, _) = await CreateApprovedProductCompositionAsync(s);

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var (success, newId, _) = await s.Equipment.CreateProductCompositionVersionAsync(sourceId);
        Assert.True(success);
        Assert.NotNull(newId);

        await using var cmd = s.Db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = @"DELETE FROM ""ProductCompositions"" WHERE ""Id"" = @id";
        var p = cmd.CreateParameter();
        p.ParameterName = "@id";
        p.Value = sourceId;
        cmd.Parameters.Add(p);
        await EnsureOpenAsync(cmd);

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23001", ex.SqlState);

        var survivor = await s.Db.ProductCompositions.AsNoTracking().SingleAsync(c => c.Id == newId.Value);
        Assert.NotNull(survivor);
    }

    [Fact]
    public async Task ProductCompositionAggregates_Schema_ExpectedIndexesAndNullablePartId()
    {
        await using var s = _fixture.CreateScope();
        var columns = await GetColumnNamesAsync(s, "ProductCompositionAggregates");
        Assert.Contains("ProductCompositionId", columns);

        Assert.True(await IndexExistsAsync(s, "ProductCompositionAggregates", "IX_ProductCompositionAggregates_ProductCompositionId_Aggregate"));
        Assert.Equal("YES", await GetColumnIsNullableAsync(s, "ProductCompositionAggregates", "PartId"));
    }

    [Fact]
    public void AuditDisplayCatalog_HasNewVersionActionsForAllLevels()
    {
        Assert.Equal("Создана новая версия состава комплекса", AuditDisplayCatalog.GetAction("ComplexComposition.NewVersionCreated").Title);
        Assert.Equal("Создана новая версия состава изделия", AuditDisplayCatalog.GetAction("ProductComposition.NewVersionCreated").Title);
        Assert.Equal("Создана новая версия состава агрегата", AuditDisplayCatalog.GetAction("AggregateComposition.NewVersionCreated").Title);
    }

    private async Task<(Guid SourceId, Guid ModelId, Guid PartId)> CreateApprovedProductCompositionAsync(TestScope s)
    {
        var (compositionId, modelId, partId) = await CreateProductCompositionDraftAsync(s);

        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(partId, aggregate.Id, 2));
        await s.Equipment.SubmitForReviewAsync(compositionId);
        var approved = await s.Equipment.ApproveCompositionAsync(compositionId, null);
        Assert.True(approved);
        return (compositionId, modelId, partId);
    }

    private async Task<(Guid CompositionId, Guid ModelId, Guid PartId)> CreateProductCompositionDraftAsync(TestScope s)
    {
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие тест" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var comp = await s.Equipment.CreateCompositionDraftAsync(new CreateCompositionRequest(model.Id, "Тест"));

        var part = await s.Equipment.AddPartAsync(new AddPartRequest(comp.Id, "Силовая установка", null, 1));

        return (comp.Id, model.Id, part.Id);
    }

    private async Task<(Guid SourceId, Guid ComplexId, Guid ModelId)> CreateArchivedComplexCompositionAsync(TestScope s)
    {
        var complex = new Complex { Id = Guid.NewGuid(), Code = "K-" + Guid.NewGuid().ToString("N")[..6], Name = "Комплекс тест" };
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "T-" + Guid.NewGuid().ToString("N")[..6], Name = "Изделие тест" };
        s.Db.Complexes.Add(complex);
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var comp = await s.Equipment.CreateComplexCompositionAsync(new CreateComplexCompositionRequest(complex.Id, "Тест"));
        await s.Equipment.AddComplexCompositionItemAsync(new AddComplexCompositionItemRequest(comp.Id, model.Id, 1));
        await s.Equipment.SubmitComplexCompositionForReviewAsync(comp.Id);
        var approved = await s.Equipment.ApproveComplexCompositionAsync(comp.Id, null);
        Assert.True(approved);
        var archived = await s.Equipment.ArchiveComplexCompositionAsync(comp.Id);
        Assert.True(archived);

        return (comp.Id, complex.Id, model.Id);
    }

    // ── Schema helpers ────────────────────────────────────────────────────

    private static async Task<string?> GetForeignKeyDeleteRuleByColumnAsync(TestScope s, string fkTable, string fkColumn)
    {
        await using var cmd = s.Db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = @"
            SELECT c.confdeltype::text
            FROM pg_constraint c
            JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord) ON true
            JOIN pg_attribute a ON a.attnum = k.attnum AND a.attrelid = c.conrelid
            WHERE c.contype = 'f'
              AND c.conrelid = quote_ident(@fktable)::regclass
              AND a.attname = @fkcol";
        cmd.Parameters.Add(CreateParameter(cmd, "@fktable", fkTable));
        cmd.Parameters.Add(CreateParameter(cmd, "@fkcol", fkColumn));
        await EnsureOpenAsync(cmd);
        await using var reader = await cmd.ExecuteReaderAsync();
        return reader.Read() ? reader.GetString(0) : null;
    }

    private static async Task<List<string>> GetColumnNamesAsync(TestScope s, string table)
    {
        await using var cmd = s.Db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = @"
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table
            ORDER BY ordinal_position";
        cmd.Parameters.Add(CreateParameter(cmd, "@table", table));
        await EnsureOpenAsync(cmd);
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<string?> GetColumnIsNullableAsync(TestScope s, string table, string column)
    {
        await using var cmd = s.Db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = @"
            SELECT is_nullable
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table AND column_name = @column";
        cmd.Parameters.Add(CreateParameter(cmd, "@table", table));
        cmd.Parameters.Add(CreateParameter(cmd, "@column", column));
        await EnsureOpenAsync(cmd);
        await using var reader = await cmd.ExecuteReaderAsync();
        return reader.Read() ? reader.GetString(0) : null;
    }

    private static async Task<bool> IndexExistsAsync(TestScope s, string table, string indexPrefix)
    {
        await using var cmd = s.Db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = @"
            SELECT 1 FROM pg_indexes
            WHERE schemaname = 'public' AND tablename = @table AND indexname LIKE @like";
        cmd.Parameters.Add(CreateParameter(cmd, "@table", table));
        cmd.Parameters.Add(CreateParameter(cmd, "@like", indexPrefix + "%"));
        await EnsureOpenAsync(cmd);
        await using var reader = await cmd.ExecuteReaderAsync();
        return reader.Read();
    }

    private static DbParameter CreateParameter(DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        return p;
    }

    private static async Task EnsureOpenAsync(DbCommand cmd)
    {
        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            await cmd.Connection.OpenAsync();
    }
}