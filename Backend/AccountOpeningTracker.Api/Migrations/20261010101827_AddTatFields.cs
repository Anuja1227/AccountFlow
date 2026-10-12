using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccountOpeningTracker.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTatFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CurrentAgingDays",
                table: "Accounts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GrossTatDays",
                table: "Accounts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsSlaBreached",
                table: "Accounts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "NetTatDays",
                table: "Accounts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "OriginalInwardDate",
                table: "Accounts",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentAgingDays",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "GrossTatDays",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "IsSlaBreached",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "NetTatDays",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "OriginalInwardDate",
                table: "Accounts");
        }
    }
}
