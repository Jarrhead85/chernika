using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Chernika.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class CompositionReadModelIntegrationTests
{
    private readonly TestDatabaseFixture _fixture;

    public CompositionReadModelIntegrationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProductComposition_WithNullAuthorId_SummaryAndDetailDoNotThrow()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, modelId, _) = await CreateProductCompositionDraftAsync(s);

        var comp = await s.Db.ProductCompositions.FindAsync(compositionId);
        Assert.NotNull(comp);
        comp.AuthorId = null;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var summaries = await s.Equipment.GetProductCompositionSummariesAsync(modelId);
        var summary = summaries.Single(x => x.Id == compositionId);
        Assert.Null(summary.AuthorName);

        var detail = await s.Equipment.GetProductCompositionDetailAsync(compositionId);
        Assert.NotNull(detail);
        Assert.Null(detail.AuthorName);
    }

    [Fact]
    public async Task AggregateComposition_WithNullAuthorId_SummaryAndDetailDoNotThrow()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, aggregateId) = await CreateAggregateCompositionDraftAsync(s);

        var comp = await s.Db.AggregateCompositions.FindAsync(compositionId);
        Assert.NotNull(comp);
        comp.AuthorId = null;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var summaries = await s.Equipment.GetAggregateCompositionSummariesAsync(aggregateId);
        var summary = summaries.Single(x => x.Id == compositionId);
        Assert.Null(summary.AuthorName);

        var detail = await s.Equipment.GetAggregateCompositionDetailAsync(compositionId);
        Assert.NotNull(detail);
        Assert.Null(detail.AuthorName);
    }

    [Fact]
    public async Task ComplexComposition_WithNullAuthorId_SummaryAndDetailDoNotThrow()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, complexId) = await CreateComplexCompositionDraftAsync(s);

        var comp = await s.Db.ComplexCompositions.FindAsync(compositionId);
        Assert.NotNull(comp);
        comp.AuthorId = null;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var summaries = await s.Equipment.GetComplexCompositionSummariesAsync(complexId);
        var summary = summaries.Single(x => x.Id == compositionId);
        Assert.Null(summary.AuthorName);

        var detail = await s.Equipment.GetComplexCompositionDetailAsync(compositionId);
        Assert.NotNull(detail);
        Assert.Null(detail.AuthorName);
    }

    [Fact]
    public async Task ProductComposition_Detail_LoadsPartsAndAggregates()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, _, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(partId, aggregate.Id, 2));

        var detail = await s.Equipment.GetProductCompositionDetailAsync(compositionId);
        Assert.NotNull(detail);
        Assert.Single(detail.Parts);
        Assert.Single(detail.Parts[0].Aggregates);
        Assert.NotNull(detail.Parts[0].Aggregates[0].Aggregate);
        Assert.Equal(aggregate.Code, detail.Parts[0].Aggregates[0].Aggregate!.Code);
    }

    [Fact]
    public async Task AggregateComposition_Detail_LoadsNodes()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, aggregateId) = await CreateAggregateCompositionDraftAsync(s);
        var node = new Node { Id = Guid.NewGuid(), Code = "N-" + Guid.NewGuid().ToString("N")[..6], Name = "Узел тест" };
        s.Db.Nodes.Add(node);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateCompositionNodeAsync(new AddAggregateCompositionNodeRequest(compositionId, node.Id, 3, null));

        var detail = await s.Equipment.GetAggregateCompositionDetailAsync(compositionId);
        Assert.NotNull(detail);
        Assert.Single(detail.Nodes);
        Assert.NotNull(detail.Nodes[0].Node);
        Assert.Equal(node.Code, detail.Nodes[0].Node!.Code);
    }

    [Fact]
    public async Task ComplexComposition_Detail_LoadsItems()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, complexId) = await CreateComplexCompositionDraftAsync(s);
        var model = new EquipmentModel { Id = Guid.NewGuid(), Index = "M-" + Guid.NewGuid().ToString("N")[..6], Name = "Модель тест" };
        s.Db.EquipmentModels.Add(model);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddComplexCompositionItemAsync(new AddComplexCompositionItemRequest(compositionId, model.Id, 2));

        var detail = await s.Equipment.GetComplexCompositionDetailAsync(compositionId);
        Assert.NotNull(detail);
        Assert.Single(detail.Items);
        Assert.NotNull(detail.Items[0].EquipmentModel);
        Assert.Equal(model.Index, detail.Items[0].EquipmentModel!.Index);
    }

    [Fact]
    public async Task ProductComposition_Summary_ContainsCountsWithoutFullGraph()
    {
        await using var s = _fixture.CreateScope();
        var (compositionId, modelId, partId) = await CreateProductCompositionDraftAsync(s);
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        await s.Equipment.AddAggregateAsync(new AddProductCompositionAggregateRequest(partId, aggregate.Id, 2));

        var summaries = await s.Equipment.GetProductCompositionSummariesAsync(modelId);
        var summary = summaries.Single(x => x.Id == compositionId);
        Assert.Equal(1, summary.PartCount);
        Assert.Equal(1, summary.AggregateCount);
        Assert.Equal(0, summary.CoveredCount);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

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

    private async Task<(Guid CompositionId, Guid AggregateId)> CreateAggregateCompositionDraftAsync(TestScope s)
    {
        var aggregate = new Aggregate { Id = Guid.NewGuid(), Code = "A-" + Guid.NewGuid().ToString("N")[..6], Name = "Агрегат тест" };
        s.Db.Aggregates.Add(aggregate);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var comp = await s.Equipment.CreateAggregateCompositionAsync(new CreateAggregateCompositionRequest(aggregate.Id, "Тест"));
        return (comp.Id, aggregate.Id);
    }

    private async Task<(Guid CompositionId, Guid ComplexId)> CreateComplexCompositionDraftAsync(TestScope s)
    {
        var complex = new Complex { Id = Guid.NewGuid(), Code = "C-" + Guid.NewGuid().ToString("N")[..6], Name = "Комплекс тест" };
        s.Db.Complexes.Add(complex);
        await s.Db.SaveChangesAsync();

        s.User.CurrentUserId = Guid.Parse(_fixture.NormAdminA.Id);
        var comp = await s.Equipment.CreateComplexCompositionAsync(new CreateComplexCompositionRequest(complex.Id, "Тест"));
        return (comp.Id, complex.Id);
    }
}
