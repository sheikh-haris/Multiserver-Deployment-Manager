using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IisControlCenter.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase4aPoolApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PoolChangeRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PoolsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChangeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExpectedJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    RequestedById = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    DecidedById = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecisionComment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ResultMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoolChangeRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PoolChangeRequests_Status_ServerId",
                table: "PoolChangeRequests",
                columns: new[] { "Status", "ServerId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PoolChangeRequests");
        }
    }
}
