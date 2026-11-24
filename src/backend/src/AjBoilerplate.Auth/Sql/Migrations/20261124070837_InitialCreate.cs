using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AjBoilerplate.Auth.Sql.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "auth");

            migrationBuilder.CreateTable(
                name: "Sessions",
                schema: "auth",
                columns: table => new
                {
                    SessionKey = table.Column<string>(type: "char(43)", nullable: false, collation: "Latin1_General_BIN2"),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    KeycloakJwt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    JwtExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EntraRefreshToken = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ForceRefresh = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.SessionKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ExpiresAt",
                schema: "auth",
                table: "Sessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_Subject",
                schema: "auth",
                table: "Sessions",
                column: "Subject");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Sessions",
                schema: "auth");
        }
    }
}
