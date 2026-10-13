using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHKCardApprovalStages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HKCardApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HKCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    AssignedApproverUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HKCardApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HKCardApprovals_HKCards_HKCardId",
                        column: x => x.HKCardId,
                        principalTable: "HKCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_HKCardApprovals_Card_Stage",
                table: "HKCardApprovals",
                columns: new[] { "HKCardId", "Stage" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HKCardApprovals");
        }
    }
}
