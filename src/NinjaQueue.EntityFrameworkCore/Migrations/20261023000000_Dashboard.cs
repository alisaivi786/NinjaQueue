using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NinjaQueue.EntityFrameworkCore.Migrations;

/// <summary>
/// The dashboard tables: users (the default admin is seeded by the dashboard on first start, never by the migration, so no
/// password is baked into the schema), the tenants a non-admin user may see, and tenant names for the tenant switcher.
/// </summary>
[DbContext(typeof(NinjaQueueDbContext))]
[Migration("20261023000000_Dashboard")]
public sealed class Dashboard : NinjaQueueMigration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        string schema = JobSchemaName.Validate(Schema);

        migrationBuilder.CreateTable(
            name: "dashboard_users",
            schema: schema,
            columns: table => new
            {
                id = table.Column<Guid>(nullable: false),
                user_name = table.Column<string>(maxLength: 100, nullable: false),
                password_hash = table.Column<string>(maxLength: 500, nullable: false),
                is_admin = table.Column<bool>(nullable: false),
                must_change_password = table.Column<bool>(nullable: false),
                failed_logins = table.Column<int>(nullable: false),
                locked_until_utc = table.Column<DateTimeOffset>(nullable: true),
                security_stamp = table.Column<Guid>(nullable: false),
                created_at_utc = table.Column<DateTimeOffset>(nullable: false),
                updated_at_utc = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_dashboard_users", x => x.id));
        migrationBuilder.CreateIndex("ux_dashboard_users_user_name", "dashboard_users", "user_name", schema, unique: true);

        migrationBuilder.CreateTable(
            name: "tenants",
            schema: schema,
            columns: table => new
            {
                tenant_id = table.Column<Guid>(nullable: false),
                name = table.Column<string>(maxLength: 200, nullable: false),
                created_at_utc = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_tenants", x => x.tenant_id));

        migrationBuilder.CreateTable(
            name: "dashboard_user_tenants",
            schema: schema,
            columns: table => new
            {
                user_id = table.Column<Guid>(nullable: false),
                tenant_id = table.Column<Guid>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_dashboard_user_tenants", x => new { x.user_id, x.tenant_id });
                table.ForeignKey("fk_dashboard_user_tenants_user_id", x => x.user_id, "dashboard_users", "id", schema, onDelete: ReferentialAction.Cascade);
                table.ForeignKey("fk_dashboard_user_tenants_tenant_id", x => x.tenant_id, "tenants", "tenant_id", schema, onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("ix_dashboard_user_tenants_tenant_id", "dashboard_user_tenants", "tenant_id", schema);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        string schema = JobSchemaName.Validate(Schema);
        migrationBuilder.DropTable("dashboard_user_tenants", schema);
        migrationBuilder.DropTable("tenants", schema);
        migrationBuilder.DropTable("dashboard_users", schema);
    }
}