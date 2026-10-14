using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHKCardRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CurrentAcceptedRevisionId",
                table: "HKCards",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HKCardRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HKCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CapturedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    SourceCardRowVersion = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Code = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true),
                    Version = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    ObjectLevel = table.Column<int>(type: "integer", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    NormativeBasis = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RequestOrganization = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RequestSenderFullName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RequestReceivedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RequestDetails = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IncomingLetterNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OutgoingLetterNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    EffectiveDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpirationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    AttachmentOriginalFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    AttachmentStorageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    AttachmentContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AttachmentSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    AttachmentSha256 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AttachmentUploadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HKCardRevisions", x => x.Id);
                    table.UniqueConstraint("AK_HKCardRevisions_HKCardId_Id", x => new { x.HKCardId, x.Id });
                    table.ForeignKey(
                        name: "FK_HKCardRevisions_HKCards_HKCardId",
                        column: x => x.HKCardId,
                        principalTable: "HKCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HKCardRevisionNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HKCardRevisionNodes", x => x.Id);
                    table.UniqueConstraint("AK_HKCardRevisionNodes_RevisionId_Id", x => new { x.RevisionId, x.Id });
                    table.ForeignKey(
                        name: "FK_HKCardRevisionNodes_HKCardRevisionNodes_RevisionId_ParentId",
                        columns: x => new { x.RevisionId, x.ParentId },
                        principalTable: "HKCardRevisionNodes",
                        principalColumns: new[] { "RevisionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HKCardRevisionNodes_HKCardRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "HKCardRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HKCardRevisionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssemblyUnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssemblyUnitCode = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AssemblyUnitName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Volume = table.Column<decimal>(type: "numeric", nullable: false),
                    UnitOfMeasure = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Periodicity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HKCardRevisionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HKCardRevisionItems_HKCardRevisionNodes_RevisionId_NodeId",
                        columns: x => new { x.RevisionId, x.NodeId },
                        principalTable: "HKCardRevisionNodes",
                        principalColumns: new[] { "RevisionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HKCardRevisionItems_HKCardRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "HKCardRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HKCardRevisionItemMaterials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceGsmMaterialId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Nd = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HKCardRevisionItemMaterials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HKCardRevisionItemMaterials_HKCardRevisionItems_RevisionIte~",
                        column: x => x.RevisionItemId,
                        principalTable: "HKCardRevisionItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HKCards_Id_CurrentAcceptedRevisionId",
                table: "HKCards",
                columns: new[] { "Id", "CurrentAcceptedRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_HKCardRevisionItemMaterials_RevisionItemId",
                table: "HKCardRevisionItemMaterials",
                column: "RevisionItemId");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardRevisionItems_RevisionId_NodeId",
                table: "HKCardRevisionItems",
                columns: new[] { "RevisionId", "NodeId" });

            migrationBuilder.CreateIndex(
                name: "UX_HKCardRevisionItems_Revision_Id",
                table: "HKCardRevisionItems",
                columns: new[] { "RevisionId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HKCardRevisionNodes_RevisionId_ParentId",
                table: "HKCardRevisionNodes",
                columns: new[] { "RevisionId", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "UX_HKCardRevisionNodes_Revision_Id",
                table: "HKCardRevisionNodes",
                columns: new[] { "RevisionId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_HKCardRevisions_Card_Id",
                table: "HKCardRevisions",
                columns: new[] { "HKCardId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_HKCardRevisions_Card_Number",
                table: "HKCardRevisions",
                columns: new[] { "HKCardId", "RevisionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_HKCards_HKCardRevisions_Id_CurrentAcceptedRevisionId",
                table: "HKCards",
                columns: new[] { "Id", "CurrentAcceptedRevisionId" },
                principalTable: "HKCardRevisions",
                principalColumns: new[] { "HKCardId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HKCards_HKCardRevisions_Id_CurrentAcceptedRevisionId",
                table: "HKCards");

            migrationBuilder.DropTable(
                name: "HKCardRevisionItemMaterials");

            migrationBuilder.DropTable(
                name: "HKCardRevisionItems");

            migrationBuilder.DropTable(
                name: "HKCardRevisionNodes");

            migrationBuilder.DropTable(
                name: "HKCardRevisions");

            migrationBuilder.DropIndex(
                name: "IX_HKCards_Id_CurrentAcceptedRevisionId",
                table: "HKCards");

            migrationBuilder.DropColumn(
                name: "CurrentAcceptedRevisionId",
                table: "HKCards");
        }
    }
}
