using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dingler.Data.Migrations.GameData
{
    /// <inheritdoc />
    public partial class AddGameReplays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GameReplays",
                columns: table => new
                {
                    Id = table.Column<ulong>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionUid = table.Column<ulong>(type: "INTEGER", nullable: false),
                    GameId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    SessionName = table.Column<string>(type: "TEXT", nullable: false),
                    SiegeId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    FightSeq = table.Column<int>(type: "INTEGER", nullable: false),
                    SiegeLedgerId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    Player1Uid = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Player1Name = table.Column<string>(type: "TEXT", nullable: false),
                    Player2Uid = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Player2Name = table.Column<string>(type: "TEXT", nullable: true),
                    WinnerUid = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Started = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Ended = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ReplayFile = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameReplays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReplayEvents",
                columns: table => new
                {
                    Id = table.Column<ulong>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameReplayId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    PlayerUid = table.Column<ulong>(type: "INTEGER", nullable: false),
                    TimeMs = table.Column<long>(type: "INTEGER", nullable: false),
                    EventClass = table.Column<int>(type: "INTEGER", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplayEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReplayEvents_GameReplays_GameReplayId",
                        column: x => x.GameReplayId,
                        principalTable: "GameReplays",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameReplays_SiegeId",
                table: "GameReplays",
                column: "SiegeId");

            migrationBuilder.CreateIndex(
                name: "IX_ReplayEvents_GameReplayId",
                table: "ReplayEvents",
                column: "GameReplayId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReplayEvents");

            migrationBuilder.DropTable(
                name: "GameReplays");
        }
    }
}
