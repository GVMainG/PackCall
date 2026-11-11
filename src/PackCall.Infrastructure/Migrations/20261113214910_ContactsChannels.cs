using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PackCall.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ContactsChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_deliveries_recipients",
                table: "deliveries");

            migrationBuilder.Sql("""
                LOCK TABLE recipients IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM recipients WHERE email IS NULL OR email ~ '^[[:space:]]*$') THEN
                    RAISE EXCEPTION 'Contact migration rejected: empty address';
                  END IF;
                  IF EXISTS (
                    SELECT 1 FROM recipients
                    GROUP BY lower(regexp_replace(email, '^[[:space:]]+|[[:space:]]+$', '', 'g') COLLATE pg_catalog.pg_c_utf8)
                    HAVING count(*) > 1
                  ) THEN
                    RAISE EXCEPTION 'Contact migration rejected: normalized address collision';
                  END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "ix_deliveries_campaign_recipient",
                table: "deliveries");

            migrationBuilder.RenameColumn(name: "recipient_email", table: "deliveries", newName: "address_snapshot");
            migrationBuilder.Sql("""
                ALTER TABLE deliveries ALTER COLUMN address_snapshot DROP NOT NULL;
                UPDATE deliveries
                SET address_snapshot = NULLIF(lower(regexp_replace(address_snapshot, '^[[:space:]]+|[[:space:]]+$', '', 'g') COLLATE pg_catalog.pg_c_utf8), '')
                WHERE address_snapshot IS DISTINCT FROM
                    NULLIF(lower(regexp_replace(address_snapshot, '^[[:space:]]+|[[:space:]]+$', '', 'g') COLLATE pg_catalog.pg_c_utf8), '');
                """);

            migrationBuilder.RenameColumn(
                name: "recipient_id",
                table: "deliveries",
                newName: "contact_id");

            migrationBuilder.RenameIndex(
                name: "IX_deliveries_recipient_id",
                table: "deliveries",
                newName: "IX_deliveries_contact_id");

            migrationBuilder.AddColumn<string>(
                name: "channel",
                table: "deliveries",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "email");

            migrationBuilder.CreateTable(
                name: "campaign_channels",
                columns: table => new
                {
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaign_channels", x => new { x.campaign_id, x.channel });
                    table.ForeignKey(
                        name: "FK_campaign_channels_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "contacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contacts_id", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "campaign_contacts",
                columns: table => new
                {
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaign_contacts", x => new { x.campaign_id, x.contact_id });
                    table.ForeignKey(
                        name: "FK_campaign_contacts_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_campaign_contacts_contacts_contact_id",
                        column: x => x.contact_id,
                        principalTable: "contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "contact_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    address = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contact_channels", x => x.id);
                    table.ForeignKey(
                        name: "FK_contact_channels_contacts_contact_id",
                        column: x => x.contact_id,
                        principalTable: "contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO contacts (id, display_name, created_at, updated_at)
                    SELECT id, NULL, created_at, updated_at FROM recipients;
                INSERT INTO contact_channels (id, contact_id, channel, address)
                    SELECT id, id, 'email', lower(regexp_replace(email, '^[[:space:]]+|[[:space:]]+$', '', 'g') COLLATE pg_catalog.pg_c_utf8)
                    FROM recipients;
                INSERT INTO campaign_contacts (campaign_id, contact_id)
                    SELECT DISTINCT campaign_id, contact_id FROM deliveries;
                INSERT INTO campaign_channels (campaign_id, channel)
                    SELECT DISTINCT campaign_id, channel FROM deliveries;
                DROP TABLE recipients;
                ALTER TABLE deliveries ALTER COLUMN channel DROP DEFAULT;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_campaign_contact_channel",
                table: "deliveries",
                columns: new[] { "campaign_id", "contact_id", "channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_campaign_id_channel",
                table: "deliveries",
                columns: new[] { "campaign_id", "channel" });

            migrationBuilder.CreateIndex(
                name: "IX_campaign_contacts_contact_id",
                table: "campaign_contacts",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "ix_contact_channels_channel_address",
                table: "contact_channels",
                columns: new[] { "channel", "address" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_contact_channels_contact_id_channel",
                table: "contact_channels",
                columns: new[] { "contact_id", "channel" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_deliveries_campaign_channels_campaign_id_channel",
                table: "deliveries",
                columns: new[] { "campaign_id", "channel" },
                principalTable: "campaign_channels",
                principalColumns: new[] { "campaign_id", "channel" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_deliveries_campaign_contacts_campaign_id_contact_id",
                table: "deliveries",
                columns: new[] { "campaign_id", "contact_id" },
                principalTable: "campaign_contacts",
                principalColumns: new[] { "campaign_id", "contact_id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_deliveries_contacts",
                table: "deliveries",
                column: "contact_id",
                principalTable: "contacts",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Restore the verified pre-migration backup and deploy beta.2; see docs/evidence/beta3/migration-candidate.md.");
        }
    }
}
