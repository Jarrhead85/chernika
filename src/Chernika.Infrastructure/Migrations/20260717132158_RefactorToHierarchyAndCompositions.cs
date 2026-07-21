using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RefactorToHierarchyAndCompositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductCompositionNodes");

            migrationBuilder.CreateTable(
                name: "Aggregates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Aggregates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Complexes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Complexes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AggregateCompositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EffectiveDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpirationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AuthorId = table.Column<string>(type: "text", nullable: true),
                    ApprovedByUserId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AggregateCompositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AggregateCompositions_Aggregates_AggregateId",
                        column: x => x.AggregateId,
                        principalTable: "Aggregates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductCompositionAggregates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    AggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCompositionAggregates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductCompositionAggregates_Aggregates_AggregateId",
                        column: x => x.AggregateId,
                        principalTable: "Aggregates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductCompositionAggregates_ProductCompositionParts_PartId",
                        column: x => x.PartId,
                        principalTable: "ProductCompositionParts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ComplexCompositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ComplexId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EffectiveDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpirationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AuthorId = table.Column<string>(type: "text", nullable: true),
                    ApprovedByUserId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplexCompositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComplexCompositions_Complexes_ComplexId",
                        column: x => x.ComplexId,
                        principalTable: "Complexes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AggregateCompositionNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AggregateCompositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AggregateCompositionNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AggregateCompositionNodes_AggregateCompositions_AggregateCo~",
                        column: x => x.AggregateCompositionId,
                        principalTable: "AggregateCompositions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AggregateCompositionNodes_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ComplexCompositionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ComplexCompositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EquipmentModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplexCompositionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComplexCompositionItems_ComplexCompositions_ComplexComposit~",
                        column: x => x.ComplexCompositionId,
                        principalTable: "ComplexCompositions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ComplexCompositionItems_EquipmentModels_EquipmentModelId",
                        column: x => x.EquipmentModelId,
                        principalTable: "EquipmentModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AggregateCompositionNodes_AggregateCompositionId_NodeId",
                table: "AggregateCompositionNodes",
                columns: new[] { "AggregateCompositionId", "NodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AggregateCompositionNodes_NodeId",
                table: "AggregateCompositionNodes",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_AggregateCompositions_AggregateId_IsActive",
                table: "AggregateCompositions",
                columns: new[] { "AggregateId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_AggregateCompositions_AggregateId_Status",
                table: "AggregateCompositions",
                columns: new[] { "AggregateId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AggregateCompositions_Status_EffectiveDate",
                table: "AggregateCompositions",
                columns: new[] { "Status", "EffectiveDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Aggregates_Code",
                table: "Aggregates",
                column: "Code",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_ComplexCompositionItems_ComplexCompositionId_EquipmentModel~",
                table: "ComplexCompositionItems",
                columns: new[] { "ComplexCompositionId", "EquipmentModelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComplexCompositionItems_EquipmentModelId",
                table: "ComplexCompositionItems",
                column: "EquipmentModelId");

            migrationBuilder.CreateIndex(
                name: "IX_ComplexCompositions_ComplexId_IsActive",
                table: "ComplexCompositions",
                columns: new[] { "ComplexId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ComplexCompositions_ComplexId_Status",
                table: "ComplexCompositions",
                columns: new[] { "ComplexId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ComplexCompositions_Status_EffectiveDate",
                table: "ComplexCompositions",
                columns: new[] { "Status", "EffectiveDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Complexes_Code",
                table: "Complexes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionAggregates_AggregateId",
                table: "ProductCompositionAggregates",
                column: "AggregateId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionAggregates_PartId_AggregateId",
                table: "ProductCompositionAggregates",
                columns: new[] { "PartId", "AggregateId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AggregateCompositionNodes");

            migrationBuilder.DropTable(
                name: "ComplexCompositionItems");

            migrationBuilder.DropTable(
                name: "ProductCompositionAggregates");

            migrationBuilder.DropTable(
                name: "AggregateCompositions");

            migrationBuilder.DropTable(
                name: "ComplexCompositions");

            migrationBuilder.DropTable(
                name: "Aggregates");

            migrationBuilder.DropTable(
                name: "Complexes");

            migrationBuilder.CreateTable(
                name: "ProductCompositionNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCompositionNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductCompositionNodes_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductCompositionNodes_ProductCompositionParts_PartId",
                        column: x => x.PartId,
                        principalTable: "ProductCompositionParts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionNodes_NodeId",
                table: "ProductCompositionNodes",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionNodes_PartId_NodeId",
                table: "ProductCompositionNodes",
                columns: new[] { "PartId", "NodeId" },
                unique: true);
        }
    }
}
