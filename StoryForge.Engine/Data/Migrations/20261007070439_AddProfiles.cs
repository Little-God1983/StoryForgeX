using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryForge.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProfileVersions",
                columns: table => new
                {
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    SavedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileVersions", x => new { x.ProfileId, x.Version });
                    table.ForeignKey(
                        name: "FK_ProfileVersions_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_Kind_Name",
                table: "Profiles",
                columns: new[] { "Kind", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProfileVersions");

            migrationBuilder.DropTable(
                name: "Profiles");
        }
    }
}
