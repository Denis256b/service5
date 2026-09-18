using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCore.Migrations
{
    /// <inheritdoc />
    public partial class AddReportTaskTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Функция шлёт pg_notify('report_tasks', Id) при вставке задания
            // и при переходе статуса в pending — ReportDaemon (LISTEN/NOTIFY)
            // мгновенно будит опросный цикл, не дожидаясь следующего витка опроса.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION "NotifyReportTask"() RETURNS trigger AS $$
                BEGIN
                    IF (TG_OP = 'INSERT' AND NEW."Status" = 'pending')
                       OR (TG_OP = 'UPDATE' AND OLD."Status" <> 'pending' AND NEW."Status" = 'pending') THEN
                        PERFORM pg_notify('report_tasks', NEW."Id"::text);
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS "TRG_ReportTasks_Notify" ON "ReportTasks";
                CREATE TRIGGER "TRG_ReportTasks_Notify"
                AFTER INSERT OR UPDATE ON "ReportTasks"
                FOR EACH ROW
                EXECUTE FUNCTION "NotifyReportTask"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TRG_ReportTasks_Notify" ON "ReportTasks";
                DROP FUNCTION IF EXISTS "NotifyReportTask"();
                """);
        }
    }
}
