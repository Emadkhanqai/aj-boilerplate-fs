using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AjBoilerplate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InboxMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceEventId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    EventVersion = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MessageType = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EventVersion = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_CorrelationId",
                table: "InboxMessages",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_SourceEventId",
                table: "InboxMessages",
                column: "SourceEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_Status_ReceivedAtUtc",
                table: "InboxMessages",
                columns: new[] { "Status", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Items_CreatedAt",
                table: "Items",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Items_Name",
                table: "Items",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Items_Status",
                table: "Items",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_CorrelationId",
                table: "OutboxMessages",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Status_OccurredAtUtc",
                table: "OutboxMessages",
                columns: new[] { "Status", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxMessages");

            migrationBuilder.DropTable(
                name: "Items");

            migrationBuilder.DropTable(
                name: "OutboxMessages");
        }
    }
}
