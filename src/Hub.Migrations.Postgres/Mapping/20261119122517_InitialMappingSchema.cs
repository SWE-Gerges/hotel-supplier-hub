using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hub.Migrations.Postgres.Mapping
{
    /// <inheritdoc />
    public partial class InitialMappingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "canonical_hotels",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_hotels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "hotel_aliases",
                columns: table => new
                {
                    RetiredId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SurvivorId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hotel_aliases", x => x.RetiredId);
                });

            migrationBuilder.CreateTable(
                name: "hotel_identities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CanonicalId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Supplier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SupplierCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hotel_identities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_hotel_aliases_SurvivorId",
                table: "hotel_aliases",
                column: "SurvivorId");

            migrationBuilder.CreateIndex(
                name: "IX_hotel_identities_CanonicalId",
                table: "hotel_identities",
                column: "CanonicalId");

            migrationBuilder.CreateIndex(
                name: "IX_hotel_identities_Supplier_SupplierCode",
                table: "hotel_identities",
                columns: new[] { "Supplier", "SupplierCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "canonical_hotels");

            migrationBuilder.DropTable(
                name: "hotel_aliases");

            migrationBuilder.DropTable(
                name: "hotel_identities");
        }
    }
}
