using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebServices.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SshKeyAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AccessCheckedAtUtc",
                table: "servers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccessError",
                table: "servers",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AutomationKeyCreatedAtUtc",
                table: "servers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutomationPublicKey",
                table: "servers",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostKeyAlgorithm",
                table: "servers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostKeyFingerprint",
                table: "servers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetiredAutomationPublicKey",
                table: "servers",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnknownKeysText",
                table: "servers",
                type: "text",
                nullable: true);

            // Existing ows keys were created with their server. Their public halves are filled in at startup.
            migrationBuilder.Sql(
                "UPDATE servers SET \"AutomationKeyCreatedAtUtc\" = \"CreatedAtUtc\" WHERE \"EncryptedSshPrivateKey\" IS NOT NULL;");

            migrationBuilder.CreateTable(
                name: "ssh_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PublicKey = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Algorithm = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Origin = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ssh_keys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "server_key_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SshKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    GrantedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_server_key_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_server_key_grants_servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_server_key_grants_ssh_keys_SshKeyId",
                        column: x => x.SshKeyId,
                        principalTable: "ssh_keys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_server_key_grants_ServerId_SshKeyId",
                table: "server_key_grants",
                columns: new[] { "ServerId", "SshKeyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_server_key_grants_SshKeyId",
                table: "server_key_grants",
                column: "SshKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_server_key_grants_TenantId",
                table: "server_key_grants",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ssh_keys_TenantId",
                table: "ssh_keys",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ssh_keys_TenantId_Fingerprint",
                table: "ssh_keys",
                columns: new[] { "TenantId", "Fingerprint" },
                unique: true,
                filter: "\"RevokedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ssh_keys_TenantId_OwnerUserId",
                table: "ssh_keys",
                columns: new[] { "TenantId", "OwnerUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "server_key_grants");

            migrationBuilder.DropTable(
                name: "ssh_keys");

            migrationBuilder.DropColumn(
                name: "AccessCheckedAtUtc",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "AccessError",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "AutomationKeyCreatedAtUtc",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "AutomationPublicKey",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "HostKeyAlgorithm",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "HostKeyFingerprint",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "RetiredAutomationPublicKey",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "UnknownKeysText",
                table: "servers");
        }
    }
}
