using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CharServer.Migrations
{
    /// <inheritdoc />
    public partial class InventoryOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "revision",
                table: "inventory",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "container_item",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    container = table.Column<byte>(type: "tinyint", nullable: false),
                    owner_id = table.Column<long>(type: "bigint", nullable: false),
                    nameid = table.Column<long>(type: "bigint", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    equip = table.Column<long>(type: "bigint", nullable: false),
                    identify = table.Column<short>(type: "smallint", nullable: false),
                    refine = table.Column<byte>(type: "tinyint", nullable: false),
                    attribute = table.Column<byte>(type: "tinyint", nullable: false),
                    card0 = table.Column<long>(type: "bigint", nullable: false),
                    card1 = table.Column<long>(type: "bigint", nullable: false),
                    card2 = table.Column<long>(type: "bigint", nullable: false),
                    card3 = table.Column<long>(type: "bigint", nullable: false),
                    option_id0 = table.Column<short>(type: "smallint", nullable: false),
                    option_val0 = table.Column<short>(type: "smallint", nullable: false),
                    option_parm0 = table.Column<byte>(type: "tinyint", nullable: false),
                    option_id1 = table.Column<short>(type: "smallint", nullable: false),
                    option_val1 = table.Column<short>(type: "smallint", nullable: false),
                    option_parm1 = table.Column<byte>(type: "tinyint", nullable: false),
                    option_id2 = table.Column<short>(type: "smallint", nullable: false),
                    option_val2 = table.Column<short>(type: "smallint", nullable: false),
                    option_parm2 = table.Column<byte>(type: "tinyint", nullable: false),
                    option_id3 = table.Column<short>(type: "smallint", nullable: false),
                    option_val3 = table.Column<short>(type: "smallint", nullable: false),
                    option_parm3 = table.Column<byte>(type: "tinyint", nullable: false),
                    option_id4 = table.Column<short>(type: "smallint", nullable: false),
                    option_val4 = table.Column<short>(type: "smallint", nullable: false),
                    option_parm4 = table.Column<byte>(type: "tinyint", nullable: false),
                    expire_time = table.Column<long>(type: "bigint", nullable: false),
                    favorite = table.Column<byte>(type: "tinyint", nullable: false),
                    bound = table.Column<byte>(type: "tinyint", nullable: false),
                    unique_id = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    equip_switch = table.Column<long>(type: "bigint", nullable: false),
                    enchantgrade = table.Column<byte>(type: "tinyint", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_container_item", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inventory_operation",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    char_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<byte>(type: "tinyint", nullable: false),
                    payload_hash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    response_bytes = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_operation", x => x.operation_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_container_item_container_owner_id",
                table: "container_item",
                columns: new[] { "container", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_operation_char_id_created_at",
                table: "inventory_operation",
                columns: new[] { "char_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "container_item");

            migrationBuilder.DropTable(
                name: "inventory_operation");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "inventory");
        }
    }
}
