using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutonomousTree : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HKCardItems_HKCardId",
                table: "HKCardItems");

            migrationBuilder.AddColumn<bool>(
                name: "IsAutonomous",
                table: "HKCards",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Автономная карта не требует привязки к справочному объекту, поэтому
            // инвариант «ровно один объект» для неё не действует. Старые карты
            // (IsAutonomous = false) защищаются прежним условием без изменений.
            migrationBuilder.Sql(@"
ALTER TABLE ""HKCards"" DROP CONSTRAINT IF EXISTS ""CK_HKCards_ExactlyOneObject"";
ALTER TABLE ""HKCards"" ADD CONSTRAINT ""CK_HKCards_ExactlyOneObject""
CHECK (
    ""IsAutonomous""
    OR (CASE WHEN ""ComplexId"" IS NOT NULL THEN 1 ELSE 0 END
      + CASE WHEN ""EquipmentModelId"" IS NOT NULL THEN 1 ELSE 0 END
      + CASE WHEN ""AggregateId"" IS NOT NULL THEN 1 ELSE 0 END
      + CASE WHEN ""NodeId"" IS NOT NULL THEN 1 ELSE 0 END) = 1
)");

            migrationBuilder.AddColumn<Guid>(
                name: "HKCardNodeId",
                table: "HKCardItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HKCardNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HKCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HKCardNodes", x => x.Id);
                    table.UniqueConstraint("AK_HKCardNodes_HKCardId_Id", x => new { x.HKCardId, x.Id });
                    table.ForeignKey(
                        name: "FK_HKCardNodes_HKCardNodes_HKCardId_ParentId",
                        columns: x => new { x.HKCardId, x.ParentId },
                        principalTable: "HKCardNodes",
                        principalColumns: new[] { "HKCardId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HKCardNodes_HKCards_HKCardId",
                        column: x => x.HKCardId,
                        principalTable: "HKCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Целостность позиции обеспечивает сам PostgreSQL. CHECK видит обе
            // колонки одной строки, поэтому самоссылка запрещается на уровне БД.
            // Многошаговые циклы CHECK не ловит — их проверяет сервис
            // (SaveTreeAsync, ValidateStructureAsync), и это указано в контракте.
            migrationBuilder.Sql(@"
ALTER TABLE ""HKCardNodes"" ADD CONSTRAINT ""CK_HKCardNodes_NoSelfReference""
CHECK (""ParentId"" IS NULL OR ""ParentId"" <> ""Id"");

ALTER TABLE ""HKCardNodes"" ADD CONSTRAINT ""CK_HKCardNodes_QuantityPositive""
CHECK (""Quantity"" > 0);

ALTER TABLE ""HKCardNodes"" ADD CONSTRAINT ""CK_HKCardNodes_SortOrderNonNegative""
CHECK (""SortOrder"" >= 0);

ALTER TABLE ""HKCardNodes"" ADD CONSTRAINT ""CK_HKCardNodes_NameNotBlank""
CHECK (length(btrim(""Name"")) > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardItems_HKCardId_HKCardNodeId",
                table: "HKCardItems",
                columns: new[] { "HKCardId", "HKCardNodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_HKCardNodes_HKCardId_ParentId",
                table: "HKCardNodes",
                columns: new[] { "HKCardId", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_HKCardNodes_HKCardId_SortOrder",
                table: "HKCardNodes",
                columns: new[] { "HKCardId", "SortOrder" });

            migrationBuilder.AddForeignKey(
                name: "FK_HKCardItems_HKCardNodes_HKCardId_HKCardNodeId",
                table: "HKCardItems",
                columns: new[] { "HKCardId", "HKCardNodeId" },
                principalTable: "HKCardNodes",
                principalColumns: new[] { "HKCardId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HKCardItems_HKCardNodes_HKCardId_HKCardNodeId",
                table: "HKCardItems");

            migrationBuilder.DropTable(
                name: "HKCardNodes");

            // Возврат прежнего инварианта «ровно один справочный объект».
            migrationBuilder.Sql(@"
ALTER TABLE ""HKCards"" DROP CONSTRAINT IF EXISTS ""CK_HKCards_ExactlyOneObject"";
ALTER TABLE ""HKCards"" ADD CONSTRAINT ""CK_HKCards_ExactlyOneObject""
CHECK (
    (CASE WHEN ""ComplexId"" IS NOT NULL THEN 1 ELSE 0 END
   + CASE WHEN ""EquipmentModelId"" IS NOT NULL THEN 1 ELSE 0 END
   + CASE WHEN ""AggregateId"" IS NOT NULL THEN 1 ELSE 0 END
   + CASE WHEN ""NodeId"" IS NOT NULL THEN 1 ELSE 0 END) = 1
)");

            migrationBuilder.DropIndex(
                name: "IX_HKCardItems_HKCardId_HKCardNodeId",
                table: "HKCardItems");

            migrationBuilder.DropColumn(
                name: "IsAutonomous",
                table: "HKCards");

            migrationBuilder.DropColumn(
                name: "HKCardNodeId",
                table: "HKCardItems");

            migrationBuilder.CreateIndex(
                name: "IX_HKCardItems_HKCardId",
                table: "HKCardItems",
                column: "HKCardId");
        }
    }
}
