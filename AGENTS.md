# Repository Instructions

Подробное описание архитектуры, конфигурации и структуры — в `README.md`. Здесь только то, что нужно для работы с кодом.

## Команды

```bash
dotnet build                          # сборка всех проектов
cd SyncDaemon && dotnet run           # командный демон (запускать первым; один экземпляр)
cd ReportDaemon && dotnet run         # демон отчётов (можно N экземпляров)
cd SyncManager && dotnet run          # веб-панель (отдельный терминал)
dotnet publish -c Release             # публикация
```

## Ключевые факты

- .NET SDK 8.0; тестов нет — валидация по логам и браузеру
- **Два демона**: `SyncDaemon` (команды + синхронизация, **один** экземпляр — гард одиночности через advisory lock) и `ReportDaemon` (отчёты, **N** экземпляров — атомарный claim по `OwnerInstanceId`)
- SyncManager — чистая веб-панель, **DB-driven**: `/api/sync/*` пишет команды в `SyncCommands`, статус/историю читает из БД (`SyncStatus`, `SyncHistory`); HTTP-прокси на демон нет (ключ `Daemon:BaseUrl` — legacy, не читается)
- Провайдеры отчётов обнаруживаются **ReportDaemon** по `IReportProvider` (проект `ReportContracts`) и подключаются одной строкой `<ProjectReference>` в `ReportDaemon.csproj`; маршрутизация заданий — по колонке `ReportTasks.ProviderId` (детали — в README, разделы «Подсистема отчётов» и «Многоэкземплярное развёртывание»)
- **Файлы отчётов хранятся в БД** (`ReportTaskFile`, bytea): провайдер пишет на локальный диск, ReportDaemon загружает байты в БД; SyncManager скачивает из БД (не с диска)
- Настройки синхронизации (`Sync.*`) — только в `SyncDaemon/appsettings.json`; настройки отчётов (`Report.*`) — в `ReportDaemon/appsettings.json`; env: `SYNC__BATCHSIZE=500`, `SYNC__COMMANDPOLLINTERVALSECONDS=1`
- Интервал цикла синхронизации задаётся **payload команды `start`** (дефолт 30 с); ключ `Sync.IntervalSeconds` на цикл не влияет (см. README, раздел «Известные отклонения»)
- SyncManager читает заголовки nginx (`X-Remote-User`, `X-Remote-Group`), но в `ForwardedUserMiddleware` есть **debug-заглушка**: пользователь захардкожен (`"Denis"`), роль `sync-admins` добавляется принудительно — локально дашборд работает без заголовков; заглушку убрать перед продакшеном
- SyncManager обновляется **realtime**: `DbChangeListener` (LISTEN/NOTIFY, каналы `sync_ui_changes`, `report_tasks`) → SignalR-хаб `/hubs/sync` (сигнал `refresh`, scope `"sync"`/`"reports"`); саму данные клиент берёт через REST; резервный polling — 30 с. Десктоп-уведомления — только бизнес-ошибки (`wwwroot/js/notify.js`); детали — README, раздел «Realtime-обновления и уведомления»
- У демонов **нет HTTP API**: SyncDaemon опрашивает `SyncCommands` каждые 2 с, ReportDaemon атомарно claim-ит задания из `ReportTasks`; SyncManager слушает только 127.0.0.1:5000

## Соглашения кода

- Отвечать и комментировать **на русском** 
- Итоги (резюме в конце задачи) — всегда на русском
- PascalCase для типов/методов, `_camelCase` для полей
- Nullable reference types включены
- XML-документация на публичных типах
- Все асинхронные методы принимают `CancellationToken`
