using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoundlessStudy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFocusSessionInterruptedAtUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "InterruptedAtUtc",
                table: "FocusSession",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InterruptedAtUtc",
                table: "FocusSession");
        }
    }
}
