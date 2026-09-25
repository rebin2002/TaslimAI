using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderResilienceFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    PreferredLanguage = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    DefaultGenerationLanguage = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OutputPreference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IncludeSourceLinks = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    OnboardingCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OnboardingIntent = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FriendlyName = table.Column<string>(type: "text", nullable: true),
                    Xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MonthlyPriceUsd = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    MonthlyCreditAllowance = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProviderCircuits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Capability = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OpenUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProbeExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastFailureAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderCircuits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    Slug = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentReconciliationRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Provider = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ProviderObjectType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ProviderObjectReference = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    LocalEntityType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    LocalEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentReconciliationRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentReconciliationRecords_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PersonalMemories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Content = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalMemories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PersonalMemories_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PersonalMemories_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Instructions = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ContextNotes = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Projects_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProviderCustomerReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ProviderCustomerReferenceValue = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderCustomerReferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderCustomerReferences_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    BillingProvider = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ProviderSubscriptionReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CurrentPeriodStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CurrentPeriodEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextRenewalAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CancelAtPeriodEnd = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkspaceMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkspaceMembers_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextMessageSequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversations_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Conversations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Conversations_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GenerationJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ProviderModel = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    InputJson = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    RequestFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RequestId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ResultJson = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ProgressPercent = table.Column<int>(type: "integer", nullable: false),
                    CancellationRequested = table.Column<bool>(type: "boolean", nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationJobs", x => x.Id);
                    table.CheckConstraint("CK_GenerationJobs_ProgressPercent", "\"ProgressPercent\" BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "FK_GenerationJobs_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GenerationJobs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GenerationJobs_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MovieProjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    AspectRatio = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Style = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Language = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    AdditionalInstructions = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieProjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieProjects_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MovieProjects_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieProjects_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BillingPeriods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    StartsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IncludedCredits = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingPeriods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BillingPeriods_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CheckoutSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Provider = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ProviderSessionReference = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckoutSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CheckoutSessions_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CheckoutSessions_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CheckoutSessions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionLifecycleEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Source = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ProviderEventReference = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionLifecycleEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionLifecycleEvents_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubscriptionLifecycleEvents_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Content = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    AttachmentManifestJson = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    ProviderKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ModelKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    InputTokens = table.Column<int>(type: "integer", nullable: true),
                    CachedInputTokens = table.Column<int>(type: "integer", nullable: true),
                    OutputTokens = table.Column<int>(type: "integer", nullable: true),
                    EstimatedCost = table.Column<decimal>(type: "numeric", nullable: true),
                    ActualCost = table.Column<decimal>(type: "numeric", nullable: true),
                    LatencyMs = table.Column<int>(type: "integer", nullable: true),
                    FinishReason = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatMessages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StoredFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginalFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    StoredFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Extension = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageProvider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TextExtractionStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ExtractedText = table.Column<string>(type: "character varying(1000000)", maxLength: 1000000, nullable: true),
                    ExtractedTextLength = table.Column<int>(type: "integer", nullable: true),
                    MetadataJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoredFiles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StoredFiles_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StoredFiles_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StoredFiles_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActivityReadStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityReadStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityReadStates_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ActivityReadStates_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProviderAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    Capability = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    ResultCategory = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LatencyMs = table.Column<long>(type: "bigint", nullable: true),
                    EstimatedCostUsd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    IsRetry = table.Column<bool>(type: "boolean", nullable: false),
                    IsFallback = table.Column<bool>(type: "boolean", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderAttempts_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProviderExecutionFinalizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderExecutionFinalizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderExecutionFinalizations_GenerationJobs_GenerationJob~",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UsageTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Feature = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Model = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    InputTokens = table.Column<int>(type: "integer", nullable: true),
                    CachedInputTokens = table.Column<int>(type: "integer", nullable: true),
                    OutputTokens = table.Column<int>(type: "integer", nullable: true),
                    ImageInputTokens = table.Column<int>(type: "integer", nullable: true),
                    ImageOutputTokens = table.Column<int>(type: "integer", nullable: true),
                    LatencyMs = table.Column<int>(type: "integer", nullable: true),
                    EstimatedProviderCostUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    ProviderCostUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    ChargedAmount = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    ChargedUnit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CostBasis = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PricingVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PricingSnapshotJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    SafeMetadataJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    IsAnomalous = table.Column<bool>(type: "boolean", nullable: false),
                    AnomalyCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AnomalyDetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefundedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsageTransactions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UsageTransactions_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UsageTransactions_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UsageTransactions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_UsageTransactions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MovieContinuityGuides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    VisualLanguage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CameraLanguage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ColorAndLighting = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SoundAndNarration = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ContinuityRules = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    ReferenceAssetIdsJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieContinuityGuides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieContinuityGuides_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieScenes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Summary = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    ContinuityNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Narration = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    Dialogue = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieScenes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieScenes_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CreditEntitlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    BillingPeriodId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    GrantedCredits = table.Column<long>(type: "bigint", nullable: false),
                    GrantedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    SourceReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditEntitlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CreditEntitlements_BillingPeriods_BillingPeriodId",
                        column: x => x.BillingPeriodId,
                        principalTable: "BillingPeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CreditEntitlements_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceGenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AssetType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    MetadataJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Assets_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assets_GenerationJobs_SourceGenerationJobId",
                        column: x => x.SourceGenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assets_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Assets_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assets_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessageAttachments",
                columns: table => new
                {
                    ChatMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessageAttachments", x => new { x.ChatMessageId, x.StoredFileId });
                    table.ForeignKey(
                        name: "FK_ChatMessageAttachments_ChatMessages_ChatMessageId",
                        column: x => x.ChatMessageId,
                        principalTable: "ChatMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatMessageAttachments_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GenerationJobOutputs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutputType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    MetadataJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenerationJobOutputs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenerationJobOutputs_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GenerationJobOutputs_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ResearchSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    CitationId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CanonicalUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Domain = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Publisher = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetrievedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Snippet = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ExtractedText = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    SearchQuery = table.Column<string>(type: "character varying(800)", maxLength: 800, nullable: true),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    IsSelected = table.Column<bool>(type: "boolean", nullable: false),
                    MetadataJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResearchSources_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResearchSources_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ResearchSources_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MovieShots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    CameraAndFraming = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CameraMotion = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    Narration = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    Dialogue = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    VisualContinuityNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieShots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieShots_MovieScenes_MovieSceneId",
                        column: x => x.MovieSceneId,
                        principalTable: "MovieScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CreditLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreditEntitlementId = table.Column<Guid>(type: "uuid", nullable: true),
                    UsageTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversesEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditLedgerEntries", x => x.Id);
                    table.CheckConstraint("CK_CreditLedgerEntries_NonZeroAmount", "\"Amount\" <> 0");
                    table.ForeignKey(
                        name: "FK_CreditLedgerEntries_CreditEntitlements_CreditEntitlementId",
                        column: x => x.CreditEntitlementId,
                        principalTable: "CreditEntitlements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CreditLedgerEntries_UsageTransactions_UsageTransactionId",
                        column: x => x.UsageTransactionId,
                        principalTable: "UsageTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CreditLedgerEntries_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetRepresentations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepresentationType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetRepresentations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetRepresentations_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssetRepresentations_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MovieAssemblies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OutputFormat = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MetadataJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieAssemblies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieAssemblies_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieAssemblies_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieAssemblies_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieCharacters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Appearance = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    VoiceAndPerformance = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ContinuityNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieCharacters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieCharacters_Assets_ReferenceAssetId",
                        column: x => x.ReferenceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieCharacters_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MovieLocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    VisualContinuityNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ReferenceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieLocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieLocations_Assets_ReferenceAssetId",
                        column: x => x.ReferenceAssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieLocations_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    DeduplicationKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    ResourceTitle = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Notifications_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Notifications_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Notifications_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Notifications_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ResearchEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResearchSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Excerpt = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Context = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResearchEvidence_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResearchEvidence_ResearchSources_ResearchSourceId",
                        column: x => x.ResearchSourceId,
                        principalTable: "ResearchSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResearchEvidence_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MovieClips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieSceneId = table.Column<Guid>(type: "uuid", nullable: true),
                    MovieShotId = table.Column<Guid>(type: "uuid", nullable: true),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ProviderClipId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    MetadataJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    ContinuitySnapshotJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieClips", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieClips_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieClips_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieClips_MovieProjects_MovieProjectId",
                        column: x => x.MovieProjectId,
                        principalTable: "MovieProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieClips_MovieScenes_MovieSceneId",
                        column: x => x.MovieSceneId,
                        principalTable: "MovieScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieClips_MovieShots_MovieShotId",
                        column: x => x.MovieShotId,
                        principalTable: "MovieShots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MovieClips_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PaymentAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CheckoutSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreditLedgerEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Provider = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ProviderPaymentReference = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SucceededAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentAttempts_CheckoutSessions_CheckoutSessionId",
                        column: x => x.CheckoutSessionId,
                        principalTable: "CheckoutSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PaymentAttempts_CreditLedgerEntries_CreditLedgerEntryId",
                        column: x => x.CreditLedgerEntryId,
                        principalTable: "CreditLedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PaymentAttempts_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PaymentAttempts_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MovieVideoProviderExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovieClipId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProviderJobId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    PollCount = table.Column<int>(type: "integer", nullable: false),
                    ProgressPercent = table.Column<int>(type: "integer", nullable: false),
                    LastErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    NextPollAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovieVideoProviderExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MovieVideoProviderExecutions_GenerationJobs_GenerationJobId",
                        column: x => x.GenerationJobId,
                        principalTable: "GenerationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MovieVideoProviderExecutions_MovieClips_MovieClipId",
                        column: x => x.MovieClipId,
                        principalTable: "MovieClips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ProviderEventReference = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CheckoutSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PaymentAttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: false),
                    SignatureVerified = table.Column<bool>(type: "boolean", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentEvents_CheckoutSessions_CheckoutSessionId",
                        column: x => x.CheckoutSessionId,
                        principalTable: "CheckoutSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PaymentEvents_PaymentAttempts_PaymentAttemptId",
                        column: x => x.PaymentAttemptId,
                        principalTable: "PaymentAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PaymentEvents_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PaymentEvents_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PaymentRefunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentAttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ProviderRefundReference = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRefunds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentRefunds_PaymentAttempts_PaymentAttemptId",
                        column: x => x.PaymentAttemptId,
                        principalTable: "PaymentAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentRefunds_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Plans",
                columns: new[] { "Id", "Code", "CreatedAt", "Currency", "Description", "IsActive", "MonthlyCreditAllowance", "MonthlyPriceUsd", "Name", "SortOrder", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("0f0e0d0c-0b0a-0908-0706-050403020100"), "free", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "USD", "A no-cost Taslim foundation plan.", true, 1000L, 0m, "Free", 1, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("1f1e1d1c-1b1a-1918-1716-151413121110"), "pro", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "USD", "For regular individual work.", true, 10000L, 9m, "Pro", 2, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("2f2e2d2c-2b2a-2928-2726-252423222120"), "ultra", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "USD", "For heavier individual usage.", true, 30000L, 19m, "Ultra", 3, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("3f3e3d3c-3b3a-3938-3736-353433323130"), "mega", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "USD", "For high-volume creative work.", true, 60000L, 29m, "Mega", 4, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("4f4e4d4c-4b4a-4948-4746-454443424140"), "business", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "USD", "For teams and business workspaces.", true, 150000L, 59m, "Business", 5, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityReadStates_GenerationJobId",
                table: "ActivityReadStates",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityReadStates_UserId_GenerationJobId",
                table: "ActivityReadStates",
                columns: new[] { "UserId", "GenerationJobId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetRepresentations_AssetId_RepresentationType",
                table: "AssetRepresentations",
                columns: new[] { "AssetId", "RepresentationType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetRepresentations_StoredFileId",
                table: "AssetRepresentations",
                column: "StoredFileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assets_CreatedByUserId",
                table: "Assets",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_ProjectId_Status_CreatedAt",
                table: "Assets",
                columns: new[] { "ProjectId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_SourceGenerationJobId",
                table: "Assets",
                column: "SourceGenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_StoredFileId",
                table: "Assets",
                column: "StoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_WorkspaceId_AssetType_Status_CreatedAt",
                table: "Assets",
                columns: new[] { "WorkspaceId", "AssetType", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_WorkspaceId_Name",
                table: "Assets",
                columns: new[] { "WorkspaceId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Assets_WorkspaceId_Status_CreatedAt",
                table: "Assets",
                columns: new[] { "WorkspaceId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingPeriods_Status_EndsAt",
                table: "BillingPeriods",
                columns: new[] { "Status", "EndsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingPeriods_SubscriptionId_StartsAt",
                table: "BillingPeriods",
                columns: new[] { "SubscriptionId", "StartsAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessageAttachments_StoredFileId_ChatMessageId",
                table: "ChatMessageAttachments",
                columns: new[] { "StoredFileId", "ChatMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ConversationId_CreatedAt_Id",
                table: "ChatMessages",
                columns: new[] { "ConversationId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ConversationId_RequestId",
                table: "ChatMessages",
                columns: new[] { "ConversationId", "RequestId" },
                unique: true,
                filter: "\"RequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ConversationId_Sequence",
                table: "ChatMessages",
                columns: new[] { "ConversationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CheckoutSessions_PlanId",
                table: "CheckoutSessions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_CheckoutSessions_Provider_ProviderSessionReference",
                table: "CheckoutSessions",
                columns: new[] { "Provider", "ProviderSessionReference" },
                unique: true,
                filter: "\"ProviderSessionReference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CheckoutSessions_Status_ExpiresAt",
                table: "CheckoutSessions",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CheckoutSessions_SubscriptionId",
                table: "CheckoutSessions",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_CheckoutSessions_WorkspaceId_IdempotencyKey",
                table: "CheckoutSessions",
                columns: new[] { "WorkspaceId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_ProjectId",
                table: "Conversations",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_UserId_WorkspaceId_Status_UpdatedAt",
                table: "Conversations",
                columns: new[] { "UserId", "WorkspaceId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_WorkspaceId_UpdatedAt",
                table: "Conversations",
                columns: new[] { "WorkspaceId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditEntitlements_BillingPeriodId",
                table: "CreditEntitlements",
                column: "BillingPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditEntitlements_WorkspaceId_ExpiresAt",
                table: "CreditEntitlements",
                columns: new[] { "WorkspaceId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditEntitlements_WorkspaceId_IdempotencyKey",
                table: "CreditEntitlements",
                columns: new[] { "WorkspaceId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditLedgerEntries_CreditEntitlementId",
                table: "CreditLedgerEntries",
                column: "CreditEntitlementId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditLedgerEntries_ReversesEntryId",
                table: "CreditLedgerEntries",
                column: "ReversesEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditLedgerEntries_UsageTransactionId",
                table: "CreditLedgerEntries",
                column: "UsageTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditLedgerEntries_WorkspaceId_CreatedAt",
                table: "CreditLedgerEntries",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditLedgerEntries_WorkspaceId_IdempotencyKey",
                table: "CreditLedgerEntries",
                columns: new[] { "WorkspaceId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobOutputs_GenerationJobId",
                table: "GenerationJobOutputs",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobOutputs_StoredFileId",
                table: "GenerationJobOutputs",
                column: "StoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_CreatedByUserId_IdempotencyKey",
                table: "GenerationJobs",
                columns: new[] { "CreatedByUserId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_ProjectId",
                table: "GenerationJobs",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_Status_QueuedAt_CreatedAt",
                table: "GenerationJobs",
                columns: new[] { "Status", "QueuedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_WorkspaceId_CreatedAt",
                table: "GenerationJobs",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GenerationJobs_WorkspaceId_Status_CreatedAt",
                table: "GenerationJobs",
                columns: new[] { "WorkspaceId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieAssemblies_AssetId",
                table: "MovieAssemblies",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieAssemblies_GenerationJobId",
                table: "MovieAssemblies",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieAssemblies_MovieProjectId_CreatedAt",
                table: "MovieAssemblies",
                columns: new[] { "MovieProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacters_MovieProjectId",
                table: "MovieCharacters",
                column: "MovieProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieCharacters_ReferenceAssetId",
                table: "MovieCharacters",
                column: "ReferenceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieClips_AssetId",
                table: "MovieClips",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieClips_GenerationJobId",
                table: "MovieClips",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieClips_MovieProjectId_Status",
                table: "MovieClips",
                columns: new[] { "MovieProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieClips_MovieSceneId",
                table: "MovieClips",
                column: "MovieSceneId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieClips_MovieShotId",
                table: "MovieClips",
                column: "MovieShotId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieClips_StoredFileId",
                table: "MovieClips",
                column: "StoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieContinuityGuides_MovieProjectId",
                table: "MovieContinuityGuides",
                column: "MovieProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieLocations_MovieProjectId",
                table: "MovieLocations",
                column: "MovieProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieLocations_ReferenceAssetId",
                table: "MovieLocations",
                column: "ReferenceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProjects_CreatedByUserId",
                table: "MovieProjects",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProjects_ProjectId",
                table: "MovieProjects",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieProjects_WorkspaceId_UpdatedAt",
                table: "MovieProjects",
                columns: new[] { "WorkspaceId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MovieScenes_MovieProjectId_Sequence",
                table: "MovieScenes",
                columns: new[] { "MovieProjectId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieShots_MovieSceneId_Sequence",
                table: "MovieShots",
                columns: new[] { "MovieSceneId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieVideoProviderExecutions_GenerationJobId",
                table: "MovieVideoProviderExecutions",
                column: "GenerationJobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MovieVideoProviderExecutions_MovieClipId",
                table: "MovieVideoProviderExecutions",
                column: "MovieClipId");

            migrationBuilder.CreateIndex(
                name: "IX_MovieVideoProviderExecutions_Status_NextPollAt",
                table: "MovieVideoProviderExecutions",
                columns: new[] { "Status", "NextPollAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_AssetId",
                table: "Notifications",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_GenerationJobId",
                table: "Notifications",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ProjectId",
                table: "Notifications",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_DeduplicationKey",
                table: "Notifications",
                columns: new[] { "UserId", "DeduplicationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_WorkspaceId_ReadAt_CreatedAt",
                table: "Notifications",
                columns: new[] { "UserId", "WorkspaceId", "ReadAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_WorkspaceId",
                table: "Notifications",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_CheckoutSessionId",
                table: "PaymentAttempts",
                column: "CheckoutSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_CreditLedgerEntryId",
                table: "PaymentAttempts",
                column: "CreditLedgerEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_Provider_ProviderPaymentReference",
                table: "PaymentAttempts",
                columns: new[] { "Provider", "ProviderPaymentReference" },
                unique: true,
                filter: "\"ProviderPaymentReference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_Status",
                table: "PaymentAttempts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_SubscriptionId",
                table: "PaymentAttempts",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_WorkspaceId_CreatedAt",
                table: "PaymentAttempts",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_WorkspaceId_IdempotencyKey",
                table: "PaymentAttempts",
                columns: new[] { "WorkspaceId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_CheckoutSessionId",
                table: "PaymentEvents",
                column: "CheckoutSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_PaymentAttemptId",
                table: "PaymentEvents",
                column: "PaymentAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_Provider_ProviderEventReference",
                table: "PaymentEvents",
                columns: new[] { "Provider", "ProviderEventReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_Status_ReceivedAt",
                table: "PaymentEvents",
                columns: new[] { "Status", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_SubscriptionId",
                table: "PaymentEvents",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_WorkspaceId",
                table: "PaymentEvents",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReconciliationRecords_Provider_ProviderObjectType_Pr~",
                table: "PaymentReconciliationRecords",
                columns: new[] { "Provider", "ProviderObjectType", "ProviderObjectReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReconciliationRecords_Status_UpdatedAt",
                table: "PaymentReconciliationRecords",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReconciliationRecords_WorkspaceId",
                table: "PaymentReconciliationRecords",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRefunds_PaymentAttemptId_CreatedAt",
                table: "PaymentRefunds",
                columns: new[] { "PaymentAttemptId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRefunds_Provider_ProviderRefundReference",
                table: "PaymentRefunds",
                columns: new[] { "Provider", "ProviderRefundReference" },
                unique: true,
                filter: "\"ProviderRefundReference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRefunds_WorkspaceId_IdempotencyKey",
                table: "PaymentRefunds",
                columns: new[] { "WorkspaceId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PersonalMemories_UserId_IsActive",
                table: "PersonalMemories",
                columns: new[] { "UserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_PersonalMemories_WorkspaceId_UpdatedAt",
                table: "PersonalMemories",
                columns: new[] { "WorkspaceId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PersonalMemories_WorkspaceId_UserId",
                table: "PersonalMemories",
                columns: new[] { "WorkspaceId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Plans_Code",
                table: "Plans",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_WorkspaceId",
                table: "Projects",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_WorkspaceId_Status",
                table: "Projects",
                columns: new[] { "WorkspaceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAttempts_GenerationJobId_IdempotencyKey",
                table: "ProviderAttempts",
                columns: new[] { "GenerationJobId", "IdempotencyKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAttempts_ProviderKey_Capability_StartedAt",
                table: "ProviderAttempts",
                columns: new[] { "ProviderKey", "Capability", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderCircuits_ProviderKey_Capability",
                table: "ProviderCircuits",
                columns: new[] { "ProviderKey", "Capability" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderCircuits_State_OpenUntil",
                table: "ProviderCircuits",
                columns: new[] { "State", "OpenUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderCustomerReferences_WorkspaceId_Provider_IsDefault",
                table: "ProviderCustomerReferences",
                columns: new[] { "WorkspaceId", "Provider", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderCustomerReferences_WorkspaceId_Provider_ProviderCus~",
                table: "ProviderCustomerReferences",
                columns: new[] { "WorkspaceId", "Provider", "ProviderCustomerReferenceValue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderExecutionFinalizations_GenerationJobId_IdempotencyK~",
                table: "ProviderExecutionFinalizations",
                columns: new[] { "GenerationJobId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderExecutionFinalizations_State_ClaimExpiresAt",
                table: "ProviderExecutionFinalizations",
                columns: new[] { "State", "ClaimExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchEvidence_GenerationJobId_ResearchSourceId",
                table: "ResearchEvidence",
                columns: new[] { "GenerationJobId", "ResearchSourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchEvidence_ResearchSourceId",
                table: "ResearchEvidence",
                column: "ResearchSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ResearchEvidence_WorkspaceId",
                table: "ResearchEvidence",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ResearchSources_GenerationJobId_CitationId",
                table: "ResearchSources",
                columns: new[] { "GenerationJobId", "CitationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResearchSources_StoredFileId",
                table: "ResearchSources",
                column: "StoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_ResearchSources_WorkspaceId_RetrievedAt",
                table: "ResearchSources",
                columns: new[] { "WorkspaceId", "RetrievedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_ConversationId",
                table: "StoredFiles",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_ProjectId",
                table: "StoredFiles",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_Status",
                table: "StoredFiles",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_StorageKey",
                table: "StoredFiles",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_UserId_CreatedAt",
                table: "StoredFiles",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_WorkspaceId_CreatedAt",
                table: "StoredFiles",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionLifecycleEvents_ProviderEventReference",
                table: "SubscriptionLifecycleEvents",
                column: "ProviderEventReference");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionLifecycleEvents_SubscriptionId_CreatedAt",
                table: "SubscriptionLifecycleEvents",
                columns: new[] { "SubscriptionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionLifecycleEvents_WorkspaceId",
                table: "SubscriptionLifecycleEvents",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_PlanId",
                table: "Subscriptions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_Status_NextRenewalAt",
                table: "Subscriptions",
                columns: new[] { "Status", "NextRenewalAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_WorkspaceId",
                table: "Subscriptions",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactions_ConversationId",
                table: "UsageTransactions",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactions_Feature",
                table: "UsageTransactions",
                column: "Feature");

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactions_GenerationJobId_CreatedAt",
                table: "UsageTransactions",
                columns: new[] { "GenerationJobId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactions_IsAnomalous",
                table: "UsageTransactions",
                column: "IsAnomalous");

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactions_ProjectId",
                table: "UsageTransactions",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactions_Provider",
                table: "UsageTransactions",
                column: "Provider");

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactions_Status",
                table: "UsageTransactions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactions_UserId_CreatedAt",
                table: "UsageTransactions",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactions_WorkspaceId_CreatedAt",
                table: "UsageTransactions",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageTransactions_WorkspaceId_RequestId_Feature",
                table: "UsageTransactions",
                columns: new[] { "WorkspaceId", "RequestId", "Feature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMembers_UserId",
                table: "WorkspaceMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMembers_WorkspaceId_UserId",
                table: "WorkspaceMembers",
                columns: new[] { "WorkspaceId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_Slug",
                table: "Workspaces",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityReadStates");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AssetRepresentations");

            migrationBuilder.DropTable(
                name: "ChatMessageAttachments");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "GenerationJobOutputs");

            migrationBuilder.DropTable(
                name: "MovieAssemblies");

            migrationBuilder.DropTable(
                name: "MovieCharacters");

            migrationBuilder.DropTable(
                name: "MovieContinuityGuides");

            migrationBuilder.DropTable(
                name: "MovieLocations");

            migrationBuilder.DropTable(
                name: "MovieVideoProviderExecutions");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "PaymentEvents");

            migrationBuilder.DropTable(
                name: "PaymentReconciliationRecords");

            migrationBuilder.DropTable(
                name: "PaymentRefunds");

            migrationBuilder.DropTable(
                name: "PersonalMemories");

            migrationBuilder.DropTable(
                name: "ProviderAttempts");

            migrationBuilder.DropTable(
                name: "ProviderCircuits");

            migrationBuilder.DropTable(
                name: "ProviderCustomerReferences");

            migrationBuilder.DropTable(
                name: "ProviderExecutionFinalizations");

            migrationBuilder.DropTable(
                name: "ResearchEvidence");

            migrationBuilder.DropTable(
                name: "SubscriptionLifecycleEvents");

            migrationBuilder.DropTable(
                name: "WorkspaceMembers");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "ChatMessages");

            migrationBuilder.DropTable(
                name: "MovieClips");

            migrationBuilder.DropTable(
                name: "PaymentAttempts");

            migrationBuilder.DropTable(
                name: "ResearchSources");

            migrationBuilder.DropTable(
                name: "Assets");

            migrationBuilder.DropTable(
                name: "MovieShots");

            migrationBuilder.DropTable(
                name: "CheckoutSessions");

            migrationBuilder.DropTable(
                name: "CreditLedgerEntries");

            migrationBuilder.DropTable(
                name: "StoredFiles");

            migrationBuilder.DropTable(
                name: "MovieScenes");

            migrationBuilder.DropTable(
                name: "CreditEntitlements");

            migrationBuilder.DropTable(
                name: "UsageTransactions");

            migrationBuilder.DropTable(
                name: "MovieProjects");

            migrationBuilder.DropTable(
                name: "BillingPeriods");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "GenerationJobs");

            migrationBuilder.DropTable(
                name: "Subscriptions");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "Plans");

            migrationBuilder.DropTable(
                name: "Workspaces");
        }
    }
}
