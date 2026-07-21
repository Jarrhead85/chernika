using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPartialUniqueIndexesForAllObjectLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HKCards_AggregateId",
                table: "HKCards");

            migrationBuilder.DropIndex(
                name: "IX_HKCards_ComplexId",
                table: "HKCards");

            migrationBuilder.DropIndex(
                name: "IX_HKCards_EquipmentModelId",
                table: "HKCards");

            migrationBuilder.CreateIndex(
                name: "UX_HKCards_OneActivePerAggregate",
                table: "HKCards",
                column: "AggregateId",
                unique: true,
                filter: "\"ObjectLevel\" = 3 AND \"Status\" IN ('Draft', 'OnReview', 'RevisionRequired')");

            migrationBuilder.CreateIndex(
                name: "UX_HKCards_OneActivePerComplex",
                table: "HKCards",
                column: "ComplexId",
                unique: true,
                filter: "\"ObjectLevel\" = 1 AND \"Status\" IN ('Draft', 'OnReview', 'RevisionRequired')");

            migrationBuilder.CreateIndex(
                name: "UX_HKCards_OneActivePerEquipmentModel",
                table: "HKCards",
                column: "EquipmentModelId",
                unique: true,
                filter: "\"ObjectLevel\" = 2 AND \"Status\" IN ('Draft', 'OnReview', 'RevisionRequired')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_HKCards_OneActivePerAggregate",
                table: "HKCards");

            migrationBuilder.DropIndex(
                name: "UX_HKCards_OneActivePerComplex",
                table: "HKCards");

            migrationBuilder.DropIndex(
                name: "UX_HKCards_OneActivePerEquipmentModel",
                table: "HKCards");

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
        }
    }
}
