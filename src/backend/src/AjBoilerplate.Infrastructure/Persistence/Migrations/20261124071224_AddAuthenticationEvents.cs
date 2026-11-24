using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AjBoilerplate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthenticationEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuthenticationEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SessionId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthenticationEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationEvents_SessionId",
                table: "AuthenticationEvents",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationEvents_Subject",
                table: "AuthenticationEvents",
                column: "Subject");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthenticationEvents");
        }
    }
}
