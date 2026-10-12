using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace IisControlCenter.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TargetId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EnvironmentName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Before = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    After = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Result = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ClientIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PrevHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Environments",
                columns: table => new
                {
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsProduction = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Environments", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrincipalType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PrincipalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PrincipalName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EnvironmentName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Role = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    GrantedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleAssignments_Environments_EnvironmentName",
                        column: x => x.EnvironmentName,
                        principalTable: "Environments",
                        principalColumn: "Name",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Servers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Hostname = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    EnvironmentName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DeclaredEnvironment = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AgentStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsDomainJoined = table.Column<bool>(type: "bit", nullable: false),
                    AgentVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    OsVersion = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LocalAllowManaged = table.Column<bool>(type: "bit", nullable: false),
                    AllowedRoots = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IisInstalled = table.Column<bool>(type: "bit", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastDiscoveryAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Servers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Servers_Environments_EnvironmentName",
                        column: x => x.EnvironmentName,
                        principalTable: "Environments",
                        principalColumn: "Name",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentCertificates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Thumbprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NotAfter = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RevokedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokeReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCertificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentCertificates_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IisAppPools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    IdentityType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RuntimeVersion = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    PipelineMode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    State = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IisAppPools", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IisAppPools_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IisCertificates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Thumbprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    NotAfter = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Store = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IisCertificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IisCertificates_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IisSites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IisSiteId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    State = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PhysicalPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AppPoolName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    MappingStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DiscoveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IisSites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IisSites_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RegistrationTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegistrationTokens_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServerDotNetVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerDotNetVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServerDotNetVersions_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IisApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IisSiteRowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PhysicalPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AppPoolName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IisApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IisApplications_IisSites_IisSiteRowId",
                        column: x => x.IisSiteRowId,
                        principalTable: "IisSites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IisBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IisSiteRowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Protocol = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Port = table.Column<int>(type: "int", nullable: false),
                    Host = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CertThumbprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IisBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IisBindings_IisSites_IisSiteRowId",
                        column: x => x.IisSiteRowId,
                        principalTable: "IisSites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IisVirtualDirs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IisApplicationRowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PhysicalPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IisVirtualDirs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IisVirtualDirs_IisApplications_IisApplicationRowId",
                        column: x => x.IisApplicationRowId,
                        principalTable: "IisApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Environments",
                columns: new[] { "Name", "IsProduction", "SortOrder" },
                values: new object[,]
                {
                    { "Dev", false, 1 },
                    { "Prod", true, 3 },
                    { "Staging", false, 2 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCertificates_ServerId",
                table: "AgentCertificates",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCertificates_Thumbprint",
                table: "AgentCertificates",
                column: "Thumbprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Action",
                table: "AuditLog",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_ServerId",
                table: "AuditLog",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Timestamp",
                table: "AuditLog",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_IisApplications_IisSiteRowId",
                table: "IisApplications",
                column: "IisSiteRowId");

            migrationBuilder.CreateIndex(
                name: "IX_IisAppPools_ServerId_Name",
                table: "IisAppPools",
                columns: new[] { "ServerId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IisBindings_IisSiteRowId",
                table: "IisBindings",
                column: "IisSiteRowId");

            migrationBuilder.CreateIndex(
                name: "IX_IisCertificates_ServerId",
                table: "IisCertificates",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_IisSites_ServerId_IisSiteId",
                table: "IisSites",
                columns: new[] { "ServerId", "IisSiteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IisVirtualDirs_IisApplicationRowId",
                table: "IisVirtualDirs",
                column: "IisApplicationRowId");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationTokens_ServerId",
                table: "RegistrationTokens",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationTokens_TokenHash",
                table: "RegistrationTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_EnvironmentName",
                table: "RoleAssignments",
                column: "EnvironmentName");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_PrincipalId",
                table: "RoleAssignments",
                column: "PrincipalId");

            migrationBuilder.CreateIndex(
                name: "IX_ServerDotNetVersions_ServerId",
                table: "ServerDotNetVersions",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_Servers_EnvironmentName",
                table: "Servers",
                column: "EnvironmentName");

            migrationBuilder.CreateIndex(
                name: "IX_Servers_Name",
                table: "Servers",
                column: "Name",
                unique: true,
                filter: "[IsDeleted] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentCertificates");

            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "IisAppPools");

            migrationBuilder.DropTable(
                name: "IisBindings");

            migrationBuilder.DropTable(
                name: "IisCertificates");

            migrationBuilder.DropTable(
                name: "IisVirtualDirs");

            migrationBuilder.DropTable(
                name: "RegistrationTokens");

            migrationBuilder.DropTable(
                name: "RoleAssignments");

            migrationBuilder.DropTable(
                name: "ServerDotNetVersions");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "IisApplications");

            migrationBuilder.DropTable(
                name: "IisSites");

            migrationBuilder.DropTable(
                name: "Servers");

            migrationBuilder.DropTable(
                name: "Environments");
        }
    }
}
