using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Taslim.Api.Persistence;

#nullable disable

namespace Taslim.Api.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(TaslimDbContext))]
[Migration("20260929213000_AddProviderCapabilityPricing")]
public partial class AddProviderCapabilityPricing : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ProviderCapabilityPricings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CapabilityKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                ProviderKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                ModelKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                SourceResolution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                TargetResolution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                QualityTier = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                ProcessingPath = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                SupportsUpscaling = table.Column<bool>(type: "boolean", nullable: false),
                MaxDurationSeconds = table.Column<int>(type: "integer", nullable: false),
                MaxRetryAttempts = table.Column<int>(type: "integer", nullable: false),
                MaxUpscalePasses = table.Column<int>(type: "integer", nullable: false),
                BasePriceUsdPerSecondMin = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                BasePriceUsdPerSecondMax = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                BasePriceUsdFixedMin = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                BasePriceUsdFixedMax = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                UpscalePriceUsdPerSecondMin = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                UpscalePriceUsdPerSecondMax = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                UpscalePriceUsdFixedMin = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                UpscalePriceUsdFixedMax = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                PricingVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                EffectiveAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Source = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProviderCapabilityPricings", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ProviderCapabilityPricings_CapabilityKey_IsActive_ProviderKey_ModelKey",
            table: "ProviderCapabilityPricings",
            columns: new[] { "CapabilityKey", "IsActive", "ProviderKey", "ModelKey" });

        migrationBuilder.CreateIndex(
            name: "IX_ProviderCapabilityPricings_SourceResolution_TargetResolution_QualityTier_ProcessingPath",
            table: "ProviderCapabilityPricings",
            columns: new[] { "SourceResolution", "TargetResolution", "QualityTier", "ProcessingPath" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ProviderCapabilityPricings");
    }
}
