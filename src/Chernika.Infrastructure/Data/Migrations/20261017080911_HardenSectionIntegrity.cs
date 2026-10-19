using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class HardenSectionIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HKCardItems_HKCardSections_SectionId",
                table: "HKCardItems");

            migrationBuilder.DropForeignKey(
                name: "FK_HKCardNodes_HKCardSections_SectionId",
                table: "HKCardNodes");

            migrationBuilder.DropForeignKey(
                name: "FK_HKCardRevisionItems_HKCardRevisionSections_SectionId",
                table: "HKCardRevisionItems");

            migrationBuilder.DropIndex(
                name: "IX_HKCardRevisionItems_SectionId",
                table: "HKCardRevisionItems");

            migrationBuilder.DropIndex(
                name: "IX_HKCardNodes_SectionId",
                table: "HKCardNodes");

            migrationBuilder.DropIndex(
                name: "IX_HKCardItems_SectionId",
                table: "HKCardItems");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_HKCardSections_HKCardId_Id",
                table: "HKCardSections",
                columns: new[] { "HKCardId", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_HKCardSections_SectionType",
                table: "HKCardSections",
                sql: "\"SectionType\" IN (1, 2, 3)");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardRevisionItems_RevisionId_SectionId",
                table: "HKCardRevisionItems",
                columns: new[] { "RevisionId", "SectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_HKCardNodes_HKCardId_SectionId",
                table: "HKCardNodes",
                columns: new[] { "HKCardId", "SectionId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_HKCardNodes_SectionOnlyTop",
                table: "HKCardNodes",
                sql: "\"SectionId\" IS NULL OR \"ParentId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardItems_HKCardId_SectionId",
                table: "HKCardItems",
                columns: new[] { "HKCardId", "SectionId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_HKCardItems_SectionOnlyRoot",
                table: "HKCardItems",
                sql: "\"SectionId\" IS NULL OR \"HKCardNodeId\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_HKCardItems_HKCardSections_HKCardId_SectionId",
                table: "HKCardItems",
                columns: new[] { "HKCardId", "SectionId" },
                principalTable: "HKCardSections",
                principalColumns: new[] { "HKCardId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_HKCardNodes_HKCardSections_HKCardId_SectionId",
                table: "HKCardNodes",
                columns: new[] { "HKCardId", "SectionId" },
                principalTable: "HKCardSections",
                principalColumns: new[] { "HKCardId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_HKCardRevisionItems_HKCardRevisionSections_RevisionId_Secti~",
                table: "HKCardRevisionItems",
                columns: new[] { "RevisionId", "SectionId" },
                principalTable: "HKCardRevisionSections",
                principalColumns: new[] { "RevisionId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HKCardItems_HKCardSections_HKCardId_SectionId",
                table: "HKCardItems");

            migrationBuilder.DropForeignKey(
                name: "FK_HKCardNodes_HKCardSections_HKCardId_SectionId",
                table: "HKCardNodes");

            migrationBuilder.DropForeignKey(
                name: "FK_HKCardRevisionItems_HKCardRevisionSections_RevisionId_Secti~",
                table: "HKCardRevisionItems");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_HKCardSections_HKCardId_Id",
                table: "HKCardSections");

            migrationBuilder.DropCheckConstraint(
                name: "CK_HKCardSections_SectionType",
                table: "HKCardSections");

            migrationBuilder.DropIndex(
                name: "IX_HKCardRevisionItems_RevisionId_SectionId",
                table: "HKCardRevisionItems");

            migrationBuilder.DropIndex(
                name: "IX_HKCardNodes_HKCardId_SectionId",
                table: "HKCardNodes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_HKCardNodes_SectionOnlyTop",
                table: "HKCardNodes");

            migrationBuilder.DropIndex(
                name: "IX_HKCardItems_HKCardId_SectionId",
                table: "HKCardItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_HKCardItems_SectionOnlyRoot",
                table: "HKCardItems");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardRevisionItems_SectionId",
                table: "HKCardRevisionItems",
                column: "SectionId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardNodes_SectionId",
                table: "HKCardNodes",
                column: "SectionId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardItems_SectionId",
                table: "HKCardItems",
                column: "SectionId");

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

            migrationBuilder.AddForeignKey(
                name: "FK_HKCardRevisionItems_HKCardRevisionSections_SectionId",
                table: "HKCardRevisionItems",
                column: "SectionId",
                principalTable: "HKCardRevisionSections",
                principalColumn: "Id");
        }
    }
}
