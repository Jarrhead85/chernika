using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductCompositionStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Pre-check: duplicate (PartId, NodeId) ───────────────────
            migrationBuilder.Sql(@"
DO $$
DECLARE
    cnt INTEGER;
BEGIN
    SELECT COUNT(*) INTO cnt FROM (
        SELECT ""PartId"", ""NodeId"" FROM ""ProductCompositionNodes""
        GROUP BY ""PartId"", ""NodeId"" HAVING COUNT(*) > 1
    ) d;
    IF cnt > 0 THEN
        RAISE EXCEPTION 'Cannot create unique index IX_ProductCompositionNodes_PartId_NodeId: % duplicate pairs found. Resolve duplicates first.', cnt;
    END IF;
END;
$$;");

            // ── Drop old index ───────────────────────────────────────────
            migrationBuilder.DropIndex(
                name: "IX_ProductCompositionNodes_PartId",
                table: "ProductCompositionNodes");

            // ── Alter Comment column ────────────────────────────────────
            migrationBuilder.AlterColumn<string>(
                name: "Comment",
                table: "ProductCompositions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            // ── Add new columns (non-nullable with placeholder defaults) ─
            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                table: "ProductCompositions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedByUserId",
                table: "ProductCompositions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthorId",
                table: "ProductCompositions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveDate",
                table: "ProductCompositions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpirationDate",
                table: "ProductCompositions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "ProductCompositions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Draft");

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "ProductCompositions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "NOW()");

            migrationBuilder.AddColumn<string>(
                name: "Version",
                table: "ProductCompositions",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            // ── Backfill existing data ──────────────────────────────────
            // Status: IsActive=true → Approved, IsActive=false → Archived
            // Version: v + MMyy from CreatedAt
            // UpdatedAt = CreatedAt
            // AuthorId, ApprovedByUserId, ApprovedAt remain NULL (no historical data)
            //   → AuthorId/ApprovedByUserId are nullable; existing records keep NULL
            //   → the absence of author info is accepted per prompt recommendation
            // IsActive is preserved as-is; will be synced by app logic for the current active approved composition
            migrationBuilder.Sql(@"
UPDATE ""ProductCompositions""
SET ""Status"" = CASE WHEN ""IsActive"" THEN 'Approved' ELSE 'Archived' END,
    ""Version"" = 'v' || TO_CHAR(""CreatedAt"", 'MMyy'),
    ""UpdatedAt"" = ""CreatedAt"";
");

            // ── ProductCompositionId on IndividualCards ─────────────────
            migrationBuilder.AddColumn<Guid>(
                name: "ProductCompositionId",
                table: "IndividualCards",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // ── New indexes ─────────────────────────────────────────────
            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositions_EquipmentModelId_Status",
                table: "ProductCompositions",
                columns: new[] { "EquipmentModelId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositions_Status_EffectiveDate",
                table: "ProductCompositions",
                columns: new[] { "Status", "EffectiveDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionNodes_PartId_NodeId",
                table: "ProductCompositionNodes",
                columns: new[] { "PartId", "NodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCards_ProductCompositionId",
                table: "IndividualCards",
                column: "ProductCompositionId");

            // ── FK: IndividualCard → ProductComposition ─────────────────
            // FK is NOT added in this migration because existing IndividualCards
            // have no ProductCompositionId set (all zeros). Adding the FK would
            // fail on existing rows. The navigation property is managed at the
            // app level in this stage. An FK will be added in a future migration
            // once all pre-migration IndividualCards have been backfilled or
            // the column is made nullable.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProductCompositions_EquipmentModelId_Status",
                table: "ProductCompositions");

            migrationBuilder.DropIndex(
                name: "IX_ProductCompositions_Status_EffectiveDate",
                table: "ProductCompositions");

            migrationBuilder.DropIndex(
                name: "IX_ProductCompositionNodes_PartId_NodeId",
                table: "ProductCompositionNodes");

            migrationBuilder.DropIndex(
                name: "IX_IndividualCards_ProductCompositionId",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "ProductCompositions");

            migrationBuilder.DropColumn(
                name: "ApprovedByUserId",
                table: "ProductCompositions");

            migrationBuilder.DropColumn(
                name: "AuthorId",
                table: "ProductCompositions");

            migrationBuilder.DropColumn(
                name: "EffectiveDate",
                table: "ProductCompositions");

            migrationBuilder.DropColumn(
                name: "ExpirationDate",
                table: "ProductCompositions");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "ProductCompositions");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "ProductCompositions");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ProductCompositions");

            migrationBuilder.DropColumn(
                name: "ProductCompositionId",
                table: "IndividualCards");

            migrationBuilder.AlterColumn<string>(
                name: "Comment",
                table: "ProductCompositions",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductCompositionNodes_PartId",
                table: "ProductCompositionNodes",
                column: "PartId");
        }
    }
}
