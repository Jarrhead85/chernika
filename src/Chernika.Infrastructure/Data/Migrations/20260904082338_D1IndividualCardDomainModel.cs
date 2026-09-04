using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class D1IndividualCardDomainModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_IndividualCardItems_HKCardItems_HKCardItemId",
                table: "IndividualCardItems");

            migrationBuilder.DropForeignKey(
                name: "FK_IndividualCards_EquipmentInstances_EquipmentInstanceId",
                table: "IndividualCards");

            migrationBuilder.DropForeignKey(
                name: "FK_IndividualCards_Nodes_NodeId",
                table: "IndividualCards");

            migrationBuilder.DropIndex(
                name: "IX_IndividualCardItems_IndividualCardId",
                table: "IndividualCardItems");

            migrationBuilder.AlterColumn<string>(
                name: "Version",
                table: "IndividualCards",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.AlterColumn<Guid>(
                name: "ProductCompositionId",
                table: "IndividualCards",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "NodeId",
                table: "IndividualCards",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "HKCardId",
                table: "IndividualCards",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "EquipmentInstanceId",
                table: "IndividualCards",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "AggregateId",
                table: "IndividualCards",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "IndividualCards",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArchivedByUserId",
                table: "IndividualCards",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "IndividualCards",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "IndividualCards",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ComplexId",
                table: "IndividualCards",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserId",
                table: "IndividualCards",
                type: "character varying(450)",
                maxLength: 450,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "EquipmentModelId",
                table: "IndividualCards",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FormedAt",
                table: "IndividualCards",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FormedByUserId",
                table: "IndividualCards",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ObjectLevel",
                table: "IndividualCards",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RevisionNumber",
                table: "IndividualCards",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "IndividualCards",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersedesIndividualCardId",
                table: "IndividualCards",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "HKCardItemId",
                table: "IndividualCardItems",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "AssemblyUnitCode",
                table: "IndividualCardItems",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AssemblyUnitName",
                table: "IndividualCardItems",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "AssemblyUnitQuantity",
                table: "IndividualCardItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "NodeSnapshotId",
                table: "IndividualCardItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "IndividualCardItems",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Periodicity",
                table: "IndividualCardItems",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "IndividualCardItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "SourceVolume",
                table: "IndividualCardItems",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "UnitOfMeasure",
                table: "IndividualCardItems",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            // ── D1 legacy backfill ──────────────────────────────────────────
            // Deterministically derives the new D1 metadata for any legacy rows.
            // Legacy rows are EquipmentInstance-scoped cards (ObjectLevel = 5);
            // Version values are never rewritten; historical links stay intact.
            // The dev/prod data inspected before this migration contains 0 legacy
            // rows, so all statements below are no-ops in practice, but they keep
            // the migration executable and safe on databases with legacy data.
            migrationBuilder.Sql("""
                -- Backfill status metadata for legacy rows.
                UPDATE "IndividualCards" ic
                SET "ObjectLevel" = 5, "Status" = 1, "RevisionNumber" = 1
                WHERE ic."ObjectLevel" = 0 AND ic."EquipmentInstanceId" IS NOT NULL;

                -- Backfill BranchId from the legacy source HKCard (unambiguous link only).
                UPDATE "IndividualCards" ic
                SET "BranchId" = h."BranchId"
                FROM "HKCards" h
                WHERE ic."HKCardId" = h."Id" AND ic."BranchId" = '00000000-0000-0000-0000-000000000000';

                -- Backfill Code from the linked EquipmentInstance serial number and creation year.
                UPDATE "IndividualCards" ic
                SET "Code" = 'ИК-ЭКЗ-' || ei."SerialNumber" || '-' || to_char(ic."CreatedAt" AT TIME ZONE 'UTC', 'YYYY')
                FROM "EquipmentInstances" ei
                WHERE ic."EquipmentInstanceId" = ei."Id" AND ic."Code" = '';

                -- Disambiguate duplicate codes with a deterministic numeric suffix.
                UPDATE "IndividualCards" ic
                SET "Code" = ic."Code" || '-' || t.rn
                FROM (
                    SELECT "Id",
                           row_number() OVER (PARTITION BY "Code" ORDER BY "Id") - 1 AS rn
                    FROM "IndividualCards"
                    WHERE "Code" <> ''
                ) t
                WHERE ic."Id" = t."Id" AND t.rn > 0;

                -- Diagnostic guard: fail descriptively instead of inventing a BranchId.
                DO $guard$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "IndividualCards"
                        WHERE "BranchId" = '00000000-0000-0000-0000-000000000000'
                    ) THEN
                        RAISE EXCEPTION 'D1 backfill: не удалось определить филиал для части legacy ИК (нет связанной ХК). Требуется ручная миграция.';
                    END IF;
                END
                $guard$;

                -- Diagnostic guard: legacy rows carry both EquipmentInstanceId and NodeId;
                -- the D1 target check requires NodeId IS NULL for EquipmentInstance level.
                -- Nulling legacy NodeId values is a business decision, so fail descriptively.
                DO $guard$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "IndividualCards"
                        WHERE "ObjectLevel" = 5 AND "NodeId" IS NOT NULL
                    ) THEN
                        RAISE EXCEPTION 'D1 backfill: legacy ИК содержат заполненный NodeId при уровне «Экземпляр техники». Очистка NodeId требует отдельного решения (cleanup-миграция).';
                    END IF;
                END
                $guard$;
                """);

            // ── D1 PostgreSQL check constraints ─────────────────────────────
            migrationBuilder.Sql("""
                ALTER TABLE "IndividualCards" ADD CONSTRAINT "CK_IndividualCards_TargetMatchesLevel"
                CHECK (
                    ("ObjectLevel" = 1 AND "ComplexId" IS NOT NULL
                       AND "EquipmentModelId" IS NULL AND "AggregateId" IS NULL
                       AND "NodeId" IS NULL AND "EquipmentInstanceId" IS NULL)
                 OR ("ObjectLevel" = 2 AND "EquipmentModelId" IS NOT NULL
                       AND "ComplexId" IS NULL AND "AggregateId" IS NULL
                       AND "NodeId" IS NULL AND "EquipmentInstanceId" IS NULL)
                 OR ("ObjectLevel" = 3 AND "AggregateId" IS NOT NULL
                       AND "ComplexId" IS NULL AND "EquipmentModelId" IS NULL
                       AND "NodeId" IS NULL AND "EquipmentInstanceId" IS NULL)
                 OR ("ObjectLevel" = 4 AND "NodeId" IS NOT NULL
                       AND "ComplexId" IS NULL AND "EquipmentModelId" IS NULL
                       AND "AggregateId" IS NULL AND "EquipmentInstanceId" IS NULL)
                 OR ("ObjectLevel" = 5 AND "EquipmentInstanceId" IS NOT NULL
                       AND "ComplexId" IS NULL AND "EquipmentModelId" IS NULL
                       AND "AggregateId" IS NULL AND "NodeId" IS NULL)
                );

                ALTER TABLE "IndividualCards" ADD CONSTRAINT "CK_IndividualCards_StatusMetadata"
                CHECK (
                    ("Status" = 1
                       AND "FormedAt" IS NULL AND "FormedByUserId" IS NULL
                       AND "ArchivedAt" IS NULL AND "ArchivedByUserId" IS NULL)
                 OR ("Status" = 2
                       AND "FormedAt" IS NOT NULL AND "FormedByUserId" IS NOT NULL
                       AND "ArchivedAt" IS NULL AND "ArchivedByUserId" IS NULL)
                 OR ("Status" = 3
                       AND "FormedAt" IS NOT NULL AND "FormedByUserId" IS NOT NULL
                       AND "ArchivedAt" IS NOT NULL AND "ArchivedByUserId" IS NOT NULL)
                );

                ALTER TABLE "IndividualCards" ADD CONSTRAINT "CK_IndividualCards_RevisionPositive"
                CHECK ("RevisionNumber" > 0);
                """);

            migrationBuilder.CreateTable(
                name: "IndividualCardCoefficientSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IndividualCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceCoefficientId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceCoefficientTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CoefficientTypeName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CoefficientName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Value = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    ConditionDescription = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    NormativeBasis = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndividualCardCoefficientSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndividualCardCoefficientSnapshots_IndividualCards_Individu~",
                        column: x => x.IndividualCardId,
                        principalTable: "IndividualCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IndividualCardCompositionSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IndividualCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceLevel = table.Column<int>(type: "integer", nullable: false),
                    SourceCompositionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceCompositionVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TargetObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetObjectCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TargetObjectName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndividualCardCompositionSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndividualCardCompositionSnapshots_IndividualCards_Individu~",
                        column: x => x.IndividualCardId,
                        principalTable: "IndividualCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IndividualCardHKSourceSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IndividualCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentHKSourceSnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceHKCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObjectLevel = table.Column<int>(type: "integer", nullable: false),
                    SourceObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceObjectCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SourceObjectName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    HKCardCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    HKCardVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    HKCardApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HKCardEffectiveDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HKCardExpirationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndividualCardHKSourceSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndividualCardHKSourceSnapshots_IndividualCardHKSourceSnaps~",
                        column: x => x.ParentHKSourceSnapshotId,
                        principalTable: "IndividualCardHKSourceSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IndividualCardHKSourceSnapshots_IndividualCards_IndividualC~",
                        column: x => x.IndividualCardId,
                        principalTable: "IndividualCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IndividualCardItemMaterialSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IndividualCardItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceGsmMaterialId = table.Column<Guid>(type: "uuid", nullable: false),
                    MaterialName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MaterialType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Gost = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    CalculatedVolume = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    UnitOfMeasure = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndividualCardItemMaterialSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndividualCardItemMaterialSnapshots_IndividualCardItems_Ind~",
                        column: x => x.IndividualCardItemId,
                        principalTable: "IndividualCardItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IndividualCardAggregateSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IndividualCardCompositionSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    AggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                    AggregateCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AggregateName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndividualCardAggregateSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndividualCardAggregateSnapshots_IndividualCardCompositionS~",
                        column: x => x.IndividualCardCompositionSnapshotId,
                        principalTable: "IndividualCardCompositionSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IndividualCardNodeSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IndividualCardAggregateSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NodeName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndividualCardNodeSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndividualCardNodeSnapshots_IndividualCardAggregateSnapshot~",
                        column: x => x.IndividualCardAggregateSnapshotId,
                        principalTable: "IndividualCardAggregateSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCards_AggregateId",
                table: "IndividualCards",
                column: "AggregateId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCards_BranchId_Status",
                table: "IndividualCards",
                columns: new[] { "BranchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCards_ComplexId",
                table: "IndividualCards",
                column: "ComplexId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCards_CreatedAt",
                table: "IndividualCards",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCards_EquipmentModelId",
                table: "IndividualCards",
                column: "EquipmentModelId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCards_FormedAt",
                table: "IndividualCards",
                column: "FormedAt");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCards_SupersedesIndividualCardId",
                table: "IndividualCards",
                column: "SupersedesIndividualCardId");

            migrationBuilder.CreateIndex(
                name: "UX_IndividualCards_Code_Version",
                table: "IndividualCards",
                columns: new[] { "Code", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCardItems_IndividualCardId_SortOrder",
                table: "IndividualCardItems",
                columns: new[] { "IndividualCardId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCardAggregateSnapshots_CompositionSnapshotId_SortOrder",
                table: "IndividualCardAggregateSnapshots",
                columns: new[] { "IndividualCardCompositionSnapshotId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCardCoefficientSnapshots_IndividualCardId_SortOrder",
                table: "IndividualCardCoefficientSnapshots",
                columns: new[] { "IndividualCardId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCardCompositionSnapshots_IndividualCardId",
                table: "IndividualCardCompositionSnapshots",
                column: "IndividualCardId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCardHKSourceSnapshots_IndividualCardId_SortOrder",
                table: "IndividualCardHKSourceSnapshots",
                columns: new[] { "IndividualCardId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCardHKSourceSnapshots_ParentHKSourceSnapshotId",
                table: "IndividualCardHKSourceSnapshots",
                column: "ParentHKSourceSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCardItemMaterialSnapshots_IndividualCardItemId_SortOrder",
                table: "IndividualCardItemMaterialSnapshots",
                columns: new[] { "IndividualCardItemId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCardNodeSnapshots_AggregateSnapshotId_SortOrder",
                table: "IndividualCardNodeSnapshots",
                columns: new[] { "IndividualCardAggregateSnapshotId", "SortOrder" });

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualCardItems_HKCardItems_HKCardItemId",
                table: "IndividualCardItems",
                column: "HKCardItemId",
                principalTable: "HKCardItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualCards_Aggregates_AggregateId",
                table: "IndividualCards",
                column: "AggregateId",
                principalTable: "Aggregates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualCards_Branches_BranchId",
                table: "IndividualCards",
                column: "BranchId",
                principalTable: "Branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualCards_Complexes_ComplexId",
                table: "IndividualCards",
                column: "ComplexId",
                principalTable: "Complexes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualCards_EquipmentInstances_EquipmentInstanceId",
                table: "IndividualCards",
                column: "EquipmentInstanceId",
                principalTable: "EquipmentInstances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualCards_EquipmentModels_EquipmentModelId",
                table: "IndividualCards",
                column: "EquipmentModelId",
                principalTable: "EquipmentModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualCards_IndividualCards_SupersedesIndividualCardId",
                table: "IndividualCards",
                column: "SupersedesIndividualCardId",
                principalTable: "IndividualCards",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualCards_Nodes_NodeId",
                table: "IndividualCards",
                column: "NodeId",
                principalTable: "Nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "IndividualCards" DROP CONSTRAINT IF EXISTS "CK_IndividualCards_TargetMatchesLevel";
                ALTER TABLE "IndividualCards" DROP CONSTRAINT IF EXISTS "CK_IndividualCards_StatusMetadata";
                ALTER TABLE "IndividualCards" DROP CONSTRAINT IF EXISTS "CK_IndividualCards_RevisionPositive";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_IndividualCardItems_HKCardItems_HKCardItemId",
                table: "IndividualCardItems");

            migrationBuilder.DropForeignKey(
                name: "FK_IndividualCards_Aggregates_AggregateId",
                table: "IndividualCards");

            migrationBuilder.DropForeignKey(
                name: "FK_IndividualCards_Branches_BranchId",
                table: "IndividualCards");

            migrationBuilder.DropForeignKey(
                name: "FK_IndividualCards_Complexes_ComplexId",
                table: "IndividualCards");

            migrationBuilder.DropForeignKey(
                name: "FK_IndividualCards_EquipmentInstances_EquipmentInstanceId",
                table: "IndividualCards");

            migrationBuilder.DropForeignKey(
                name: "FK_IndividualCards_EquipmentModels_EquipmentModelId",
                table: "IndividualCards");

            migrationBuilder.DropForeignKey(
                name: "FK_IndividualCards_IndividualCards_SupersedesIndividualCardId",
                table: "IndividualCards");

            migrationBuilder.DropForeignKey(
                name: "FK_IndividualCards_Nodes_NodeId",
                table: "IndividualCards");

            migrationBuilder.DropTable(
                name: "IndividualCardCoefficientSnapshots");

            migrationBuilder.DropTable(
                name: "IndividualCardHKSourceSnapshots");

            migrationBuilder.DropTable(
                name: "IndividualCardItemMaterialSnapshots");

            migrationBuilder.DropTable(
                name: "IndividualCardNodeSnapshots");

            migrationBuilder.DropTable(
                name: "IndividualCardAggregateSnapshots");

            migrationBuilder.DropTable(
                name: "IndividualCardCompositionSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_IndividualCards_AggregateId",
                table: "IndividualCards");

            migrationBuilder.DropIndex(
                name: "IX_IndividualCards_BranchId_Status",
                table: "IndividualCards");

            migrationBuilder.DropIndex(
                name: "IX_IndividualCards_ComplexId",
                table: "IndividualCards");

            migrationBuilder.DropIndex(
                name: "IX_IndividualCards_CreatedAt",
                table: "IndividualCards");

            migrationBuilder.DropIndex(
                name: "IX_IndividualCards_EquipmentModelId",
                table: "IndividualCards");

            migrationBuilder.DropIndex(
                name: "IX_IndividualCards_FormedAt",
                table: "IndividualCards");

            migrationBuilder.DropIndex(
                name: "IX_IndividualCards_SupersedesIndividualCardId",
                table: "IndividualCards");

            migrationBuilder.DropIndex(
                name: "UX_IndividualCards_Code_Version",
                table: "IndividualCards");

            migrationBuilder.DropIndex(
                name: "IX_IndividualCardItems_IndividualCardId_SortOrder",
                table: "IndividualCardItems");

            migrationBuilder.DropColumn(
                name: "AggregateId",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "ArchivedByUserId",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "ComplexId",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "EquipmentModelId",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "FormedAt",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "FormedByUserId",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "ObjectLevel",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "RevisionNumber",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "SupersedesIndividualCardId",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "AssemblyUnitCode",
                table: "IndividualCardItems");

            migrationBuilder.DropColumn(
                name: "AssemblyUnitName",
                table: "IndividualCardItems");

            migrationBuilder.DropColumn(
                name: "AssemblyUnitQuantity",
                table: "IndividualCardItems");

            migrationBuilder.DropColumn(
                name: "NodeSnapshotId",
                table: "IndividualCardItems");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "IndividualCardItems");

            migrationBuilder.DropColumn(
                name: "Periodicity",
                table: "IndividualCardItems");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "IndividualCardItems");

            migrationBuilder.DropColumn(
                name: "SourceVolume",
                table: "IndividualCardItems");

            migrationBuilder.DropColumn(
                name: "UnitOfMeasure",
                table: "IndividualCardItems");

            migrationBuilder.AlterColumn<string>(
                name: "Version",
                table: "IndividualCards",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<Guid>(
                name: "ProductCompositionId",
                table: "IndividualCards",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "NodeId",
                table: "IndividualCards",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "HKCardId",
                table: "IndividualCards",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "EquipmentInstanceId",
                table: "IndividualCards",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "HKCardItemId",
                table: "IndividualCardItems",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_IndividualCardItems_IndividualCardId",
                table: "IndividualCardItems",
                column: "IndividualCardId");

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualCardItems_HKCardItems_HKCardItemId",
                table: "IndividualCardItems",
                column: "HKCardItemId",
                principalTable: "HKCardItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualCards_EquipmentInstances_EquipmentInstanceId",
                table: "IndividualCards",
                column: "EquipmentInstanceId",
                principalTable: "EquipmentInstances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualCards_Nodes_NodeId",
                table: "IndividualCards",
                column: "NodeId",
                principalTable: "Nodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
