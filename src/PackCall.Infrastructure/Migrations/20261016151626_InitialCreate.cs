using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PackCall.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    message_text = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaigns_id", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mock_email_sends",
                columns: table => new
                {
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    message_text = table.Column<string>(type: "text", nullable: false),
                    is_success = table.Column<bool>(type: "boolean", nullable: false),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mock_email_sends_delivery_id", x => x.delivery_id);
                });

            migrationBuilder.CreateTable(
                name: "recipients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipients_id", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    delivery_status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    worker_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    lock_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deliveries_id", x => x.id);
                    table.ForeignKey(
                        name: "fk_deliveries_campaigns",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_deliveries_recipients",
                        column: x => x.recipient_id,
                        principalTable: "recipients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_campaigns_created_at",
                table: "campaigns",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_campaigns_status",
                table: "campaigns",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_campaign_recipient",
                table: "deliveries",
                columns: new[] { "campaign_id", "recipient_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_created_at",
                table: "deliveries",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_recipient_id",
                table: "deliveries",
                column: "recipient_id");

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_status",
                table: "deliveries",
                column: "delivery_status");

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_status_lock_expires_at",
                table: "deliveries",
                columns: new[] { "delivery_status", "lock_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_mock_email_sends_created_at",
                table: "mock_email_sends",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_recipients_email",
                table: "recipients",
                column: "email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deliveries");

            migrationBuilder.DropTable(
                name: "mock_email_sends");

            migrationBuilder.DropTable(
                name: "campaigns");

            migrationBuilder.DropTable(
                name: "recipients");
        }
    }
}
