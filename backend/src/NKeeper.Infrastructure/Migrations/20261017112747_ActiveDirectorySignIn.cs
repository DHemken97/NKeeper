using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ActiveDirectorySignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "ProtectedSignInPassword",
                table: "AuthSessions",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDirectoryAccount",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProtectedSignInPassword",
                table: "AuthSessions");

            migrationBuilder.DropColumn(
                name: "IsDirectoryAccount",
                table: "AspNetUsers");
        }
    }
}
