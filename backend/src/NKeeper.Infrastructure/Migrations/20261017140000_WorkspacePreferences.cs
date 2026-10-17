using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NKeeper.Infrastructure.Persistence;

#nullable disable

namespace NKeeper.Infrastructure.Migrations;

[DbContext(typeof(NKeeperDbContext))]
[Migration("20261017140000_WorkspacePreferences")]
public partial class WorkspacePreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string[]>(
            name: "WorkspaceGroupBy",
            table: "AspNetUsers",
            type: "text[]",
            nullable: false,
            defaultValue: Array.Empty<string>());

        migrationBuilder.AddColumn<string>(
            name: "WorkspaceSort",
            table: "AspNetUsers",
            type: "text",
            nullable: false,
            defaultValue: "name");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "WorkspaceGroupBy", table: "AspNetUsers");
        migrationBuilder.DropColumn(name: "WorkspaceSort", table: "AspNetUsers");
    }
}
