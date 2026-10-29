using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Way2.Todo.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "processed_operations",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_operations", x => new { x.user_id, x.operation_id });
                    table.ForeignKey(
                        name: "fk_processed_operations_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "task_change_log",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_change_log", x => new { x.user_id, x.revision });
                    table.CheckConstraint("ck_task_change_log_change_type", "change_type IN ('upsert', 'delete')");
                    table.ForeignKey(
                        name: "fk_task_change_log_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_sync_counters",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_sync_counters", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_user_sync_counters_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "processed_operations");

            migrationBuilder.DropTable(
                name: "task_change_log");

            migrationBuilder.DropTable(
                name: "user_sync_counters");
        }
    }
}
