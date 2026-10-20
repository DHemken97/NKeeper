using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class JumpHosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "JumpCredentialId",
                table: "Hosts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "JumpHostId",
                table: "Hosts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Hosts_JumpCredentialId",
                table: "Hosts",
                column: "JumpCredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_Hosts_JumpHostId",
                table: "Hosts",
                column: "JumpHostId");

            migrationBuilder.AddForeignKey(
                name: "FK_Hosts_HostCredentials_JumpCredentialId",
                table: "Hosts",
                column: "JumpCredentialId",
                principalTable: "HostCredentials",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Hosts_Hosts_JumpHostId",
                table: "Hosts",
                column: "JumpHostId",
                principalTable: "Hosts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Hosts_HostCredentials_JumpCredentialId",
                table: "Hosts");

            migrationBuilder.DropForeignKey(
                name: "FK_Hosts_Hosts_JumpHostId",
                table: "Hosts");

            migrationBuilder.DropIndex(
                name: "IX_Hosts_JumpCredentialId",
                table: "Hosts");

            migrationBuilder.DropIndex(
                name: "IX_Hosts_JumpHostId",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "JumpCredentialId",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "JumpHostId",
                table: "Hosts");
        }
    }
}
