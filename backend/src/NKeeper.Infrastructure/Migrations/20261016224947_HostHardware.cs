using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HostHardware : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CpuCores",
                table: "Hosts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MemoryMiB",
                table: "Hosts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SerialNumber",
                table: "Hosts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CpuCores",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "MemoryMiB",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "SerialNumber",
                table: "Hosts");
        }
    }
}
