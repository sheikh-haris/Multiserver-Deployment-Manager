using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IisControlCenter.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase4bIisOps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PayloadJson",
                table: "PoolChangeRequests",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "PoolChangeRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "PoolChangeRequests",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IisCreatedItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(1100)", maxLength: 1100, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CreatedById = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IisCreatedItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IisCreatedItems_ProjectId",
                table: "IisCreatedItems",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_IisCreatedItems_ServerId_Kind",
                table: "IisCreatedItems",
                columns: new[] { "ServerId", "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IisCreatedItems");

            migrationBuilder.DropColumn(
                name: "PayloadJson",
                table: "PoolChangeRequests");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "PoolChangeRequests");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "PoolChangeRequests");
        }
    }
}
