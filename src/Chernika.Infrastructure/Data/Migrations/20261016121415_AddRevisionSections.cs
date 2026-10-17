using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRevisionSections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SectionId",
                table: "HKCardRevisionNodes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SectionId",
                table: "HKCardRevisionItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HKCardRevisionSections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SectionType = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HKCardRevisionSections", x => x.Id);
                    table.UniqueConstraint("AK_HKCardRevisionSections_RevisionId_Id", x => new { x.RevisionId, x.Id });
                    table.ForeignKey(
                        name: "FK_HKCardRevisionSections_HKCardRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "HKCardRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HKCardRevisionNodes_RevisionId_SectionId",
                table: "HKCardRevisionNodes",
                columns: new[] { "RevisionId", "SectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_HKCardRevisionItems_SectionId",
                table: "HKCardRevisionItems",
                column: "SectionId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardRevisionSections_RevisionId_SortOrder",
                table: "HKCardRevisionSections",
                columns: new[] { "RevisionId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "UX_HKCardRevisionSections_RevisionId_SectionType",
                table: "HKCardRevisionSections",
                columns: new[] { "RevisionId", "SectionType" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_HKCardRevisionItems_HKCardRevisionSections_SectionId",
                table: "HKCardRevisionItems",
                column: "SectionId",
                principalTable: "HKCardRevisionSections",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_HKCardRevisionNodes_HKCardRevisionSections_RevisionId_Secti~",
                table: "HKCardRevisionNodes",
                columns: new[] { "RevisionId", "SectionId" },
                principalTable: "HKCardRevisionSections",
                principalColumns: new[] { "RevisionId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HKCardRevisionItems_HKCardRevisionSections_SectionId",
                table: "HKCardRevisionItems");

            migrationBuilder.DropForeignKey(
                name: "FK_HKCardRevisionNodes_HKCardRevisionSections_RevisionId_Secti~",
                table: "HKCardRevisionNodes");

            migrationBuilder.DropTable(
                name: "HKCardRevisionSections");

            migrationBuilder.DropIndex(
                name: "IX_HKCardRevisionNodes_RevisionId_SectionId",
                table: "HKCardRevisionNodes");

            migrationBuilder.DropIndex(
                name: "IX_HKCardRevisionItems_SectionId",
                table: "HKCardRevisionItems");

            migrationBuilder.DropColumn(
                name: "SectionId",
                table: "HKCardRevisionNodes");

            migrationBuilder.DropColumn(
                name: "SectionId",
                table: "HKCardRevisionItems");
        }
    }
}
