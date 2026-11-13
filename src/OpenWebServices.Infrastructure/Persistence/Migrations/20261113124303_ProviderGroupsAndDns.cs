using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebServices.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProviderGroupsAndDns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Groups replace the single token per provider. Existing tokens become groups with the same id.
            migrationBuilder.CreateTable(
                name: "provider_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProviderType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EncryptedCredentials = table.Column<string>(type: "text", nullable: false),
                    EnabledResources = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSyncAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_groups", x => x.Id);
                });

            // The old encrypted value is a bare token; the app reads that as the "api_token" credential.
            // Hetzner groups get servers (1) and DNS (2) enabled, other providers servers only.
            migrationBuilder.Sql("""
                INSERT INTO provider_groups ("Id", "Name", "ProviderType", "EncryptedCredentials", "EnabledResources", "CreatedAtUtc", "UpdatedAtUtc")
                SELECT "Id",
                       CASE "ProviderId" WHEN 'hetzner' THEN 'Hetzner' ELSE "ProviderId" END,
                       "ProviderId", "EncryptedApiToken",
                       CASE "ProviderId" WHEN 'hetzner' THEN 3 ELSE 1 END,
                       "UpdatedAtUtc", "UpdatedAtUtc"
                FROM provider_accounts;
                """);

            // Servers whose provider had no stored token keep a group without credentials, so nothing is lost.
            migrationBuilder.Sql("""
                INSERT INTO provider_groups ("Id", "Name", "ProviderType", "EncryptedCredentials", "EnabledResources", "CreatedAtUtc", "UpdatedAtUtc")
                SELECT gen_random_uuid(), p."ProviderId" || ' (no credentials)', p."ProviderId", '', 1, now(), now()
                FROM (SELECT DISTINCT "ProviderId" FROM servers) p
                WHERE NOT EXISTS (SELECT 1 FROM provider_groups g WHERE g."ProviderType" = p."ProviderId");
                """);

            // 2. Servers: string ids (other providers do not use numbers), group, origin, pending state.
            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "servers",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSshKeyId",
                table: "servers",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "servers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "servers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Created");

            migrationBuilder.AddColumn<DateTime>(
                name: "PendingSinceUtc",
                table: "servers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE servers s SET
                    "ExternalId" = s."ProviderServerId"::text,
                    "ExternalSshKeyId" = s."ProviderSshKeyId"::text,
                    "GroupId" = (SELECT g."Id" FROM provider_groups g
                                 WHERE g."ProviderType" = s."ProviderId"
                                 ORDER BY g."CreatedAtUtc" LIMIT 1);
                ALTER TABLE servers ALTER COLUMN "GroupId" SET NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "ProviderId",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "ProviderServerId",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "ProviderSshKeyId",
                table: "servers");

            migrationBuilder.DropTable(
                name: "provider_accounts");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "servers",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(63)",
                oldMaxLength: 63);

            migrationBuilder.AlterColumn<string>(
                name: "Location",
                table: "servers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "Image",
                table: "servers",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "EncryptedSshPrivateKey",
                table: "servers",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            // 3. DNS zones.
            migrationBuilder.CreateTable(
                name: "dns_zones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Ttl = table.Column<int>(type: "integer", nullable: true),
                    RecordCount = table.Column<int>(type: "integer", nullable: true),
                    Nameservers = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    DelegationStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dns_zones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dns_zones_provider_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "provider_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_servers_GroupId_ExternalId",
                table: "servers",
                columns: new[] { "GroupId", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_dns_zones_GroupId_ExternalId",
                table: "dns_zones",
                columns: new[] { "GroupId", "ExternalId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_servers_provider_groups_GroupId",
                table: "servers",
                column: "GroupId",
                principalTable: "provider_groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_servers_provider_groups_GroupId",
                table: "servers");

            migrationBuilder.DropTable(
                name: "dns_zones");

            migrationBuilder.DropTable(
                name: "provider_groups");

            migrationBuilder.DropIndex(
                name: "IX_servers_GroupId_ExternalId",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "ExternalSshKeyId",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "PendingSinceUtc",
                table: "servers");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "servers",
                type: "character varying(63)",
                maxLength: 63,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255);

            migrationBuilder.AlterColumn<string>(
                name: "Location",
                table: "servers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "Image",
                table: "servers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "EncryptedSshPrivateKey",
                table: "servers",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderId",
                table: "servers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "ProviderServerId",
                table: "servers",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProviderSshKeyId",
                table: "servers",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "provider_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EncryptedApiToken = table.Column<string>(type: "text", nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_accounts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provider_accounts_ProviderId",
                table: "provider_accounts",
                column: "ProviderId",
                unique: true);
        }
    }
}
