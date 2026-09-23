using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Taslim.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionsAndCreditsFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                name: "IX_BillingPeriods_Status_EndsAt",
                table: "BillingPeriods",
                columns: new[] { "Status", "EndsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingPeriods_SubscriptionId_StartsAt",
                table: "BillingPeriods",
                columns: new[] { "SubscriptionId", "StartsAt" },
                unique: true);

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
                name: "IX_Plans_Code",
                table: "Plans",
                column: "Code",
                unique: true);

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreditLedgerEntries");

            migrationBuilder.DropTable(
                name: "CreditEntitlements");

            migrationBuilder.DropTable(
                name: "BillingPeriods");

            migrationBuilder.DropTable(
                name: "Subscriptions");

            migrationBuilder.DropTable(
                name: "Plans");
        }
    }
}
