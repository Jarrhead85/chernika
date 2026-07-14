using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    public partial class AddHKCardRowVersionAndActiveUniqueIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
DECLARE
    cnt INTEGER;
BEGIN
    SELECT COUNT(*) INTO cnt FROM (
        SELECT ""NodeId"" FROM ""HKCards""
        WHERE ""Status"" IN ('Draft', 'OnReview', 'RevisionRequired')
        GROUP BY ""NodeId"" HAVING COUNT(*) > 1
    ) d;
    IF cnt > 0 THEN
        RAISE EXCEPTION 'Cannot create UX_HKCards_OneActivePerNode: % nodes have more than one active HK card. Resolve duplicates first.', cnt;
    END IF;
END;
$$;");

            migrationBuilder.DropIndex(
                name: "IX_HKCards_NodeId",
                table: "HKCards");

            migrationBuilder.CreateIndex(
                name: "UX_HKCards_OneActivePerNode",
                table: "HKCards",
                columns: new[] { "NodeId", "Status" },
                unique: true,
                filter: "\"Status\" IN ('Draft', 'OnReview', 'RevisionRequired')");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_HKCards_OneActivePerNode",
                table: "HKCards");

            migrationBuilder.CreateIndex(
                name: "IX_HKCards_NodeId",
                table: "HKCards",
                column: "NodeId");
        }
    }
}
