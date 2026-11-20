using Microsoft.EntityFrameworkCore.Migrations;

namespace SmartGallery.Infrastructure.Persistence.Migrations;

public partial class AddPhotoAnalyses : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PhotoAnalyses",
            columns: table => new
            {
                FileId = table.Column<Guid>(type: "TEXT", nullable: false),
                AnalyzedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                Caption = table.Column<string>(type: "TEXT", nullable: false),
                SuggestedName = table.Column<string>(type: "TEXT", nullable: true),
                Truncated = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PhotoAnalyses", columns => columns.FileId);
                table.ForeignKey("FK_PhotoAnalyses_AssetFiles_FileId", columns => columns.FileId,
                    principalTable: "AssetFiles", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "PhotoAnalyses");
}
