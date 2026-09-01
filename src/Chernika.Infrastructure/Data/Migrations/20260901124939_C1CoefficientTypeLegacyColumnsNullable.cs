using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class C1CoefficientTypeLegacyColumnsNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Legacy Group/Description columns may or may not exist depending on previous migration history.
            // If they exist, make them nullable so new records can be inserted without specifying legacy values.
            // If they were already dropped, these statements are no-ops.
            // Physical cleanup requires separate approved data migration after production review.
            migrationBuilder.Sql(
                @"DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'CoefficientTypes' AND column_name = 'Group') THEN
                        ALTER TABLE ""CoefficientTypes"" ALTER COLUMN ""Group"" DROP NOT NULL;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'CoefficientTypes' AND column_name = 'Description') THEN
                        ALTER TABLE ""CoefficientTypes"" ALTER COLUMN ""Description"" DROP NOT NULL;
                    END IF;
                END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally left empty: legacy columns remain nullable if they exist.
        }
    }
}
