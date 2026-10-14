using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccountOpeningTracker.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMofslAccountNumberDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MofslAccountNumber",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MofslAccountOpeningDate",
                table: "Accounts",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MofslAccountNumber",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MofslAccountOpeningDate",
                table: "Accounts");
        }
    }
}
