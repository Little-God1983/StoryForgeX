using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StoryForge.Engine.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQueuedClipProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QueuedProfile",
                table: "Cells",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QueuedProfile",
                table: "Cells");
        }
    }
}
