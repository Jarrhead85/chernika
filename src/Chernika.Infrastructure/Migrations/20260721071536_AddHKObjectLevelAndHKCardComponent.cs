using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHKObjectLevelAndHKCardComponent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HKCards_Nodes_NodeId",
                table: "HKCards");

            migrationBuilder.DropIndex(
                name: "UX_HKCards_OneActivePerNode",
                table: "HKCards");

            migrationBuilder.AlterColumn<Guid>(
                name: "NodeId",
                table: "HKCards",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "AggregateId",
                table: "HKCards",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ComplexId",
                table: "HKCards",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EquipmentModelId",
                table: "HKCards",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ObjectLevel",
                table: "HKCards",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersedesHKCardId",
                table: "HKCards",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("UPDATE \"HKCards\" SET \"ObjectLevel\" = 4 WHERE \"ObjectLevel\" = 0");

            migrationBuilder.Sql(@"
ALTER TABLE ""HKCards"" ADD CONSTRAINT ""CK_HKCards_ExactlyOneObject""
CHECK (
    (CASE WHEN ""ComplexId"" IS NOT NULL THEN 1 ELSE 0 END +
     CASE WHEN ""EquipmentModelId"" IS NOT NULL THEN 1 ELSE 0 END +
     CASE WHEN ""AggregateId"" IS NOT NULL THEN 1 ELSE 0 END +
     CASE WHEN ""NodeId"" IS NOT NULL THEN 1 ELSE 0 END) = 1
)");

            migrationBuilder.CreateTable(
                name: "HKCardComponents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentHKCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildHKCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AddedByUserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChildCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ChildVersion = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ChildApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HKCardComponents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HKCardComponents_HKCards_ChildHKCardId",
                        column: x => x.ChildHKCardId,
                        principalTable: "HKCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HKCardComponents_HKCards_ParentHKCardId",
                        column: x => x.ParentHKCardId,
                        principalTable: "HKCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HKCards_AggregateId",
                table: "HKCards",
                column: "AggregateId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCards_ComplexId",
                table: "HKCards",
                column: "ComplexId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCards_EquipmentModelId",
                table: "HKCards",
                column: "EquipmentModelId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCards_SupersedesHKCardId",
                table: "HKCards",
                column: "SupersedesHKCardId");

            migrationBuilder.CreateIndex(
                name: "UX_HKCards_OneActivePerNode",
                table: "HKCards",
                column: "NodeId",
                unique: true,
                filter: "\"ObjectLevel\" = 4 AND \"Status\" IN ('Draft', 'OnReview', 'RevisionRequired')");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardComponents_ChildHKCardId",
                table: "HKCardComponents",
                column: "ChildHKCardId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardComponents_ParentHKCardId_ChildHKCardId",
                table: "HKCardComponents",
                columns: new[] { "ParentHKCardId", "ChildHKCardId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_HKCards_Aggregates_AggregateId",
                table: "HKCards",
                column: "AggregateId",
                principalTable: "Aggregates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_HKCards_Complexes_ComplexId",
                table: "HKCards",
                column: "ComplexId",
                principalTable: "Complexes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_HKCards_EquipmentModels_EquipmentModelId",
                table: "HKCards",
                column: "EquipmentModelId",
                principalTable: "EquipmentModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_HKCards_HKCards_SupersedesHKCardId",
                table: "HKCards",
                column: "SupersedesHKCardId",
                principalTable: "HKCards",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_HKCards_Nodes_NodeId",
                table: "HKCards",
                column: "NodeId",
                principalTable: "Nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HKCards_Aggregates_AggregateId",
                table: "HKCards");

            migrationBuilder.DropForeignKey(
                name: "FK_HKCards_Complexes_ComplexId",
                table: "HKCards");

            migrationBuilder.DropForeignKey(
                name: "FK_HKCards_EquipmentModels_EquipmentModelId",
                table: "HKCards");

            migrationBuilder.DropForeignKey(
                name: "FK_HKCards_HKCards_SupersedesHKCardId",
                table: "HKCards");

            migrationBuilder.DropForeignKey(
                name: "FK_HKCards_Nodes_NodeId",
                table: "HKCards");

            migrationBuilder.Sql("ALTER TABLE \"HKCards\" DROP CONSTRAINT IF EXISTS \"CK_HKCards_ExactlyOneObject\"");

            migrationBuilder.DropTable(
                name: "HKCardComponents");

            migrationBuilder.DropIndex(
                name: "IX_HKCards_AggregateId",
                table: "HKCards");

            migrationBuilder.DropIndex(
                name: "IX_HKCards_ComplexId",
                table: "HKCards");

            migrationBuilder.DropIndex(
                name: "IX_HKCards_EquipmentModelId",
                table: "HKCards");

            migrationBuilder.DropIndex(
                name: "IX_HKCards_SupersedesHKCardId",
                table: "HKCards");

            migrationBuilder.DropIndex(
                name: "UX_HKCards_OneActivePerNode",
                table: "HKCards");

            migrationBuilder.DropColumn(
                name: "AggregateId",
                table: "HKCards");

            migrationBuilder.DropColumn(
                name: "ComplexId",
                table: "HKCards");

            migrationBuilder.DropColumn(
                name: "EquipmentModelId",
                table: "HKCards");

            migrationBuilder.DropColumn(
                name: "ObjectLevel",
                table: "HKCards");

            migrationBuilder.DropColumn(
                name: "SupersedesHKCardId",
                table: "HKCards");

            migrationBuilder.AlterColumn<Guid>(
                name: "NodeId",
                table: "HKCards",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_HKCards_OneActivePerNode",
                table: "HKCards",
                column: "NodeId",
                unique: true,
                filter: "\"Status\" IN ('Draft', 'OnReview', 'RevisionRequired')");

            migrationBuilder.AddForeignKey(
                name: "FK_HKCards_Nodes_NodeId",
                table: "HKCards",
                column: "NodeId",
                principalTable: "Nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
