using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccountOpeningTracker.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountWorkflowFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActionBy",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "DigitalFormSentToClientDate",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DiscrepancyRaisedDate",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DiscrepancyResolutionReceivedDate",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscrepancyType",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntryType",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "FormPreparedDate",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FormReturnedToCseDate",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCurrentStageManuallyOverridden",
                table: "Accounts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "MofslQueryRaisedDate",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MofslQueryResolvedDate",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MofslQueryType",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PhysicalFormSubmittedToCseDate",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResubmissionDate",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResubmittedFormReceivedDate",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignedFormReceivedDate",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Stage1QueryDetails",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "Stage1QueryRaisedDate",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "Stage1ResubmissionDate",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Stage1Status",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Stage2Status",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Stage3Status",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Stage4ReviewOutcome",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Stage4Status",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Stage5Status",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Stage6Status",
                table: "Accounts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "SubmittedToMofslDate",
                table: "Accounts",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActionBy",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "DigitalFormSentToClientDate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "DiscrepancyRaisedDate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "DiscrepancyResolutionReceivedDate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "DiscrepancyType",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "EntryType",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "FormPreparedDate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "FormReturnedToCseDate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "IsCurrentStageManuallyOverridden",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MofslQueryRaisedDate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MofslQueryResolvedDate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MofslQueryType",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "PhysicalFormSubmittedToCseDate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "ResubmissionDate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "ResubmittedFormReceivedDate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "SignedFormReceivedDate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Stage1QueryDetails",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Stage1QueryRaisedDate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Stage1ResubmissionDate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Stage1Status",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Stage2Status",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Stage3Status",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Stage4ReviewOutcome",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Stage4Status",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Stage5Status",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Stage6Status",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "SubmittedToMofslDate",
                table: "Accounts");
        }
    }
}
