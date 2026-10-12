using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IisControlCenter.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase3Deployments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ToAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Packages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StoragePath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    AvResult = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AvDetail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AvScannedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EntryCount = table.Column<int>(type: "int", nullable: false),
                    UncompressedSize = table.Column<long>(type: "bigint", nullable: false),
                    WarningsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UploadedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    UploadedById = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Packages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Packages_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProtectedListConfirmations",
                columns: table => new
                {
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfirmedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ListHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtectedListConfirmations", x => x.ComponentId);
                    table.ForeignKey(
                        name: "FK_ProtectedListConfirmations_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UploadSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    TotalSize = table.Column<long>(type: "bigint", nullable: false),
                    ChunkSize = table.Column<int>(type: "int", nullable: false),
                    TotalChunks = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ErrorsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UploadSessions_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Mode = table.Column<int>(type: "int", nullable: false),
                    SourceReference = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", maxLength: 4100, nullable: true),
                    IsDirectToProd = table.Column<bool>(type: "bit", nullable: false),
                    DirectToProdReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SingleServerReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PromotedFromRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RollbackOfRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StatusDetail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RequestedBy = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    RequestedById = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RequestedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovalsRequired = table.Column<int>(type: "int", nullable: false),
                    SelfApproved = table.Column<bool>(type: "bit", nullable: false),
                    SubmitAssignmentIds = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReleaseBundleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BundleOrder = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentRequests_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeploymentRequests_Environments_EnvironmentName",
                        column: x => x.EnvironmentName,
                        principalTable: "Environments",
                        principalColumn: "Name",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeploymentRequests_Packages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "Packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Approvals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApproverId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ApproverName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsSelfApproval = table.Column<bool>(type: "bit", nullable: false),
                    AuthorizedByAssignmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Approvals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Approvals_DeploymentRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "DeploymentRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeploymentTargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BackupId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BackupPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    FailureStep = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    HealthDetail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Added = table.Column<int>(type: "int", nullable: false),
                    Replaced = table.Column<int>(type: "int", nullable: false),
                    Deleted = table.Column<int>(type: "int", nullable: false),
                    Skipped = table.Column<int>(type: "int", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    CurrentStep = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentRuns_DeploymentRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "DeploymentRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeploymentRuns_DeploymentTargets_DeploymentTargetId",
                        column: x => x.DeploymentTargetId,
                        principalTable: "DeploymentTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ForcedFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    MaskedDiffJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForcedFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ForcedFiles_DeploymentRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "DeploymentRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequestTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeploymentTargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeployOrder = table.Column<int>(type: "int", nullable: false),
                    PhysicalPathAtSubmit = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DeletesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Added = table.Column<int>(type: "int", nullable: false),
                    Replaced = table.Column<int>(type: "int", nullable: false),
                    Deleted = table.Column<int>(type: "int", nullable: false),
                    Skipped = table.Column<int>(type: "int", nullable: false),
                    Unchanged = table.Column<int>(type: "int", nullable: false),
                    DeletionWarning = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestTargets_DeploymentRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "DeploymentRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RequestTargets_DeploymentTargets_DeploymentTargetId",
                        column: x => x.DeploymentTargetId,
                        principalTable: "DeploymentTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentLogLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Seq = table.Column<int>(type: "int", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Level = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Step = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentLogLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentLogLines_DeploymentRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "DeploymentRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_RequestId_ApproverId",
                table: "Approvals",
                columns: new[] { "RequestId", "ApproverId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLogLines_RunId_Seq",
                table: "DeploymentLogLines",
                columns: new[] { "RunId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentRequests_ComponentId_EnvironmentName_Status",
                table: "DeploymentRequests",
                columns: new[] { "ComponentId", "EnvironmentName", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentRequests_EnvironmentName",
                table: "DeploymentRequests",
                column: "EnvironmentName");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentRequests_PackageId",
                table: "DeploymentRequests",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentRequests_Status_EnvironmentName",
                table: "DeploymentRequests",
                columns: new[] { "Status", "EnvironmentName" });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentRuns_DeploymentTargetId",
                table: "DeploymentRuns",
                column: "DeploymentTargetId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentRuns_RequestId_Sequence",
                table: "DeploymentRuns",
                columns: new[] { "RequestId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ForcedFiles_RequestId",
                table: "ForcedFiles",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Status",
                table: "Notifications",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Packages_ComponentId_Sha256",
                table: "Packages",
                columns: new[] { "ComponentId", "Sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_Packages_ExpiresAt",
                table: "Packages",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_RequestTargets_DeploymentTargetId",
                table: "RequestTargets",
                column: "DeploymentTargetId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestTargets_RequestId_DeployOrder",
                table: "RequestTargets",
                columns: new[] { "RequestId", "DeployOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_ComponentId",
                table: "UploadSessions",
                column: "ComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_ExpiresAt",
                table: "UploadSessions",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Approvals");

            migrationBuilder.DropTable(
                name: "DeploymentLogLines");

            migrationBuilder.DropTable(
                name: "ForcedFiles");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "ProtectedListConfirmations");

            migrationBuilder.DropTable(
                name: "RequestTargets");

            migrationBuilder.DropTable(
                name: "UploadSessions");

            migrationBuilder.DropTable(
                name: "DeploymentRuns");

            migrationBuilder.DropTable(
                name: "DeploymentRequests");

            migrationBuilder.DropTable(
                name: "Packages");
        }
    }
}
