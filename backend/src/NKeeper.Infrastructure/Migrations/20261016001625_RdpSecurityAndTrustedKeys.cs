using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Infrastructure.Migrations
{
    /// <summary>
    /// Generalizes trusted SSH host keys into trusted keys for any protocol (SSH host keys and
    /// RDP certificates), keeping every key already trusted, and adds the RDP security setting.
    /// Written by hand: the scaffolded version dropped and recreated the table, losing the keys.
    /// </summary>
    public partial class RdpSecurityAndTrustedKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RdpSecurity",
                table: "HostServices",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.RenameTable(name: "HostSshKeys", newName: "HostTrustedKeys");
            migrationBuilder.Sql("""ALTER TABLE "HostTrustedKeys" RENAME CONSTRAINT "PK_HostSshKeys" TO "PK_HostTrustedKeys";""");
            migrationBuilder.Sql("""ALTER TABLE "HostTrustedKeys" RENAME CONSTRAINT "FK_HostSshKeys_Hosts_HostId" TO "FK_HostTrustedKeys_Hosts_HostId";""");
            migrationBuilder.Sql("""ALTER TABLE "HostTrustedKeys" RENAME CONSTRAINT "FK_HostSshKeys_AspNetUsers_TrustedByUserId" TO "FK_HostTrustedKeys_AspNetUsers_TrustedByUserId";""");
            migrationBuilder.RenameIndex(name: "IX_HostSshKeys_TrustedByUserId", table: "HostTrustedKeys", newName: "IX_HostTrustedKeys_TrustedByUserId");

            // Every key trusted so far is an SSH host key.
            migrationBuilder.AddColumn<string>(
                name: "Protocol",
                table: "HostTrustedKeys",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Ssh");

            migrationBuilder.AlterColumn<string>(
                name: "Fingerprint",
                table: "HostTrustedKeys",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.DropIndex(name: "IX_HostSshKeys_HostId_Port_Algorithm", table: "HostTrustedKeys");
            migrationBuilder.CreateIndex(
                name: "IX_HostTrustedKeys_HostId_Protocol_Port_Algorithm",
                table: "HostTrustedKeys",
                columns: new[] { "HostId", "Protocol", "Port", "Algorithm" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only SSH keys existed before; RDP certificates have nowhere to go.
            migrationBuilder.Sql("""DELETE FROM "HostTrustedKeys" WHERE "Protocol" <> 'Ssh';""");

            migrationBuilder.DropIndex(name: "IX_HostTrustedKeys_HostId_Protocol_Port_Algorithm", table: "HostTrustedKeys");
            migrationBuilder.DropColumn(name: "Protocol", table: "HostTrustedKeys");
            migrationBuilder.AlterColumn<string>(
                name: "Fingerprint",
                table: "HostTrustedKeys",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.RenameIndex(name: "IX_HostTrustedKeys_TrustedByUserId", table: "HostTrustedKeys", newName: "IX_HostSshKeys_TrustedByUserId");
            migrationBuilder.Sql("""ALTER TABLE "HostTrustedKeys" RENAME CONSTRAINT "FK_HostTrustedKeys_AspNetUsers_TrustedByUserId" TO "FK_HostSshKeys_AspNetUsers_TrustedByUserId";""");
            migrationBuilder.Sql("""ALTER TABLE "HostTrustedKeys" RENAME CONSTRAINT "FK_HostTrustedKeys_Hosts_HostId" TO "FK_HostSshKeys_Hosts_HostId";""");
            migrationBuilder.Sql("""ALTER TABLE "HostTrustedKeys" RENAME CONSTRAINT "PK_HostTrustedKeys" TO "PK_HostSshKeys";""");
            migrationBuilder.RenameTable(name: "HostTrustedKeys", newName: "HostSshKeys");
            migrationBuilder.CreateIndex(
                name: "IX_HostSshKeys_HostId_Port_Algorithm",
                table: "HostSshKeys",
                columns: new[] { "HostId", "Port", "Algorithm" },
                unique: true);

            migrationBuilder.DropColumn(name: "RdpSecurity", table: "HostServices");
        }
    }
}
