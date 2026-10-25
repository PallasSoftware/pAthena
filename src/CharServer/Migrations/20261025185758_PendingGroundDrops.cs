using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CharServer.Migrations
{
    /// <inheritdoc />
    public partial class PendingGroundDrops : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_ground_drop",
                columns: table => new
                {
                    drop_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    char_id = table.Column<long>(type: "bigint", nullable: false),
                    map_id = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    x = table.Column<int>(type: "int", nullable: false),
                    y = table.Column<int>(type: "int", nullable: false),
                    world_epoch = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_row_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_ground_drop", x => x.drop_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pending_ground_drop_char_id",
                table: "pending_ground_drop",
                column: "char_id");

            migrationBuilder.CreateIndex(
                name: "IX_pending_ground_drop_item_row_id",
                table: "pending_ground_drop",
                column: "item_row_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_ground_drop");
        }
    }
}
