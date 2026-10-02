using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Завершение перехода ГСМ: удаление переходных колонок
    /// <c>GsmMaterials.Type / Gost / Description</c> и снятие переходного триггера.
    /// <para>
    /// Фаза B PR-6. Требует проверенного бэкапа с выполненным пробным
    /// восстановлением: <see cref="Down"/> возвращает только схему и НЕ является
    /// откатом данных.
    /// </para>
    /// </summary>
    public partial class DropGsmLegacyColumns : Migration
    {
        /// <summary>
        /// Порядок обязателен и функционален, а не стилистичен.
        /// <para>
        /// Тело <c>fn_gsm_legacy_field_sync</c> обращается к <c>NEW."Gost"</c> и
        /// <c>NEW."Description"</c>. plpgsql разрешает имена колонок в момент
        /// выполнения, поэтому оставить триггер при удалённых колонках значит
        /// уронить ЛЮБУЮ вставку и правку марки. Поэтому сначала снимаются
        /// триггер и функция, и лишь затем удаляются колонки.
        /// </para>
        /// <para>
        /// Снятие выполняется идемпотентно (IF EXISTS): миграция обязана
        /// применяться к БД, где триггер уже снят вручную, без падения.
        /// </para>
        /// <para>
        /// НЕ снимаются: <c>TRG_GsmMaterials_ForeignFlagGuard</c> (тоже висит на
        /// <c>GsmMaterials</c>, но проверяет флаг номенклатуры, а не legacy-поля),
        /// <c>TRG_GsmMaterialClassifications_SingleGroup</c>,
        /// <c>TRG_GsmMaterialRelations_ForeignCheck</c>, а также индексы и
        /// ограничения справочных таблиц.
        /// </para>
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Шаг 1 — переходный триггер и его функция.
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS ""TRG_GsmMaterials_LegacyFieldSync"" ON ""GsmMaterials"";
DROP FUNCTION IF EXISTS ""fn_gsm_legacy_field_sync""();
");

            // Шаг 2 — только три переходные колонки. Таблица GsmMaterials,
            // её строки, Guid и FK остаются: удаляются поля, а не данные.
            //
            // Порядок не имеет отдельного значения (триггер уже снят), но Type
            // идёт последним: он был единственным NOT NULL из трёх.
            migrationBuilder.DropColumn(
                name: "Description",
                table: "GsmMaterials");

            migrationBuilder.DropColumn(
                name: "Gost",
                table: "GsmMaterials");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "GsmMaterials");
        }

        /// <summary>
        /// Возвращает ТОЛЬКО СХЕМУ. Значения удалённых колонок из неё
        /// не восстановить.
        /// <para>
        /// <b>Данные не восстанавливаются и не выдумываются.</b> Прежние значения
        /// <c>Gost</c>/<c>Description</c> физически удалены вместе с колонками;
        /// восстановить их отсюда нечем. Колонки возвращаются NULLABLE и без
        /// значения по умолчанию, поэтому существующие строки получают NULL, а не
        /// выдуманную строку.
        /// </para>
        /// <para>
        /// Nullable, а не NOT NULL с defaultValue — сознательное отличие от
        /// прежней схемы: defaultValue = "" заполнил бы все марки выдуманным
        /// значением, что неотличимо от настоящих данных и опаснее их отсутствия.
        /// </para>
        /// <para>
        /// Переходный триггер НЕ восстанавливается. Триггер над восстановленными
        /// NULL-колонками переписал бы <c>Nd</c> из NULL при следующей же правке
        /// марки — это вторая потеря данных внутри «отката». Поддерживаемый откат:
        /// восстановление из проверенного бэкапа вместе с совместимой версией кода,
        /// а не вызов <c>Down()</c>.
        /// </para>
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "GsmMaterials",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Gost",
                table: "GsmMaterials",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "GsmMaterials",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }
    }
}
