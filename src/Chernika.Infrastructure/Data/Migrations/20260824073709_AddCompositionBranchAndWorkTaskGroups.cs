using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCompositionBranchAndWorkTaskGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WorkTaskGroupId",
                table: "WorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "ProductCompositions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "ComplexCompositions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "AggregateCompositions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkTaskGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTaskGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkTaskGroups_Users_CompletedByUserId",
                        column: x => x.CompletedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_WorkTaskGroupId",
                table: "WorkTasks",
                column: "WorkTaskGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositions_BranchId",
                table: "ProductCompositions",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_ComplexCompositions_BranchId",
                table: "ComplexCompositions",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_AggregateCompositions_BranchId",
                table: "AggregateCompositions",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskGroups_CompletedByUserId",
                table: "WorkTaskGroups",
                column: "CompletedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskGroups_EntityType_EntityId_BranchId",
                table: "WorkTaskGroups",
                columns: new[] { "EntityType", "EntityId", "BranchId" });

            migrationBuilder.AddForeignKey(
                name: "FK_WorkTasks_WorkTaskGroups_WorkTaskGroupId",
                table: "WorkTasks",
                column: "WorkTaskGroupId",
                principalTable: "WorkTaskGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkTasks_WorkTaskGroups_WorkTaskGroupId",
                table: "WorkTasks");

            migrationBuilder.DropTable(
                name: "WorkTaskGroups");

            migrationBuilder.DropIndex(
                name: "IX_WorkTasks_WorkTaskGroupId",
                table: "WorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_ProductCompositions_BranchId",
                table: "ProductCompositions");

            migrationBuilder.DropIndex(
                name: "IX_ComplexCompositions_BranchId",
                table: "ComplexCompositions");

            migrationBuilder.DropIndex(
                name: "IX_AggregateCompositions_BranchId",
                table: "AggregateCompositions");

            migrationBuilder.DropColumn(
                name: "WorkTaskGroupId",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "ProductCompositions");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "ComplexCompositions");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "AggregateCompositions");
        }
    }
}
