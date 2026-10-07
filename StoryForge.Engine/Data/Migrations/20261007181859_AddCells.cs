using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryForge.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCells : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Cells",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    ApprovedVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    ActivityJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cells", x => new { x.ProjectId, x.Stage, x.Key });
                    table.ForeignKey(
                        name: "FK_Cells_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CellVersions",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Origin = table.Column<string>(type: "TEXT", nullable: false),
                    BasedOn = table.Column<int>(type: "INTEGER", nullable: true),
                    InputHash = table.Column<string>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    OutputJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CellVersions", x => new { x.ProjectId, x.Stage, x.Key, x.Version });
                    table.ForeignKey(
                        name: "FK_CellVersions_Cells_ProjectId_Stage_Key",
                        columns: x => new { x.ProjectId, x.Stage, x.Key },
                        principalTable: "Cells",
                        principalColumns: new[] { "ProjectId", "Stage", "Key" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CellVersions");

            migrationBuilder.DropTable(
                name: "Cells");
        }
    }
}
