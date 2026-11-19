using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hub.Migrations.Postgres.Booking
{
    /// <inheritdoc />
    public partial class InitialBookingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bookings",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Tenant = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    QuoteId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OfferId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Supplier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SupplierHotelCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SupplierReference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SupplierBookingRef = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SellingAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SellingCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    SupplierAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SupplierCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    LeadGuestName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PaymentMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PaidAt = table.Column<long>(type: "bigint", nullable: true),
                    FreeCancellationUntil = table.Column<long>(type: "bigint", nullable: true),
                    PayBy = table.Column<long>(type: "bigint", nullable: true),
                    CancelStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PenaltyAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    RefundAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    CancelledAt = table.Column<long>(type: "bigint", nullable: true),
                    BookAttempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    OfferJson = table.Column<string>(type: "text", nullable: false),
                    SearchJson = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bookings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "quotes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Tenant = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OfferId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Supplier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SellingAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SellingCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    SupplierAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SupplierCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<long>(type: "bigint", nullable: false),
                    Refundable = table.Column<bool>(type: "boolean", nullable: false),
                    PolicyKnown = table.Column<bool>(type: "boolean", nullable: false),
                    FreeCancellationUntil = table.Column<long>(type: "bigint", nullable: true),
                    OfferJson = table.Column<string>(type: "text", nullable: false),
                    SearchJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bookings_PaymentMode_PayBy",
                table: "bookings",
                columns: new[] { "PaymentMode", "PayBy" });

            migrationBuilder.CreateIndex(
                name: "IX_bookings_Status_UpdatedAt",
                table: "bookings",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_bookings_Tenant_IdempotencyKey",
                table: "bookings",
                columns: new[] { "Tenant", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quotes_Tenant_OfferId_Status",
                table: "quotes",
                columns: new[] { "Tenant", "OfferId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bookings");

            migrationBuilder.DropTable(
                name: "quotes");
        }
    }
}
