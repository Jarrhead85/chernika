using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class D4CorrectiveCalculationSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceHKCardCode",
                table: "IndividualCardItems",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceHKCardId",
                table: "IndividualCardItems",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "SourceHKCardItemId",
                table: "IndividualCardItems",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "SourceHKCardVersion",
                table: "IndividualCardItems",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceHKSourceSnapshotId",
                table: "IndividualCardItems",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "IndividualCardCalculationProblemSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IndividualCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    HKCardId = table.Column<Guid>(type: "uuid", nullable: true),
                    HKCardItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    NodeSnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndividualCardCalculationProblemSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndividualCardCalculationProblemSnapshots_IndividualCards_I~",
                        column: x => x.IndividualCardId,
                        principalTable: "IndividualCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCardCalculationProblemSnapshots_IndividualCardId_SortOrder",
                table: "IndividualCardCalculationProblemSnapshots",
                columns: new[] { "IndividualCardId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IndividualCardCalculationProblemSnapshots");

            migrationBuilder.DropColumn(
                name: "SourceHKCardCode",
                table: "IndividualCardItems");

            migrationBuilder.DropColumn(
                name: "SourceHKCardId",
                table: "IndividualCardItems");

            migrationBuilder.DropColumn(
                name: "SourceHKCardItemId",
                table: "IndividualCardItems");

            migrationBuilder.DropColumn(
                name: "SourceHKCardVersion",
                table: "IndividualCardItems");

            migrationBuilder.DropColumn(
                name: "SourceHKSourceSnapshotId",
                table: "IndividualCardItems");
        }
    }
}
