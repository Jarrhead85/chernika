using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class GsmInventoryAndRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HKCardItemMaterials_GsmMaterials_GsmMaterialId",
                table: "HKCardItemMaterials");

            migrationBuilder.AddColumn<bool>(
                name: "InGostNomenclature",
                table: "GsmMaterials",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "IntendedUse",
                table: "GsmMaterials",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NatoIndex",
                table: "GsmMaterials",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Nd",
                table: "GsmMaterials",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "GsmMaterials",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SuitabilityAir",
                table: "GsmMaterials",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SuitabilityGround",
                table: "GsmMaterials",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SuitabilitySea",
                table: "GsmMaterials",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "GsmMaterialClassifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GsmMaterialId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SubgroupName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GsmMaterialClassifications", x => x.Id);
                    table.CheckConstraint("CK_GsmMaterialClassifications_GroupName", "btrim(\"GroupName\") <> ''");
                    table.CheckConstraint("CK_GsmMaterialClassifications_SubgroupName", "btrim(\"SubgroupName\") <> ''");
                    table.ForeignKey(
                        name: "FK_GsmMaterialClassifications_GsmMaterials_GsmMaterialId",
                        column: x => x.GsmMaterialId,
                        principalTable: "GsmMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GsmMaterialRelations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrimaryGsmMaterialId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelatedGsmMaterialId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GsmMaterialRelations", x => x.Id);
                    table.CheckConstraint("CK_GsmMaterialRelations_NoSelfReference", "\"PrimaryGsmMaterialId\" <> \"RelatedGsmMaterialId\"");
                    table.CheckConstraint("CK_GsmMaterialRelations_RelationType", "\"RelationType\" IN ('Duplicate', 'Reserve', 'DuplicateAndReserve', 'Foreign')");
                    table.ForeignKey(
                        name: "FK_GsmMaterialRelations_GsmMaterials_PrimaryGsmMaterialId",
                        column: x => x.PrimaryGsmMaterialId,
                        principalTable: "GsmMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GsmMaterialRelations_GsmMaterials_RelatedGsmMaterialId",
                        column: x => x.RelatedGsmMaterialId,
                        principalTable: "GsmMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GsmMaterialClassifications_GsmMaterialId",
                table: "GsmMaterialClassifications",
                column: "GsmMaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_GsmMaterialRelations_PrimaryGsmMaterialId",
                table: "GsmMaterialRelations",
                column: "PrimaryGsmMaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_GsmMaterialRelations_RelatedGsmMaterialId",
                table: "GsmMaterialRelations",
                column: "RelatedGsmMaterialId");

            migrationBuilder.AddForeignKey(
                name: "FK_HKCardItemMaterials_GsmMaterials_GsmMaterialId",
                table: "HKCardItemMaterials",
                column: "GsmMaterialId",
                principalTable: "GsmMaterials",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // ── Backfill (идемпотентный, без перезаписи уже заполненного) ──
            // Переносим только проверяемые значения: прежний Gost → Nd, Description → IntendedUse.
            // Классификация НЕ переносится: старый Type может быть подгруппой, но группа
            // неизвестна. «Не классифицирована» — только UI-состояние, в БД фиктивной
            // группы не создаётся.
            migrationBuilder.Sql(@"
UPDATE ""GsmMaterials""
SET ""Nd"" = NULLIF(btrim(""Gost""), '')
WHERE ""Nd"" IS NULL AND ""Gost"" IS NOT NULL;

UPDATE ""GsmMaterials""
SET ""IntendedUse"" = ""Description""
WHERE ""IntendedUse"" IS NULL AND ""Description"" IS NOT NULL;
");

            // ── Инвариант A: уникальность нормализованной тройки (Id, группа, подгруппа) ──
            // Выражение lower(btrim(...)) fluent API не выражает — создаём напрямую.
            migrationBuilder.Sql(@"
CREATE UNIQUE INDEX ""UX_GsmMaterialClassifications_GroupSubgroup""
ON ""GsmMaterialClassifications"" (""GsmMaterialId"", lower(btrim(""GroupName"")), lower(btrim(""SubgroupName"")));
");

            // ── Уникальность активной направленной пары связей ──
            // Частичный индекс: после мягкого удаления связь можно создать заново.
            // Восстановление старой связи при конфликте с новой активной парой — PR-4.
            migrationBuilder.Sql(@"
CREATE UNIQUE INDEX ""UX_GsmMaterialRelations_ActivePair""
ON ""GsmMaterialRelations"" (""PrimaryGsmMaterialId"", ""RelatedGsmMaterialId"")
WHERE ""IsDeleted"" = false;
");

            // ── Инвариант B: у марки только ОДНА нормализованная группа ──
            // Межстрочное правило, которое нельзя закрыть одиночным unique-индексом
            // (он запретил бы вторую подгруппу той же группы). Триггер сериализует
            // конкурентные записи по родительской марке и проверяет существующие строки.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION ""fn_gsm_classification_single_group""() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_other_group text;
BEGIN
    -- Сериализация по родительской марке: конкурентные вставки выстраиваются в очередь.
    PERFORM 1 FROM ""GsmMaterials"" WHERE ""Id"" = NEW.""GsmMaterialId"" FOR UPDATE;

    SELECT lower(btrim(""GroupName"")) INTO v_other_group
    FROM ""GsmMaterialClassifications""
    WHERE ""GsmMaterialId"" = NEW.""GsmMaterialId""
      AND ""Id"" <> NEW.""Id""
      AND lower(btrim(""GroupName"")) <> lower(btrim(NEW.""GroupName""))
    LIMIT 1;

    IF v_other_group IS NOT NULL THEN
        RAISE EXCEPTION 'У марки ГСМ уже задана другая группа «%»: допустима только одна группа на марку.', v_other_group
            USING ERRCODE = 'check_violation';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER ""TRG_GsmMaterialClassifications_SingleGroup""
BEFORE INSERT OR UPDATE ON ""GsmMaterialClassifications""
FOR EACH ROW EXECUTE FUNCTION ""fn_gsm_classification_single_group""();
");

            // ── Правило Foreign: связанная марка не включена в номенклатуру по ГОСТ ──
            // Сторона 1: запись/смена типа активной связи. Блокировка строки марки
            // исключает гонку с параллельным переводом флага (сторона 2).
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION ""fn_gsm_relation_foreign_check""() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_in_nomenclature boolean;
BEGIN
    IF NEW.""RelationType"" = 'Foreign' AND NOT NEW.""IsDeleted"" THEN
        PERFORM 1 FROM ""GsmMaterials"" WHERE ""Id"" = NEW.""RelatedGsmMaterialId"" FOR UPDATE;
        SELECT ""InGostNomenclature"" INTO v_in_nomenclature
        FROM ""GsmMaterials"" WHERE ""Id"" = NEW.""RelatedGsmMaterialId"";

        IF v_in_nomenclature THEN
            RAISE EXCEPTION 'Связь Foreign требует, чтобы связанная марка не была включена в номенклатуру по ГОСТ.'
                USING ERRCODE = 'check_violation';
        END IF;
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER ""TRG_GsmMaterialRelations_ForeignCheck""
BEFORE INSERT OR UPDATE ON ""GsmMaterialRelations""
FOR EACH ROW EXECUTE FUNCTION ""fn_gsm_relation_foreign_check""();
");

            // Сторона 2: попытка включить марку в номенклатуру по ГОСТ при активной
            // Foreign-связи. Удалённые (IsDeleted = true) связи не блокируют изменение.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION ""fn_gsm_material_foreign_flag_guard""() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_active int;
BEGIN
    IF NEW.""InGostNomenclature"" = true AND OLD.""InGostNomenclature"" IS DISTINCT FROM true THEN
        SELECT count(*) INTO v_active
        FROM ""GsmMaterialRelations""
        WHERE ""RelatedGsmMaterialId"" = NEW.""Id""
          AND ""RelationType"" = 'Foreign'
          AND ""IsDeleted"" = false;

        IF v_active > 0 THEN
            RAISE EXCEPTION 'Нельзя включить марку в номенклатуру по ГОСТ: существует активная связь Foreign.'
                USING ERRCODE = 'check_violation';
        END IF;
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER ""TRG_GsmMaterials_ForeignFlagGuard""
BEFORE UPDATE OF ""InGostNomenclature"" ON ""GsmMaterials""
FOR EACH ROW EXECUTE FUNCTION ""fn_gsm_material_foreign_flag_guard""();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Объекты, созданные вручную (выражения lower/btrim не выражаются fluent API).
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS ""TRG_GsmMaterials_ForeignFlagGuard"" ON ""GsmMaterials"";
DROP FUNCTION IF EXISTS ""fn_gsm_material_foreign_flag_guard""();

DROP TRIGGER IF EXISTS ""TRG_GsmMaterialRelations_ForeignCheck"" ON ""GsmMaterialRelations"";
DROP FUNCTION IF EXISTS ""fn_gsm_relation_foreign_check""();

DROP TRIGGER IF EXISTS ""TRG_GsmMaterialClassifications_SingleGroup"" ON ""GsmMaterialClassifications"";
DROP FUNCTION IF EXISTS ""fn_gsm_classification_single_group""();

DROP INDEX IF EXISTS ""UX_GsmMaterialRelations_ActivePair"";
DROP INDEX IF EXISTS ""UX_GsmMaterialClassifications_GroupSubgroup"";
");

            // ВНИМАНИЕ: Down() необратимо теряет значения, записанные приложением в новые
            // поля (Nd, IntendedUse, Note, NatoIndex, Suitability*, InGostNomenclature)
            // и в новые таблицы. Откат миграции допустим только до начала такой записи.
            migrationBuilder.DropForeignKey(
                name: "FK_HKCardItemMaterials_GsmMaterials_GsmMaterialId",
                table: "HKCardItemMaterials");

            migrationBuilder.DropTable(
                name: "GsmMaterialClassifications");

            migrationBuilder.DropTable(
                name: "GsmMaterialRelations");

            migrationBuilder.DropColumn(
                name: "InGostNomenclature",
                table: "GsmMaterials");

            migrationBuilder.DropColumn(
                name: "IntendedUse",
                table: "GsmMaterials");

            migrationBuilder.DropColumn(
                name: "NatoIndex",
                table: "GsmMaterials");

            migrationBuilder.DropColumn(
                name: "Nd",
                table: "GsmMaterials");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "GsmMaterials");

            migrationBuilder.DropColumn(
                name: "SuitabilityAir",
                table: "GsmMaterials");

            migrationBuilder.DropColumn(
                name: "SuitabilityGround",
                table: "GsmMaterials");

            migrationBuilder.DropColumn(
                name: "SuitabilitySea",
                table: "GsmMaterials");

            migrationBuilder.AddForeignKey(
                name: "FK_HKCardItemMaterials_GsmMaterials_GsmMaterialId",
                table: "HKCardItemMaterials",
                column: "GsmMaterialId",
                principalTable: "GsmMaterials",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
