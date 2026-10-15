using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NKeeper.Infrastructure.Migrations
{
    /// <summary>
    /// Replaces groups, the shared tag table and the shared credential library with tags and
    /// credentials owned by each host. Written by hand: the scaffolded version reused the old
    /// join tables by renaming columns, which would have mislabeled any existing rows.
    /// The previous model was never released, so no data is carried over.
    /// </summary>
    public partial class HostOwnedTagsAndCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "HostCredentials");
            migrationBuilder.DropTable(name: "HostTags");
            migrationBuilder.DropTable(name: "Credentials");
            migrationBuilder.DropTable(name: "Tags");

            migrationBuilder.DropForeignKey(name: "FK_Hosts_HostGroups_GroupId", table: "Hosts");
            migrationBuilder.DropIndex(name: "IX_Hosts_GroupId", table: "Hosts");
            migrationBuilder.DropColumn(name: "GroupId", table: "Hosts");
            migrationBuilder.DropTable(name: "HostGroups");

            migrationBuilder.CreateTable(
                name: "HostTags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HostTags_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HostCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Domain = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ProtectedPassword = table.Column<byte[]>(type: "bytea", nullable: true),
                    ProtectedPrivateKey = table.Column<byte[]>(type: "bytea", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HostCredentials_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostTags_HostId_Name_Value",
                table: "HostTags",
                columns: new[] { "HostId", "Name", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostTags_Name_Value",
                table: "HostTags",
                columns: new[] { "Name", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_HostCredentials_HostId_Position",
                table: "HostCredentials",
                columns: new[] { "HostId", "Position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "HostCredentials");
            migrationBuilder.DropTable(name: "HostTags");

            migrationBuilder.CreateTable(
                name: "HostGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_HostGroups", x => x.Id));

            migrationBuilder.AddColumn<Guid>(name: "GroupId", table: "Hosts", type: "uuid", nullable: true);
            migrationBuilder.CreateIndex(name: "IX_Hosts_GroupId", table: "Hosts", column: "GroupId");
            migrationBuilder.AddForeignKey(
                name: "FK_Hosts_HostGroups_GroupId",
                table: "Hosts",
                column: "GroupId",
                principalTable: "HostGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_Tags", x => x.Id));

            migrationBuilder.CreateTable(
                name: "Credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Domain = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ProtectedPassword = table.Column<byte[]>(type: "bytea", nullable: true),
                    ProtectedPrivateKey = table.Column<byte[]>(type: "bytea", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Credentials_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Credentials_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "HostCredentials",
                columns: table => new
                {
                    CredentialsId = table.Column<Guid>(type: "uuid", nullable: false),
                    HostsId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostCredentials", x => new { x.CredentialsId, x.HostsId });
                    table.ForeignKey(
                        name: "FK_HostCredentials_Credentials_CredentialsId",
                        column: x => x.CredentialsId,
                        principalTable: "Credentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HostCredentials_Hosts_HostsId",
                        column: x => x.HostsId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HostTags",
                columns: table => new
                {
                    HostsId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagsId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostTags", x => new { x.HostsId, x.TagsId });
                    table.ForeignKey(
                        name: "FK_HostTags_Hosts_HostsId",
                        column: x => x.HostsId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HostTags_Tags_TagsId",
                        column: x => x.TagsId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(name: "IX_Credentials_CreatedByUserId", table: "Credentials", column: "CreatedByUserId");
            migrationBuilder.CreateIndex(name: "IX_Credentials_Name", table: "Credentials", column: "Name");
            migrationBuilder.CreateIndex(name: "IX_Credentials_UpdatedByUserId", table: "Credentials", column: "UpdatedByUserId");
            migrationBuilder.CreateIndex(name: "IX_HostCredentials_HostsId", table: "HostCredentials", column: "HostsId");
            migrationBuilder.CreateIndex(name: "IX_HostGroups_NormalizedName", table: "HostGroups", column: "NormalizedName", unique: true);
            migrationBuilder.CreateIndex(name: "IX_HostTags_TagsId", table: "HostTags", column: "TagsId");
            migrationBuilder.CreateIndex(name: "IX_Tags_NormalizedName", table: "Tags", column: "NormalizedName", unique: true);
        }
    }
}
