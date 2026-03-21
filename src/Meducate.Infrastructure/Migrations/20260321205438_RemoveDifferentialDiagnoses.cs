using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meducate.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDifferentialDiagnoses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DifferentialDiagnoses",
                table: "HealthTopics");

            migrationBuilder.Sql(
                "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20260317143347_AddDifferentialDiagnoses';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DifferentialDiagnoses",
                table: "HealthTopics",
                type: "jsonb",
                nullable: true);
        }
    }
}
