using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dingler.Data.Migrations.GameData
{
    /// <inheritdoc />
    public partial class AddReplayFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "ReplayFile",
                table: "GameReplays",
                type: "BLOB",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplayFile",
                table: "GameReplays");
        }
    }
}
