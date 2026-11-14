using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebServices.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GroupCommitmentOffers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowCommitmentOffers",
                table: "provider_groups",
                type: "boolean",
                nullable: false,
                defaultValue: true); // existing groups keep listing commitment offers
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowCommitmentOffers",
                table: "provider_groups");
        }
    }
}
