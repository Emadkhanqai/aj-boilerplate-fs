using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AjBoilerplate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionEndAndDisplayName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "AuthenticationEvents",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EndEventId",
                table: "AuthenticationEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EndReason",
                table: "AuthenticationEvents",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EndReasonDetail",
                table: "AuthenticationEvents",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EndedAt",
                table: "AuthenticationEvents",
                type: "datetimeoffset(7)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationEvents_StartedAt",
                table: "AuthenticationEvents",
                column: "StartedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuthenticationEvents_StartedAt",
                table: "AuthenticationEvents");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "AuthenticationEvents");

            migrationBuilder.DropColumn(
                name: "EndEventId",
                table: "AuthenticationEvents");

            migrationBuilder.DropColumn(
                name: "EndReason",
                table: "AuthenticationEvents");

            migrationBuilder.DropColumn(
                name: "EndReasonDetail",
                table: "AuthenticationEvents");

            migrationBuilder.DropColumn(
                name: "EndedAt",
                table: "AuthenticationEvents");
        }
    }
}
