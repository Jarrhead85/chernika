using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftDeleteToGsmMaterialAndCoefficient : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "GsmMaterials",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "GsmMaterials",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Coefficients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Coefficients",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "GsmMaterials");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "GsmMaterials");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Coefficients");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Coefficients");
        }
    }
}
