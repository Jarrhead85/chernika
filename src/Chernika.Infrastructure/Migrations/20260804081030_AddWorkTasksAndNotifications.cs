using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkTasksAndNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkTasks_Users_AssigneeId",
                table: "WorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_WorkTasks_AssigneeId",
                table: "WorkTasks");

            migrationBuilder.AddColumn<string>(
                name: "AssignedRole",
                table: "WorkTasks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedToUserId",
                table: "WorkTasks",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "WorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAtUtc",
                table: "WorkTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletedByUserId",
                table: "WorkTasks",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletionComment",
                table: "WorkTasks",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "WorkTasks",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserId",
                table: "WorkTasks",
                type: "character varying(450)",
                maxLength: 450,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EntityCodeSnapshot",
                table: "WorkTasks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntityTitleSnapshot",
                table: "WorkTasks",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DueDateUtc",
                table: "WorkTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "WorkTasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "WorkTasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedAtUtc",
                table: "WorkTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "WorkTasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "WorkTasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "WorkTasks",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.Sql(
                """
                UPDATE "WorkTasks" SET
                    "AssignedToUserId" = "AssigneeId",
                    "CreatedByUserId" = COALESCE("AssigneeId", ''),
                    "CreatedAtUtc" = "CreatedAt",
                    "UpdatedAtUtc" = "CreatedAt",
                    "DueDateUtc" = "DueDate",
                    "CompletedAtUtc" = "CompletedAt",
                    "Status" = CASE WHEN "IsCompleted" THEN 3 ELSE 1 END,
                    "Priority" = 2,
                    "Type" = 1,
                    "IsDeleted" = false
                """);

            migrationBuilder.Sql(
                """
                UPDATE "WorkTasks" SET "EntityId" = NULL
                WHERE "EntityId" IS NOT NULL
                  AND "EntityId" !~ '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$'
                """);

            migrationBuilder.DropColumn(
                name: "AssigneeId",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "DueDate",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "IsCompleted",
                table: "WorkTasks");

            migrationBuilder.Sql(
                """
                ALTER TABLE "WorkTasks" ALTER COLUMN "EntityId" TYPE uuid USING ("EntityId"::uuid)
                """);

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    NavigationUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DeduplicationKey = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Notifications_WorkTasks_WorkTaskId",
                        column: x => x.WorkTaskId,
                        principalTable: "WorkTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_AssignedRole_Status_IsDeleted",
                table: "WorkTasks",
                columns: new[] { "AssignedRole", "Status", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_AssignedToUserId_Status_IsDeleted",
                table: "WorkTasks",
                columns: new[] { "AssignedToUserId", "Status", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_BranchId_Status",
                table: "WorkTasks",
                columns: new[] { "BranchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_CreatedAtUtc",
                table: "WorkTasks",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_CreatedByUserId",
                table: "WorkTasks",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_DueDateUtc_Status",
                table: "WorkTasks",
                columns: new[] { "DueDateUtc", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_EntityType_EntityId",
                table: "WorkTasks",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkTasks_Assignee",
                table: "WorkTasks",
                sql: "\"AssignedToUserId\" IS NOT NULL OR \"AssignedRole\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkTasks_CompletedAt",
                table: "WorkTasks",
                sql: "\"CompletedAtUtc\" IS NULL OR \"Status\" IN (3, 4)");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_EntityType_EntityId",
                table: "Notifications",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_ExpiresAtUtc",
                table: "Notifications",
                columns: new[] { "UserId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_IsRead_CreatedAtUtc",
                table: "Notifications",
                columns: new[] { "UserId", "IsRead", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_WorkTaskId",
                table: "Notifications",
                column: "WorkTaskId");

            migrationBuilder.CreateIndex(
                name: "UX_Notifications_DeduplicationKey",
                table: "Notifications",
                columns: new[] { "UserId", "DeduplicationKey" },
                unique: true,
                filter: "\"DeduplicationKey\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkTasks_Users_AssignedToUserId",
                table: "WorkTasks",
                column: "AssignedToUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkTasks_Users_CreatedByUserId",
                table: "WorkTasks",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkTasks_Users_AssignedToUserId",
                table: "WorkTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkTasks_Users_CreatedByUserId",
                table: "WorkTasks");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_WorkTasks_AssignedRole_Status_IsDeleted",
                table: "WorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_WorkTasks_AssignedToUserId_Status_IsDeleted",
                table: "WorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_WorkTasks_BranchId_Status",
                table: "WorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_WorkTasks_CreatedAtUtc",
                table: "WorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_WorkTasks_CreatedByUserId",
                table: "WorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_WorkTasks_DueDateUtc_Status",
                table: "WorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_WorkTasks_EntityType_EntityId",
                table: "WorkTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkTasks_Assignee",
                table: "WorkTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkTasks_CompletedAt",
                table: "WorkTasks");

            migrationBuilder.Sql(
                """
                ALTER TABLE "WorkTasks" ALTER COLUMN "EntityId" TYPE character varying(100)
                """);

            migrationBuilder.AddColumn<string>(
                name: "AssigneeId",
                table: "WorkTasks",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAt",
                table: "WorkTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "WorkTasks",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "DueDate",
                table: "WorkTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCompleted",
                table: "WorkTasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                """
                UPDATE "WorkTasks" SET
                    "AssigneeId" = "AssignedToUserId",
                    "CreatedAt" = "CreatedAtUtc",
                    "DueDate" = "DueDateUtc",
                    "CompletedAt" = "CompletedAtUtc",
                    "IsCompleted" = ("Status" = 3)
                """);

            migrationBuilder.DropColumn(
                name: "AssignedRole",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "AssignedToUserId",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "CompletedByUserId",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "CompletionComment",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "EntityCodeSnapshot",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "EntityTitleSnapshot",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "DueDateUtc",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "StartedAtUtc",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "WorkTasks");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_AssigneeId",
                table: "WorkTasks",
                column: "AssigneeId");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkTasks_Users_AssigneeId",
                table: "WorkTasks",
                column: "AssigneeId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
