using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PackCall.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryCaptureIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_deliveries_status_id",
                table: "deliveries",
                columns: new[] { "delivery_status", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_deliveries_status_id",
                table: "deliveries");
        }
    }
}
