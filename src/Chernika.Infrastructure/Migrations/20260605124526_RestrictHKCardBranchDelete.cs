using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestrictHKCardBranchDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HKCards_Branches_BranchId",
                table: "HKCards");

            migrationBuilder.AddForeignKey(
                name: "FK_HKCards_Branches_BranchId",
                table: "HKCards",
                column: "BranchId",
                principalTable: "Branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HKCards_Branches_BranchId",
                table: "HKCards");

            migrationBuilder.AddForeignKey(
                name: "FK_HKCards_Branches_BranchId",
                table: "HKCards",
                column: "BranchId",
                principalTable: "Branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
