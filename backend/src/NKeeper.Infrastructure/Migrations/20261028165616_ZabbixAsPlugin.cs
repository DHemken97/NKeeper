using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ZabbixAsPlugin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Zabbix became a plugin: its settings and the hosts' links move into the plugin tables.
            // The API token stays encrypted with the same key, as plugin secrets are.
            migrationBuilder.Sql("""
                INSERT INTO "PluginSettings" ("PluginId", "Name", "Value", "ProtectedValue", "UpdatedAtUtc", "UpdatedByUserId")
                SELECT 'zabbix', 'enabled', CASE WHEN "Enabled" THEN 'true' ELSE 'false' END, NULL::bytea, "UpdatedAtUtc", "UpdatedByUserId" FROM "ZabbixSettings"
                UNION ALL
                SELECT 'zabbix', 'url', "Url", NULL::bytea, "UpdatedAtUtc", "UpdatedByUserId" FROM "ZabbixSettings" WHERE "Url" <> ''
                UNION ALL
                SELECT 'zabbix', 'trustedCertificateFingerprint', "TrustedCertificateFingerprint", NULL::bytea, "UpdatedAtUtc", "UpdatedByUserId"
                FROM "ZabbixSettings" WHERE "TrustedCertificateFingerprint" IS NOT NULL
                UNION ALL
                SELECT 'zabbix', 'apiToken', NULL::text, "ProtectedApiToken", "UpdatedAtUtc", "UpdatedByUserId" FROM "ZabbixSettings" WHERE "ProtectedApiToken" IS NOT NULL;

                INSERT INTO "HostPluginValues" ("HostId", "PluginId", "Name", "Value")
                SELECT "Id", 'zabbix', 'hostId', "ZabbixHostId" FROM "Hosts" WHERE "ZabbixHostId" IS NOT NULL;
                """);

            migrationBuilder.DropTable(
                name: "ZabbixSettings");

            migrationBuilder.DropColumn(
                name: "ZabbixHostId",
                table: "Hosts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ZabbixHostId",
                table: "Hosts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ZabbixSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ProtectedApiToken = table.Column<byte[]>(type: "bytea", nullable: true),
                    TrustedCertificateFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZabbixSettings", x => x.Id);
                    table.CheckConstraint("CK_ZabbixSettings_Singleton", "\"Id\" = 1");
                    table.ForeignKey(
                        name: "FK_ZabbixSettings_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ZabbixSettings_UpdatedByUserId",
                table: "ZabbixSettings",
                column: "UpdatedByUserId");

            migrationBuilder.Sql("""
                INSERT INTO "ZabbixSettings" ("Id", "Enabled", "Url", "ProtectedApiToken", "TrustedCertificateFingerprint", "UpdatedAtUtc", "UpdatedByUserId")
                SELECT 1,
                       COALESCE(MAX("Value") FILTER (WHERE "Name" = 'enabled'), 'false') = 'true',
                       COALESCE(MAX("Value") FILTER (WHERE "Name" = 'url'), ''),
                       (SELECT "ProtectedValue" FROM "PluginSettings" WHERE "PluginId" = 'zabbix' AND "Name" = 'apiToken'),
                       MAX("Value") FILTER (WHERE "Name" = 'trustedCertificateFingerprint'),
                       MAX("UpdatedAtUtc"),
                       NULL
                FROM "PluginSettings" WHERE "PluginId" = 'zabbix'
                HAVING COUNT(*) > 0;

                UPDATE "Hosts" h SET "ZabbixHostId" = v."Value"
                FROM "HostPluginValues" v
                WHERE v."HostId" = h."Id" AND v."PluginId" = 'zabbix' AND v."Name" = 'hostId' AND length(v."Value") <= 20;

                DELETE FROM "PluginSettings" WHERE "PluginId" = 'zabbix';
                DELETE FROM "HostPluginValues" WHERE "PluginId" = 'zabbix';
                """);
        }
    }
}
