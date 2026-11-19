using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hub.Migrations.Postgres.Crosswalk
{
    /// <inheritdoc />
    public partial class InitialCrosswalkSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "crosswalk_entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Supplier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SupplierCityCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SupplierCountryCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CanonicalCityId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Score = table.Column<double>(type: "double precision", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crosswalk_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "crosswalk_reviews",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Supplier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SupplierCityCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SupplierCityName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SupplierCountryCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SupplierRegion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProposedCityId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Score = table.Column<double>(type: "double precision", nullable: false),
                    RunnerUpScore = table.Column<double>(type: "double precision", nullable: false),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CandidatesJson = table.Column<string>(type: "text", nullable: false),
                    ResolvedCityId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    ResolvedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crosswalk_reviews", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_crosswalk_entries_Supplier_CanonicalCityId",
                table: "crosswalk_entries",
                columns: new[] { "Supplier", "CanonicalCityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_crosswalk_entries_Supplier_SupplierCityCode",
                table: "crosswalk_entries",
                columns: new[] { "Supplier", "SupplierCityCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_crosswalk_reviews_Status",
                table: "crosswalk_reviews",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_crosswalk_reviews_Supplier_SupplierCityCode",
                table: "crosswalk_reviews",
                columns: new[] { "Supplier", "SupplierCityCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "crosswalk_entries");

            migrationBuilder.DropTable(
                name: "crosswalk_reviews");
        }
    }
}
