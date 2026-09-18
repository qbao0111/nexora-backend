using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class PersistCheckoutActionSnapshot : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CheckoutActionSnapshot",
            table: "orders",
            type: "jsonb",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CheckoutActionSnapshot",
            table: "orders");
    }
}
