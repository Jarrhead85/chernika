using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chernika.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkTaskAssigneeFK : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "AssigneeId",
                table: "WorkTasks",
                type: "text",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_AssigneeId",
                table: "WorkTasks",
                column: "AssigneeId");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkTasks_Users_AssigneeId",
                table: "WorkTasks",
                column: "AssigneeId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkTasks_Users_AssigneeId",
                table: "WorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_WorkTasks_AssigneeId",
                table: "WorkTasks");

            migrationBuilder.AlterColumn<Guid>(
                name: "AssigneeId",
                table: "WorkTasks",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
