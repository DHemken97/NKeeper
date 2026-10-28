using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TagInheritance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Inheritable",
                table: "HostTags",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // A hypervisor's operating system isn't its VMs': scans record it in the "os" tag.
            migrationBuilder.Sql("""UPDATE "HostTags" SET "Inheritable" = false WHERE lower("Name") = 'os';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Inheritable",
                table: "HostTags");
        }
    }
}
