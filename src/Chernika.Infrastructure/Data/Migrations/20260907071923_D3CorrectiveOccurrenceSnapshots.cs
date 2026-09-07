using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class D3CorrectiveOccurrenceSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsComplete",
                table: "IndividualCardHKSourceSnapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "PreflightOccurrenceId",
                table: "IndividualCardHKSourceSnapshots",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Safe backfill of pre-corrective snapshot rows: occurrence identity
            // was not persisted before corrective D3, so legacy rows get a fresh
            // unique occurrence id (never restored from history).
            migrationBuilder.Sql("""
                CREATE EXTENSION IF NOT EXISTS pgcrypto;

                UPDATE "IndividualCardHKSourceSnapshots"
                SET "PreflightOccurrenceId" = gen_random_uuid()
                WHERE "PreflightOccurrenceId" = '00000000-0000-0000-0000-000000000000';
                """);

            // Conservative completeness rule: a Draft without normative gap
            // snapshots is considered historically complete; a Draft WITH gap
            // snapshots keeps IsComplete = false (do not attempt to recover
            // which source position was broken).
            migrationBuilder.Sql("""
                UPDATE "IndividualCardHKSourceSnapshots" s
                SET "IsComplete" = true
                WHERE s."IsComplete" = false
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "IndividualCardNormativeGapSnapshots" g
                      WHERE g."IndividualCardId" = s."IndividualCardId"
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsComplete",
                table: "IndividualCardHKSourceSnapshots");

            migrationBuilder.DropColumn(
                name: "PreflightOccurrenceId",
                table: "IndividualCardHKSourceSnapshots");
        }
    }
}
