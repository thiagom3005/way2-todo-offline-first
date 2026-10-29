using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Way2.Todo.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrationOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integration_outbox",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    task_version = table.Column<int>(type: "integer", nullable: false),
                    server_revision = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_retry_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    claim_token = table.Column<Guid>(type: "uuid", nullable: true),
                    last_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_status_code = table.Column<int>(type: "integer", nullable: true),
                    last_error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    delivered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_outbox", x => x.event_id);
                    table.CheckConstraint("ck_integration_outbox_event_type", "event_type IN ('task.created', 'task.updated', 'task.deleted')");
                    table.CheckConstraint("ck_integration_outbox_status", "status IN ('pending', 'delivered')");
                    table.ForeignKey(
                        name: "fk_integration_outbox_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_integration_outbox_status_next_retry_at",
                table: "integration_outbox",
                columns: new[] { "status", "next_retry_at" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_outbox_task_id_server_revision",
                table: "integration_outbox",
                columns: new[] { "task_id", "server_revision" },
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_integration_outbox_user_id_server_revision",
                table: "integration_outbox",
                columns: new[] { "user_id", "server_revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integration_outbox");
        }
    }
}
