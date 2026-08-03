using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AjBoilerplate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFeatureAnnouncements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "feat_Features",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    TitleEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TitleAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BodyEn = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    BodyAr = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PagesJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feat_Features", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "feat_Acknowledgements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FeatureId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feat_Acknowledgements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feat_Acknowledgements_feat_Features_FeatureId",
                        column: x => x.FeatureId,
                        principalTable: "feat_Features",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_feat_Acknowledgements_FeatureId",
                table: "feat_Acknowledgements",
                column: "FeatureId");

            migrationBuilder.CreateIndex(
                name: "IX_feat_Acknowledgements_User_Feature",
                table: "feat_Acknowledgements",
                columns: new[] { "UserId", "FeatureId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_feat_Features_Active_Order",
                table: "feat_Features",
                columns: new[] { "IsActive", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_feat_Features_Key",
                table: "feat_Features",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feat_Acknowledgements");

            migrationBuilder.DropTable(
                name: "feat_Features");
        }
    }
}
