﻿using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Taslim.Api.Persistence;

#nullable disable

namespace Taslim.Api.Persistence.Migrations;

[DbContext(typeof(TaslimDbContext))]
[Migration("20260924041926_AddAccountSettingsPreferences")]
/// <inheritdoc />
public partial class AddAccountSettingsPreferences : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DefaultGenerationLanguage",
            table: "AspNetUsers",
            type: "character varying(5)",
            maxLength: 5,
            nullable: false,
            defaultValue: "en");

        migrationBuilder.AddColumn<bool>(
            name: "IncludeSourceLinks",
            table: "AspNetUsers",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<string>(
            name: "OutputPreference",
            table: "AspNetUsers",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "balanced");

        migrationBuilder.AddColumn<string>(
            name: "TimeZone",
            table: "AspNetUsers",
            type: "character varying(100)",
            maxLength: 100,
            nullable: false,
            defaultValue: "UTC");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DefaultGenerationLanguage",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "IncludeSourceLinks",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "OutputPreference",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "TimeZone",
            table: "AspNetUsers");
    }
}
