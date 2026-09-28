using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Защита от расхождения Gost/Description и их переходных копий Nd/IntendedUse
    /// в переходный период PR-2…PR-5.
    /// </summary>
    public partial class GsmLegacyFieldSyncGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Данные этой миграцией НЕ переписываются: прежние миграции уже применены,
            // а молчаливая перезапись новых полей скрыла бы историю расхождений.
            // Актуальное состояние показывает GsmMaterialService.GetTransitionDivergencesAsync,
            // приведение выполняет ReconcileTransitionFieldsAsync.
            //
            // Триггер закрывает саму возможность расхождения: пока новые поля не
            // редактируются штатно, они следуют за прежними значениями. Явная запись
            // Nd/IntendedUse не перехватывается (условие по неизменности OLD).
            // После переключения источника истины на Nd в PR-5 триггер удаляется
            // миграцией удаления переходных полей.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION ""fn_gsm_legacy_field_sync""() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        -- Явное значение нового поля важнее выведенного из прежнего.
        IF NEW.""Nd"" IS NULL THEN
            NEW.""Nd"" := NULLIF(btrim(NEW.""Gost""), '');
        END IF;
        IF NEW.""IntendedUse"" IS NULL THEN
            NEW.""IntendedUse"" := NULLIF(btrim(NEW.""Description""), '');
        END IF;
        RETURN NEW;
    END IF;

    -- UPDATE: синхронизируем только если новое поле не меняли явно, прежнее — меняли.
    IF NEW.""Nd"" IS NOT DISTINCT FROM OLD.""Nd"" AND NEW.""Gost"" IS DISTINCT FROM OLD.""Gost"" THEN
        NEW.""Nd"" := NULLIF(btrim(NEW.""Gost""), '');
    END IF;

    IF NEW.""IntendedUse"" IS NOT DISTINCT FROM OLD.""IntendedUse""
       AND NEW.""Description"" IS DISTINCT FROM OLD.""Description"" THEN
        NEW.""IntendedUse"" := NULLIF(btrim(NEW.""Description""), '');
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER ""TRG_GsmMaterials_LegacyFieldSync""
BEFORE INSERT OR UPDATE ON ""GsmMaterials""
FOR EACH ROW EXECUTE FUNCTION ""fn_gsm_legacy_field_sync""();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS ""TRG_GsmMaterials_LegacyFieldSync"" ON ""GsmMaterials"";
DROP FUNCTION IF EXISTS ""fn_gsm_legacy_field_sync""();
");
        }
    }
}
