using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dingler.Data.Migrations.GameData
{
    /// <inheritdoc />
    public partial class AddSiegeTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sieges",
                columns: table => new
                {
                    Id = table.Column<ulong>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlayerProfileId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    PlayerName = table.Column<string>(type: "TEXT", nullable: false),
                    Currency = table.Column<int>(type: "INTEGER", nullable: false),
                    Pot = table.Column<int>(type: "INTEGER", nullable: false),
                    ChallengeLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    FightInfo = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Wins = table.Column<int>(type: "INTEGER", nullable: false),
                    Loss = table.Column<int>(type: "INTEGER", nullable: false),
                    ChallengeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StatusDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RequestClose = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CloseDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastChallengeDate = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sieges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sieges_PlayerProfiles_PlayerProfileId",
                        column: x => x.PlayerProfileId,
                        principalTable: "PlayerProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SiegeLedger",
                columns: table => new
                {
                    Id = table.Column<ulong>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SiegeId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Action = table.Column<int>(type: "INTEGER", nullable: false),
                    Context = table.Column<string>(type: "TEXT", nullable: true),
                    FightSeq = table.Column<int>(type: "INTEGER", nullable: false),
                    Wins = table.Column<int>(type: "INTEGER", nullable: false),
                    PlayerProfileId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    PlayerName = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerDeck = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Forfeit = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Escrow = table.Column<int>(type: "INTEGER", nullable: false),
                    Bet = table.Column<int>(type: "INTEGER", nullable: false),
                    Fee = table.Column<int>(type: "INTEGER", nullable: false),
                    Rake = table.Column<int>(type: "INTEGER", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiegeLedger", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SiegeLedger_PlayerProfiles_PlayerProfileId",
                        column: x => x.PlayerProfileId,
                        principalTable: "PlayerProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SiegeLedger_Sieges_SiegeId",
                        column: x => x.SiegeId,
                        principalTable: "Sieges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SiegeLedger_PlayerProfileId",
                table: "SiegeLedger",
                column: "PlayerProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_SiegeLedger_SiegeId",
                table: "SiegeLedger",
                column: "SiegeId");

            migrationBuilder.CreateIndex(
                name: "IX_Sieges_PlayerProfileId",
                table: "Sieges",
                column: "PlayerProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Sieges_Status",
                table: "Sieges",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiegeLedger");

            migrationBuilder.DropTable(
                name: "Sieges");
        }
    }
}
