using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VirtualMachineHypervisors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HypervisorId",
                table: "Hosts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HypervisorSeenAtUtc",
                table: "Hosts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsVirtualMachine",
                table: "Hosts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "VirtualMachineId",
                table: "Hosts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Hosts_HypervisorId",
                table: "Hosts",
                column: "HypervisorId");

            migrationBuilder.CreateIndex(
                name: "IX_Hosts_VirtualMachineId",
                table: "Hosts",
                column: "VirtualMachineId");

            migrationBuilder.AddForeignKey(
                name: "FK_Hosts_Hosts_HypervisorId",
                table: "Hosts",
                column: "HypervisorId",
                principalTable: "Hosts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Hosts_Hosts_HypervisorId",
                table: "Hosts");

            migrationBuilder.DropIndex(
                name: "IX_Hosts_HypervisorId",
                table: "Hosts");

            migrationBuilder.DropIndex(
                name: "IX_Hosts_VirtualMachineId",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "HypervisorId",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "HypervisorSeenAtUtc",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "IsVirtualMachine",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "VirtualMachineId",
                table: "Hosts");
        }
    }
}
