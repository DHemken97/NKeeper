using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CredentialServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<Guid>>(
                name: "ServiceIds",
                table: "HostCredentials",
                type: "uuid[]",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ServiceIds",
                table: "HostCredentials");
        }
    }
}
