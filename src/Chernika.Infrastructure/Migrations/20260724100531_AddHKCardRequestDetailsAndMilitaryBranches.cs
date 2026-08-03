using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHKCardRequestDetailsAndMilitaryBranches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IncomingLetterNumber",
                table: "HKCards",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutgoingLetterNumber",
                table: "HKCards",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestDetails",
                table: "HKCards",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestOrganization",
                table: "HKCards",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RequestReceivedDate",
                table: "HKCards",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestSenderFullName",
                table: "HKCards",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HKCardAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HKCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UploadedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    UploadedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HKCardAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HKCardAttachments_HKCards_HKCardId",
                        column: x => x.HKCardId,
                        principalTable: "HKCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MilitaryBranches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MilitaryBranches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HKCardMilitaryBranches",
                columns: table => new
                {
                    HKCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    MilitaryBranchId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HKCardMilitaryBranches", x => new { x.HKCardId, x.MilitaryBranchId });
                    table.ForeignKey(
                        name: "FK_HKCardMilitaryBranches_HKCards_HKCardId",
                        column: x => x.HKCardId,
                        principalTable: "HKCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HKCardMilitaryBranches_MilitaryBranches_MilitaryBranchId",
                        column: x => x.MilitaryBranchId,
                        principalTable: "MilitaryBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HKCardAttachments_HKCardId",
                table: "HKCardAttachments",
                column: "HKCardId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HKCardMilitaryBranches_MilitaryBranchId",
                table: "HKCardMilitaryBranches",
                column: "MilitaryBranchId");

            migrationBuilder.CreateIndex(
                name: "IX_MilitaryBranches_Code",
                table: "MilitaryBranches",
                column: "Code",
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HKCardAttachments");

            migrationBuilder.DropTable(
                name: "HKCardMilitaryBranches");

            migrationBuilder.DropTable(
                name: "MilitaryBranches");

            migrationBuilder.DropColumn(
                name: "IncomingLetterNumber",
                table: "HKCards");

            migrationBuilder.DropColumn(
                name: "OutgoingLetterNumber",
                table: "HKCards");

            migrationBuilder.DropColumn(
                name: "RequestDetails",
                table: "HKCards");

            migrationBuilder.DropColumn(
                name: "RequestOrganization",
                table: "HKCards");

            migrationBuilder.DropColumn(
                name: "RequestReceivedDate",
                table: "HKCards");

            migrationBuilder.DropColumn(
                name: "RequestSenderFullName",
                table: "HKCards");
        }
    }
}
