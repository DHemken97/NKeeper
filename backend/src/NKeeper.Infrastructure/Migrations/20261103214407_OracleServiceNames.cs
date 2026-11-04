using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OracleServiceNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Instance",
                table: "HostServices",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only Oracle service names can be longer than 16 (SQL Server instance names can't): versions before
            // this one have no Oracle services, so those names are cleared rather than cut short.
            migrationBuilder.Sql("UPDATE \"HostServices\" SET \"Instance\" = NULL WHERE length(\"Instance\") > 16;");

            migrationBuilder.AlterColumn<string>(
                name: "Instance",
                table: "HostServices",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);
        }
    }
}
