using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PackCall.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryStatusJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "delivery_status_journal",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_status = table.Column<int>(type: "integer", nullable: false),
                    new_status = table.Column<int>(type: "integer", nullable: false),
                    worker_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    operator_result = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delivery_status_journal_id", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_status_journal_campaign_occurred_at",
                table: "delivery_status_journal",
                columns: new[] { "campaign_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_status_journal_delivery_occurred_at",
                table: "delivery_status_journal",
                columns: new[] { "delivery_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delivery_status_journal");
        }
    }
}
