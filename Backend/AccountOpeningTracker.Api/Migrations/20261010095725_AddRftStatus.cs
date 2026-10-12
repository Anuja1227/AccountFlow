using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccountOpeningTracker.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRftStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RftStatus",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RftStatus",
                table: "Accounts");
        }
    }
}
