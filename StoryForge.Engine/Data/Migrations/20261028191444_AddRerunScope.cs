using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryForge.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRerunScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RerunScope",
                table: "Cells",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RerunScope",
                table: "Cells");
        }
    }
}
