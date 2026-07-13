using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveHKCardFromProductCompositionNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProductCompositionNodes_HKCards_HKCardId",
                table: "ProductCompositionNodes");

            migrationBuilder.DropIndex(
                name: "IX_ProductCompositionNodes_HKCardId",
                table: "ProductCompositionNodes");

            migrationBuilder.DropColumn(
                name: "HKCardId",
                table: "ProductCompositionNodes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HKCardId",
                table: "ProductCompositionNodes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionNodes_HKCardId",
                table: "ProductCompositionNodes",
                column: "HKCardId");

            migrationBuilder.AddForeignKey(
                name: "FK_ProductCompositionNodes_HKCards_HKCardId",
                table: "ProductCompositionNodes",
                column: "HKCardId",
                principalTable: "HKCards",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
