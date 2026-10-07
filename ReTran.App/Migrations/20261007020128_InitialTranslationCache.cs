using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReTran_App.Migrations
{
    /// <inheritdoc />
    public partial class InitialTranslationCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Translations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Context = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Src = table.Column<string>(type: "TEXT", nullable: false),
                    Dst = table.Column<string>(type: "TEXT", nullable: false),
                    Translated = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Translations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Translations_Context_Text_Src_Dst",
                table: "Translations",
                columns: new[] { "Context", "Text", "Src", "Dst" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Translations");
        }
    }
}
