using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IisControlCenter.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase4AppPools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfigJson",
                table: "IisAppPools",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfigJson",
                table: "IisAppPools");
        }
    }
}
