using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class D3DraftSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "IndividualCardHKSourceSnapshots",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "Quantity",
                table: "IndividualCardCompositionSnapshots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "IndividualCardNormativeGapSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IndividualCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    RelatedLevel = table.Column<int>(type: "integer", nullable: false),
                    RelatedObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    RelatedObjectType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RelatedObjectCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RelatedObjectName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RelatedHKCardId = table.Column<Guid>(type: "uuid", nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndividualCardNormativeGapSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndividualCardNormativeGapSnapshots_IndividualCards_Individ~",
                        column: x => x.IndividualCardId,
                        principalTable: "IndividualCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCardNormativeGapSnapshots_IndividualCardId_SortOrder",
                table: "IndividualCardNormativeGapSnapshots",
                columns: new[] { "IndividualCardId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IndividualCardNormativeGapSnapshots");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "IndividualCardHKSourceSnapshots");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "IndividualCardCompositionSnapshots");
        }
    }
}
