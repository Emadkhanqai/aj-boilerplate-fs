using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AjBoilerplate.Auth.Sql.Migrations
{
    /// <summary>
    /// Portal hand-off (a handed-off session names the session whose refresh token it renews from) and the session-ended outbox.
    /// Both are additive: a nullable column and a new table, so the previous revision keeps working against the migrated database;
    /// run it before the new revision rolls out.
    /// </summary>
    public partial class AddHandoffAndSessionEndedOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RefreshVia",
                schema: "auth",
                table: "Sessions",
                type: "char(43)",
                nullable: true,
                collation: "Latin1_General_BIN2");

            migrationBuilder.CreateTable(
                name: "SessionEndedOutbox",
                schema: "auth",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<string>(type: "char(32)", nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "varchar(32)", nullable: false),
                    Detail = table.Column<string>(type: "varchar(64)", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SessionStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(64)", nullable: true),
                    EnqueuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastOutcome = table.Column<string>(type: "varchar(64)", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AbandonedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionEndedOutbox", x => x.EventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SessionEndedOutbox_Pending",
                schema: "auth",
                table: "SessionEndedOutbox",
                column: "NextAttemptAt",
                filter: "[DeliveredAt] IS NULL AND [AbandonedAt] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessionEndedOutbox",
                schema: "auth");

            migrationBuilder.DropColumn(
                name: "RefreshVia",
                schema: "auth",
                table: "Sessions");
        }
    }
}
