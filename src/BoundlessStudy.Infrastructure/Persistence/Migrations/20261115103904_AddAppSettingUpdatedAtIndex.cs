using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoundlessStudy.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAppSettingUpdatedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AppSetting_UpdatedAtUtc",
                table: "AppSetting",
                column: "UpdatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AppSetting_UpdatedAtUtc",
                table: "AppSetting");
        }
    }
}
