using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InventoryLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryLimits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    MaxServicesPerHost = table.Column<int>(type: "integer", nullable: false),
                    MaxCredentialsPerHost = table.Column<int>(type: "integer", nullable: false),
                    MaxTagsPerHost = table.Column<int>(type: "integer", nullable: false),
                    ScanUsersPreselectedUpTo = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryLimits", x => x.Id);
                    table.CheckConstraint("CK_InventoryLimits_Singleton", "\"Id\" = 1");
                    table.ForeignKey(
                        name: "FK_InventoryLimits_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLimits_UpdatedByUserId",
                table: "InventoryLimits",
                column: "UpdatedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryLimits");
        }
    }
}
