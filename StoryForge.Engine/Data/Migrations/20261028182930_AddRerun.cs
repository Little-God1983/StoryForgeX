using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryForge.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRerun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Rerun",
                table: "Cells",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Rerun",
                table: "Cells");
        }
    }
}
