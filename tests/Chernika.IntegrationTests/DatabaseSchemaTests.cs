using Chernika.Domain.Entities;
using Chernika.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using Xunit;

namespace Chernika.IntegrationTests;

[Collection("Database")]
public class DatabaseSchemaTests
{
    private readonly TestDatabaseFixture _fixture;

    public DatabaseSchemaTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task WorkTasks_HasExpectedColumns()
    {
        await using var s = _fixture.CreateScope();
        var columns = await GetColumnNamesAsync(s, "WorkTasks");

        Assert.Contains("Id", columns);
        Assert.Contains("Status", columns);
        Assert.Contains("Type", columns);
        Assert.Contains("Priority", columns);
        Assert.Contains("AssignedToUserId", columns);
        Assert.Contains("AssignedRole", columns);
        Assert.Contains("BranchId", columns);
        Assert.Contains("EntityType", columns);
        Assert.Contains("EntityId", columns);
        Assert.Contains("CreatedByUserId", columns);
        Assert.Contains("CreatedAtUtc", columns);
        Assert.Contains("DueDateUtc", columns);
        Assert.Contains("StartedAtUtc", columns);
        Assert.Contains("CompletedAtUtc", columns);
        Assert.Contains("CompletedByUserId", columns);
        Assert.Contains("CompletionComment", columns);
        Assert.Contains("IsDeleted", columns);
    }

    [Fact]
    public async Task WorkTasks_HasCheckConstraints()
    {
        await using var s = _fixture.CreateScope();
        Assert.True(await ConstraintExistsAsync(s, "WorkTasks", "CK_WorkTasks_Assignee"));
        Assert.True(await ConstraintExistsAsync(s, "WorkTasks", "CK_WorkTasks_CompletedAt"));
    }

    [Fact]
    public async Task WorkTasks_HasExpectedIndexes()
    {
        await using var s = _fixture.CreateScope();
        Assert.True(await IndexExistsAsync(s, "WorkTasks", "IX_WorkTasks_BranchId_Status"));
        Assert.True(await IndexExistsAsync(s, "WorkTasks", "IX_WorkTasks_DueDateUtc_Status"));
        Assert.True(await IndexExistsAsync(s, "WorkTasks", "IX_WorkTasks_EntityType_EntityId"));
        Assert.True(await IndexExistsAsync(s, "WorkTasks", "IX_WorkTasks_AssignedToUserId_Status_IsDeleted"));
    }

    [Fact]
    public async Task WorkTasks_HasForeignKeysToUsers()
    {
        await using var s = _fixture.CreateScope();
        Assert.True(await ForeignKeyExistsAsync(s, "FK_WorkTasks_Users_AssignedToUserId"));
        Assert.True(await ForeignKeyExistsAsync(s, "FK_WorkTasks_Users_CreatedByUserId"));
    }

    [Fact]
    public async Task Notifications_HasExpectedSchema()
    {
        await using var s = _fixture.CreateScope();
        var columns = await GetColumnNamesAsync(s, "Notifications");

        Assert.Contains("UserId", columns);
        Assert.Contains("Type", columns);
        Assert.Contains("DeduplicationKey", columns);
        Assert.Contains("IsRead", columns);
        Assert.Contains("CreatedAtUtc", columns);
        Assert.Contains("WorkTaskId", columns);

        Assert.True(await IndexExistsAsync(s, "Notifications", "UX_Notifications_DeduplicationKey"));
        Assert.True(await IndexExistsAsync(s, "Notifications", "IX_Notifications_UserId_IsRead_CreatedAtUtc"));
        Assert.True(await ForeignKeyExistsAsync(s, "FK_Notifications_Users_UserId"));
        Assert.True(await ForeignKeyExistsAsync(s, "FK_Notifications_WorkTasks_WorkTaskId"));
    }

    [Fact]
    public async Task Notifications_UniqueIndex_EnforcesDeduplication()
    {
        await using var s = _fixture.CreateScope();

        s.Db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = _fixture.OperatorA.Id,
            Type = NotificationType.Information,
            Title = "Дубль",
            DeduplicationKey = "schema-duplicate",
            CreatedAtUtc = DateTime.UtcNow,
        });
        s.Db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = _fixture.OperatorA.Id,
            Type = NotificationType.Information,
            Title = "Дубль",
            DeduplicationKey = "schema-duplicate",
            CreatedAtUtc = DateTime.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
    }

    private static async Task<List<string>> GetColumnNamesAsync(TestScope s, string table)
    {
        await using var cmd = s.Db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = @"
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table
            ORDER BY ordinal_position";
        var p = cmd.CreateParameter();
        p.ParameterName = "@table";
        p.Value = table;
        cmd.Parameters.Add(p);

        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            await cmd.Connection.OpenAsync();

        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<bool> ConstraintExistsAsync(TestScope s, string table, string constraint)
    {
        await using var cmd = s.Db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = @"
            SELECT 1 FROM information_schema.table_constraints
            WHERE constraint_type = 'CHECK'
              AND table_schema = 'public'
              AND table_name = @table
              AND constraint_name = @constraint";
        cmd.Parameters.Add(CreateParameter(cmd, "@table", table));
        cmd.Parameters.Add(CreateParameter(cmd, "@constraint", constraint));
        return await ScalarExistsAsync(cmd);
    }

    private static async Task<bool> IndexExistsAsync(TestScope s, string table, string index)
    {
        await using var cmd = s.Db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = @"
            SELECT 1 FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = @table
              AND indexname = @index";
        cmd.Parameters.Add(CreateParameter(cmd, "@table", table));
        cmd.Parameters.Add(CreateParameter(cmd, "@index", index));
        return await ScalarExistsAsync(cmd);
    }

    private static async Task<bool> ForeignKeyExistsAsync(TestScope s, string constraint)
    {
        await using var cmd = s.Db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = @"
            SELECT 1 FROM pg_constraint
            WHERE contype = 'f' AND conname = @constraint";
        cmd.Parameters.Add(CreateParameter(cmd, "@constraint", constraint));
        return await ScalarExistsAsync(cmd);
    }

    private static DbParameter CreateParameter(DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        return p;
    }

    private static async Task<bool> ScalarExistsAsync(DbCommand cmd)
    {
        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            await cmd.Connection.OpenAsync();
        return (await cmd.ExecuteScalarAsync()) != null;
    }
}
