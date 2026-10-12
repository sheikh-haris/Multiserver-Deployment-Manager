using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IisControlCenter.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase2Projects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DriftEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObjectType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ObjectRef = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Before = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    After = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DetectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcknowledgedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriftEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DriftEvents_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LinkExceptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Violation = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    GrantedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkExceptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    OwnerGroupId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OwnerGroupName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ProdApproversRequired = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Components",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    HealthCheckPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AllowedFileTypes = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RequiresStagingBeforeProd = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Components", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Components_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectEnvironmentSettings",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    AllowSelfApproval = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectEnvironmentSettings", x => new { x.ProjectId, x.EnvironmentName });
                    table.ForeignKey(
                        name: "FK_ProjectEnvironmentSettings_Environments_EnvironmentName",
                        column: x => x.EnvironmentName,
                        principalTable: "Environments",
                        principalColumn: "Name",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectEnvironmentSettings_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IisSiteId = table.Column<long>(type: "bigint", nullable: false),
                    IisSiteName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    AppPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PhysicalPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    AppPoolName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    DeployOrder = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StatusDetail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LinkExceptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentTargets_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeploymentTargets_Environments_EnvironmentName",
                        column: x => x.EnvironmentName,
                        principalTable: "Environments",
                        principalColumn: "Name",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeploymentTargets_LinkExceptions_LinkExceptionId",
                        column: x => x.LinkExceptionId,
                        principalTable: "LinkExceptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeploymentTargets_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LinkRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IisSiteId = table.Column<long>(type: "bigint", nullable: false),
                    AppPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    RequestedById = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecisionComment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkRequests_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LinkRequests_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProtectedPaths",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtectedPaths", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProtectedPaths_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_ProjectId",
                table: "RoleAssignments",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Components_ProjectId_Name",
                table: "Components",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentTargets_ComponentId_EnvironmentName_ServerId",
                table: "DeploymentTargets",
                columns: new[] { "ComponentId", "EnvironmentName", "ServerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentTargets_EnvironmentName",
                table: "DeploymentTargets",
                column: "EnvironmentName");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentTargets_LinkExceptionId",
                table: "DeploymentTargets",
                column: "LinkExceptionId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentTargets_ServerId_IisSiteId_AppPath",
                table: "DeploymentTargets",
                columns: new[] { "ServerId", "IisSiteId", "AppPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriftEvents_ServerId_DetectedAt",
                table: "DriftEvents",
                columns: new[] { "ServerId", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LinkRequests_ComponentId",
                table: "LinkRequests",
                column: "ComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_LinkRequests_ServerId",
                table: "LinkRequests",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectEnvironmentSettings_EnvironmentName",
                table: "ProjectEnvironmentSettings",
                column: "EnvironmentName");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Name",
                table: "Projects",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Slug",
                table: "Projects",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProtectedPaths_ComponentId",
                table: "ProtectedPaths",
                column: "ComponentId");

            migrationBuilder.AddForeignKey(
                name: "FK_RoleAssignments_Projects_ProjectId",
                table: "RoleAssignments",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RoleAssignments_Projects_ProjectId",
                table: "RoleAssignments");

            migrationBuilder.DropTable(
                name: "DeploymentTargets");

            migrationBuilder.DropTable(
                name: "DriftEvents");

            migrationBuilder.DropTable(
                name: "LinkRequests");

            migrationBuilder.DropTable(
                name: "ProjectEnvironmentSettings");

            migrationBuilder.DropTable(
                name: "ProtectedPaths");

            migrationBuilder.DropTable(
                name: "LinkExceptions");

            migrationBuilder.DropTable(
                name: "Components");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignments_ProjectId",
                table: "RoleAssignments");
        }
    }
}
