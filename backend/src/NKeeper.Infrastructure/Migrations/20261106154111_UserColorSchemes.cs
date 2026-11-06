using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UserColorSchemes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ColorScheme",
                table: "AspNetUsers",
                type: "text",
                nullable: false,
                defaultValue: "navy");

            migrationBuilder.AddColumn<string>(
                name: "CustomColorSchemes",
                table: "AspNetUsers",
                type: "text",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ColorScheme",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "CustomColorSchemes",
                table: "AspNetUsers");
        }
    }
}
