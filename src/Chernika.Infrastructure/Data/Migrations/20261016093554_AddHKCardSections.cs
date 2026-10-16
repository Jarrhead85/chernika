using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHKCardSections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SectionId",
                table: "HKCardNodes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SectionId",
                table: "HKCardItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HKCardSections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HKCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    SectionType = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HKCardSections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HKCardSections_HKCards_HKCardId",
                        column: x => x.HKCardId,
                        principalTable: "HKCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HKCardNodes_SectionId",
                table: "HKCardNodes",
                column: "SectionId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardItems_SectionId",
                table: "HKCardItems",
                column: "SectionId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardSections_HKCardId_SortOrder",
                table: "HKCardSections",
                columns: new[] { "HKCardId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "UX_HKCardSections_HKCardId_SectionType",
                table: "HKCardSections",
                columns: new[] { "HKCardId", "SectionType" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_HKCardItems_HKCardSections_SectionId",
                table: "HKCardItems",
                column: "SectionId",
                principalTable: "HKCardSections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_HKCardNodes_HKCardSections_SectionId",
                table: "HKCardNodes",
                column: "SectionId",
                principalTable: "HKCardSections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HKCardItems_HKCardSections_SectionId",
                table: "HKCardItems");

            migrationBuilder.DropForeignKey(
                name: "FK_HKCardNodes_HKCardSections_SectionId",
                table: "HKCardNodes");

            migrationBuilder.DropTable(
                name: "HKCardSections");

            migrationBuilder.DropIndex(
                name: "IX_HKCardNodes_SectionId",
                table: "HKCardNodes");

            migrationBuilder.DropIndex(
                name: "IX_HKCardItems_SectionId",
                table: "HKCardItems");

            migrationBuilder.DropColumn(
                name: "SectionId",
                table: "HKCardNodes");

            migrationBuilder.DropColumn(
                name: "SectionId",
                table: "HKCardItems");
        }
    }
}
