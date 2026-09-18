using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCore.Migrations
{
    /// <inheritdoc />
    public partial class AddUiNotificationTriggers : Migration
    {
        /// <summary>
        /// Триггеры pg_notify для realtime-обновлений веб-панели (SyncManager, LISTEN/NOTIFY):
        /// канал <c>sync_ui_changes</c> — изменения SyncStatus и новые записи SyncHistory;
        /// расширение функции <c>NotifyReportTask</c> — уведомление при ЛЮБОМ изменении статуса
        /// задания отчёта (UI должен видеть processing/done/error/cancelled, а не только pending).
        /// Влияние на ReportDaemon: слушатель будится чаще (на любой смене статуса), но claim
        /// идемпотентен (условный UPDATE <c>WHERE Status='pending'</c>) — лишние задания не возникают.
        /// </summary>
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Расширяем существующую функцию: уведомлять при ЛЮБОМ изменении статуса задания,
            //    а не только при переходе в pending (UI должен видеть processing/done/error/cancelled).
            // 2) Новый канал для UI: любые изменения SyncStatus и новые записи SyncHistory.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION "NotifyReportTask"() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'INSERT'
                       OR (TG_OP = 'UPDATE' AND OLD."Status" <> NEW."Status") THEN
                        PERFORM pg_notify('report_tasks', NEW."Id"::text);
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE OR REPLACE FUNCTION "NotifySyncUiChanges"() RETURNS trigger AS $$
                BEGIN
                    PERFORM pg_notify('sync_ui_changes', TG_TABLE_NAME);
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS "TRG_SyncStatus_Notify" ON "SyncStatus";
                CREATE TRIGGER "TRG_SyncStatus_Notify" AFTER UPDATE ON "SyncStatus"
                    FOR EACH ROW EXECUTE FUNCTION "NotifySyncUiChanges"();

                DROP TRIGGER IF EXISTS "TRG_SyncHistory_Notify" ON "SyncHistory";
                CREATE TRIGGER "TRG_SyncHistory_Notify" AFTER INSERT ON "SyncHistory"
                    FOR EACH ROW EXECUTE FUNCTION "NotifySyncUiChanges"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Откат: убираем новые триггеры UI и возвращаем функцию NotifyReportTask
            // в исходный вид (уведомление только при переходе статуса в pending).
            // Триггер TRG_ReportTasks_Notify пересоздавать не нужно — он привязан к функции.
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TRG_SyncStatus_Notify" ON "SyncStatus";
                DROP TRIGGER IF EXISTS "TRG_SyncHistory_Notify" ON "SyncHistory";
                DROP FUNCTION IF EXISTS "NotifySyncUiChanges"();

                CREATE OR REPLACE FUNCTION "NotifyReportTask"() RETURNS trigger AS $$
                BEGIN
                    IF (TG_OP = 'INSERT' AND NEW."Status" = 'pending')
                       OR (TG_OP = 'UPDATE' AND OLD."Status" <> 'pending' AND NEW."Status" = 'pending') THEN
                        PERFORM pg_notify('report_tasks', NEW."Id"::text);
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }
    }
}
