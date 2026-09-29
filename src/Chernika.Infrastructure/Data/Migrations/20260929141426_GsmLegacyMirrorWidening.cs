using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Расширение переходных legacy-колонок <c>GsmMaterials.Type</c> и
    /// <c>Gost</c> с varchar(128) до varchar(256).
    /// <para>
    /// Причина: эти колонки ведутся зеркалами новой модели (<c>Type</c> = подгруппа,
    /// <c>Gost</c> = <c>Nd</c>), а подгруппа бывает до 200 символов, и НД — текстом
    /// нескольких нормативных документов. При 128 допустимый ввод падал бы на
    /// уровне БД.
    /// </para>
    /// <para>
    /// Расширение ничего не переписывает. Обратное сужение в <c>Down()</c> может не
    /// пройти, если за время жизни миграции появится значение длиннее 128 символов,
    /// — как и при любом сужении колонки с данными.
    /// </para>
    /// </summary>
    public partial class GsmLegacyMirrorWidening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "GsmMaterials",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "Gost",
                table: "GsmMaterials",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "GsmMaterials",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            migrationBuilder.AlterColumn<string>(
                name: "Gost",
                table: "GsmMaterials",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);
        }
    }
}
