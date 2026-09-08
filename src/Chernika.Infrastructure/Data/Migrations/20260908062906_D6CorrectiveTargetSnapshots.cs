using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class D6CorrectiveTargetSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetContextSnapshot",
                table: "IndividualCards",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetObjectCodeSnapshot",
                table: "IndividualCards",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TargetObjectNameSnapshot",
                table: "IndividualCards",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            // Legacy backfill from the structural target rows. Only existing
            // targets are resolved; when a target row is missing, an explicit
            // historical marker is written (never an invented code/name).
            // Only IndividualCards and their target tables are touched.
            migrationBuilder.Sql("""
                UPDATE "IndividualCards" c
                SET "TargetObjectCodeSnapshot" = t."Code",
                    "TargetObjectNameSnapshot" = t."Name"
                FROM "Complexes" t
                WHERE c."ObjectLevel" = 1 AND c."ComplexId" = t."Id"
                  AND c."TargetObjectCodeSnapshot" = '';

                UPDATE "IndividualCards" c
                SET "TargetObjectCodeSnapshot" = t."Index",
                    "TargetObjectNameSnapshot" = t."Name"
                FROM "EquipmentModels" t
                WHERE c."ObjectLevel" = 2 AND c."EquipmentModelId" = t."Id"
                  AND c."TargetObjectCodeSnapshot" = '';

                UPDATE "IndividualCards" c
                SET "TargetObjectCodeSnapshot" = t."Code",
                    "TargetObjectNameSnapshot" = t."Name"
                FROM "Aggregates" t
                WHERE c."ObjectLevel" = 3 AND c."AggregateId" = t."Id"
                  AND c."TargetObjectCodeSnapshot" = '';

                UPDATE "IndividualCards" c
                SET "TargetObjectCodeSnapshot" = t."Code",
                    "TargetObjectNameSnapshot" = t."Name"
                FROM "Nodes" t
                WHERE c."ObjectLevel" = 4 AND c."NodeId" = t."Id"
                  AND c."TargetObjectCodeSnapshot" = '';

                UPDATE "IndividualCards" c
                SET "TargetObjectCodeSnapshot" = t."SerialNumber",
                    "TargetObjectNameSnapshot" = t."Name",
                    "TargetContextSnapshot" = CASE WHEN t."Index" IS NULL OR t."Index" = '' THEN NULL ELSE 'Изделие ' || t."Index" END
                FROM "EquipmentInstances" t
                WHERE c."ObjectLevel" = 5 AND c."EquipmentInstanceId" = t."Id"
                  AND c."TargetObjectCodeSnapshot" = '';

                UPDATE "IndividualCards"
                SET "TargetObjectCodeSnapshot" = '[исторический объект]',
                    "TargetObjectNameSnapshot" = 'Данные целевого объекта недоступны'
                WHERE "TargetObjectCodeSnapshot" = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetContextSnapshot",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "TargetObjectCodeSnapshot",
                table: "IndividualCards");

            migrationBuilder.DropColumn(
                name: "TargetObjectNameSnapshot",
                table: "IndividualCards");
        }
    }
}
