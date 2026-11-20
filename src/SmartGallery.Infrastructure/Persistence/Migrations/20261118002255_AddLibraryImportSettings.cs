using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartGallery.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryImportSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileNaming",
                table: "Libraries",
                type: "TEXT",
                nullable: false,
                defaultValue: "KeepOriginal");

            migrationBuilder.AddColumn<string>(
                name: "FolderLayout",
                table: "Libraries",
                type: "TEXT",
                nullable: false,
                defaultValue: "YearMonth");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileNaming",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "FolderLayout",
                table: "Libraries");
        }
    }
}
