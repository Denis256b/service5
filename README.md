# SyncDaemon + ReportDaemon + SyncManager

## 1. Обзор проекта

Репозиторий содержит **шесть связанных проектов**:

| Проект | Назначение |
|--------|-----------|
| **SyncCore** | Сущности БД + `SyncBusContext` + миграции (`SyncCommand`, `SyncStatus`, `SyncHistoryEntry`, `ReportTask`, `ReportTaskEvent`, `ReportTaskFile`) |
| **SyncDaemon** | Движок синхронизации (`ISourceDb`/`IDestinationDb`/`SyncEngine`/`SyncService`), `CommandPoller`, `SingleInstanceGuard`, `StubDbSource` (Worker Service, net8.0). Развёртывается **в одном экземпляре** (гард одиночности) |
| **ReportDaemon** | Демон отчётов: `ReportPoller` + `ReportNotificationListener` (LISTEN/NOTIFY) + обнаружение провайдеров (Worker Service, net8.0). Масштабируется до **N экземпляров** (атомарный claim заданий) |
| **SyncManager** | Веб-панель мониторинга и управления (ASP.NET Core MVC, net8.0) — DB-driven, скачивает отчёты из БД |
| **ReportContracts** | Контракт подсистемы отчётов: `IReportProvider` + DTO, без внешних зависимостей |
| **SampleProvider** | Эталонный провайдер отчётов (stub-данные, файл .xls) — образец для подключения реальных ИС |

**SyncDaemon** — ядро синхронизации: периодическое копирование изменений между двумя базами данных (source → destination) + обработка команд (опрос таблицы `SyncCommands` через `CommandPoller`). Развёртывается **в одном экземпляре**: при старте занимает advisory lock в Postgres, второй запуск завершается с кодом 1.

**ReportDaemon** — подсистема отчётов: запуск формирования событийный (триггер БД `TRG_ReportTasks_Notify` + `LISTEN/NOTIFY`, `ReportNotificationListener`) с резервным периодическим опросом (`ReportPoller`); задания берутся атомарным claim и маршрутизируются в провайдеров по `ProviderId`. Масштабируется до N экземпляров; каждое задание обрабатывает ровно один экземпляр. Файлы отчётов сохраняются в БД (`ReportTaskFile`), поэтому скачиваются с любой машины.

**SyncManager** — чистая веб-панель: дашборд, график активности, история, кнопки Start/Stop/Execute, раздел отчётов. Самой синхронизации **не выполняет** — команды пишутся в таблицу `SyncCommands` (Postgres, `ConnectionStrings:SyncBus`), их опрашивает `CommandPoller` демона; статус и история читаются из БД. Данные обновляются **в момент изменения в БД**: `DbChangeListener` (LISTEN/NOTIFY) пушит сигнал `refresh` через SignalR-хаб, резервный polling — каждые 30 с (см. раздел «Realtime-обновления и уведомления»). Аутентификация (nginx + заголовки `X-Remote-User` / `X-Remote-Group`) остаётся на веб-приложении.

**Ключевые технологии:**
- .NET 8.0 (все проекты)
- ASP.NET Core MVC + Chart.js + DataTables + jQuery + Bootstrap
- `Microsoft.Extensions.Hosting` 8.0.0 (DI, конфигурация, логирование)
- Kestrel: SyncManager на `127.0.0.1:5000` (доступен только через localhost); у демонов HTTP API нет
- Postgres — шина команд (`SyncCommands`) и хранилище статусов/истории/отчётов (`ConnectionStrings:SyncBus`)
- Realtime-обновления: **SignalR** (WebSocket `/hubs/sync`) + PostgreSQL **LISTEN/NOTIFY** (`DbChangeListener` в SyncManager); резервный polling — каждые 30 с
- Десктоп-уведомления браузера об ошибках (Web Notification API, только бизнес-ошибки)
- ForwardedHeaders + Middleware для аутентификации из заголовков nginx

**Высокоуровневая архитектура:**

```
┌─────────────────────────────────────────────────────────────────┐
│  nginx (TLS, LDAP/AD auth)                                      │
│  → X-Remote-User, X-Remote-Group, X-Forwarded-*                │
└──────────────────────────────┬──────────────────────────────────┘
                               │ 127.0.0.1:5000
┌──────────────────────────────▼──────────────────────────────────┐
│  SyncManager (ASP.NET Core MVC) — только веб-панель             │
│                                                                │
│  ForwardedUserMiddleware → ClaimsPrincipal                     │
│  SyncController (Dashboard + API /api/sync/*)                  │
│  ReportsController (/reports + api/reports/*)                  │
│  SyncHub (SignalR, WebSocket /hubs/sync)                       │
│  DbChangeListener (LISTEN/NOTIFY → сигнал "refresh")           │
│  Chart.js + DataTables (Grid) + jQuery (polling 30с — резерв)  │
└──────────────────────────────┬──────────────────────────────────┘
                               │ EF Core (Npgsql, ConnectionStrings:SyncBus)
                               │ пишет в SyncCommands; читает SyncStatus,
                               │ SyncHistory, ReportTasks, ReportTaskFile
                               │ DbChangeListener: LISTEN sync_ui_changes + report_tasks
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│        Postgres «syncbus» — шина команд и хранилище отчётов      │
└──────────┬──────────────────────────────────────┬───────────────┘
            │ CommandPoller опрашивает каждые 2 с   │ LISTEN/NOTIFY + атомарный
            ▼                                       ▼ claim (≤3 параллельно)
┌──────────────────────────────────────┐  ┌──────────────────────────────────────┐
│  SyncDaemon (Worker Service, без    │  │  ReportDaemon (N экземпляров, без   │
│  HTTP; один экземпляр)              │  │  HTTP)                              │
│                                     │  │                                      │
│  SingleInstanceGuard (advisory lock)│  │  InstanceIdProvider (уникальный ID) │
│  CommandPoller (start/stop/execute) │  │  ReportPoller (claim, stale-reset)  │
│                                     │  │  ReportNotificationListener (LISTEN)│
│      └── SyncService (цикл;         │  │      └── IReportProvider            │
│          запускается командой или   │  │              (SampleProvider)        │
│          восстановлением из БД)     │  │              └── файл на диск,       │
│              └── SyncEngine         │  │                  байты в ReportTaskFile│
│                      ├── ISourceDb  │  └──────────────────────────────────────┘
│                      └── IDestinationDb
│                          (StubDbSource)
└──────────────────────────────────────┘

Оба демона и SyncManager используют SyncCore: сущности + SyncBusContext + миграции.
```

Интервал цикла синхронизации задаётся payload команды `start` (дефолт 30 с); ключ `Sync.IntervalSeconds` в конфиге на цикл не влияет (см. раздел «Известные отклонения»). Изменения читаются пакетами по `BatchSize` (по умолчанию 100). Настройки синхронизации живут **только в демоне** (`SyncDaemon/appsettings.json`).

## 2. Начало работы

### Требования
- .NET SDK **8.0** (обязательно)
- Linux / macOS / Windows, git
- nginx (для production, не требуется для локальной разработки)

### Сборка и запуск
```bash
# Сборка всего решения (все 6 проектов)
dotnet build

# 1. SyncDaemon (командный воркер, запускать первым; один экземпляр — гард одиночности)
cd SyncDaemon
dotnet run
# HTTP API нет: опрашивает таблицу SyncCommands каждые 2 с

# 2. ReportDaemon (демон отчётов; можно N экземпляров, каждый в своём терминале)
cd ../ReportDaemon
dotnet run

# 3. SyncManager (веб-интерфейс, отдельный терминал)
cd ../SyncManager
dotnet run
# → http://localhost:5000
```

### Тестирование без nginx

> **Debug-заглушка:** в `ForwardedUserMiddleware` пользователь захардкожен (`"Denis"`), а роль `sync-admins` добавляется принудительно — поэтому локально дашборд и кнопки Start/Stop работают **без заголовков nginx**. Заглушка помечена комментарием «Заглушка для локальной отладки» и должна быть убрана перед продакшеном (см. раздел «Известные отклонения»).

```bash
# Дашборд (заглушка подставляет пользователя и роль sync-admins):
curl http://localhost:5000/

# API веб-панели (DB-driven: команды пишутся в SyncCommands, статус/история читаются из БД):
curl http://localhost:5000/api/sync/status
curl -X POST http://localhost:5000/api/sync/start
curl -X POST http://localhost:5000/api/sync/stop
curl -X POST http://localhost:5000/api/sync/execute
curl http://localhost:5000/api/sync/history

# Раздел отчётов:
curl http://localhost:5000/reports
curl http://localhost:5000/api/reports/tasks
curl -X POST http://localhost:5000/api/reports/tasks -H "Content-Type: application/json" \
     -d '{"reportType":"sample","providerId":"sample","payload":"{}"}'
curl http://localhost:5000/api/reports/summary
```

### Конфигурация

**SyncDaemon** — `SyncDaemon/appsettings.json` (все настройки синхронизации):

| Секция | Ключ | Описание | По умолчанию |
|--------|------|----------|--------------|
| `Sync` | `IntervalSeconds` | ⚠️ на цикл не влияет (интервал задаётся payload команды `start`) | 30 |
| `Sync` | `BatchSize` | Размер пакета при чтении источника | 100 |
| `Sync` | `CommandPollIntervalSeconds` | Интервал опроса таблицы `SyncCommands`, с | 2 |
| `Sync` | `ProcessingTimeoutMinutes` | Тайм-аут обработки команды (зависшие сбрасываются в `pending`) | 5 |
| `Sync` | `Source.ConnectionString` | Строка подключения источника | — |
| `Sync` | `Source.TableName` | Таблица источника | — |
| `Sync` | `Source.PrimaryKey` | PK источника | — |
| `Sync` | `Destination.*` | Аналогично для назначения | — |

Переопределение через переменные окружения (для демона): `SYNC__BATCHSIZE=500`, `SYNC__COMMANDPOLLINTERVALSECONDS=1`.

**ReportDaemon** — `ReportDaemon/appsettings.json`:

| Секция | Ключ | Описание | По умолчанию |
|--------|------|----------|--------------|
| `Report` | `PollIntervalSeconds` | Интервал опроса `ReportTasks`, с | 5 |
| `Report` | `ProcessingTimeoutMinutes` | Тайм-аут задания (stale-reset) | 10 |
| `Report` | `MaxParallelReports` | Максимум параллельных заданий на экземпляр | 3 |

**SyncManager** — `SyncManager/appsettings.json`:

| Секция | Ключ | Описание | По умолчанию |
|--------|------|----------|--------------|
| `Proxy` | `UserHeader` | Заголовок с именем пользователя (используется middleware в продакшене) | `X-Remote-User` |
| `Proxy` | `GroupHeader` | Заголовок с группами (используется middleware) | `X-Remote-Group` |
| `Proxy` | `GroupSeparator` | Разделитель групп | `;` |
| `Report` | `Providers` | Статический список провайдеров для формы создания задания (`api/reports/providers`) | — |

Общий для всех трёх проектов: `ConnectionStrings:SyncBus` — одна и та же Postgres. Переопределение через переменные окружения: `ConnectionStrings__SyncBus=...`.

#### Секреты и публикация

Коммитимые `appsettings.json` (SyncDaemon, ReportDaemon, SyncManager) содержат **плейсхолдеры** в строке подключения `ConnectionStrings:SyncBus` (`Username=CHANGE_ME;Password=CHANGE_ME`) — реальные учётные данные БД в git не попадают. Реальная строка задаётся одним из способов:

- **переменная окружения** `ConnectionStrings__SyncBus="Host=...;Database=syncbus;Username=...;Password=..."` — все три проекта читают env поверх appsettings.json (механизм уже поддерживается);
- **локальный gitignored-файл** с реальной строкой: `.env` (готовый пример в корне) или `appsettings.<env>.local.json` — оба типа добавлены в `.gitignore`.

Для design-time инструментов (`dotnet ef ...`) фабрика `SyncCore/DesignTimeDbContextFactory.cs` читает ту же переменную окружения `ConnectionStrings__SyncBus`; без неё подставляется безопасная заглушка без пароля. Перед первым `commit`+`push` убедитесь, что в отслеживаемых файлах нет реальных учётных данных БД: строки подключения должны содержать только плейсхолдер `CHANGE_ME`, а не настоящий пароль (реальные значения живут лишь в gitignored-файле `.env`).

### Тесты
**Проекты тестов в репозитории нет.** Валидация — по логам при `dotnet run` и через браузер.

## 3. Структура проекта

```
.
├── service5.sln                  # Решение (6 проектов)
│
├── SyncCore/                     # Общая библиотека: сущности + контекст + миграции
│   ├── SyncCore.csproj           # Ссылки: EF Core, Npgsql
│   ├── SyncBusContext.cs         # DbContext базы «syncbus»
│   ├── DesignTimeDbContextFactory.cs  # Для dotnet ef tools
│   ├── SyncCommand.cs            # Команда (start/stop/execute) — шина
│   ├── SyncStatus.cs             # Статус для дашборда
│   ├── SyncHistoryEntry.cs       # Запись истории
│   ├── ReportTask.cs             # Задание отчёта (+ OwnerInstanceId)
│   ├── ReportTaskEvent.cs        # Журнал событий задания
│   ├── ReportTaskFile.cs         # Файл отчёта (bytea)
│   └── Migrations/               # Миграции EF Core
│
├── SyncDaemon/                   # Командный демон: worker, без HTTP (один экземпляр)
│   ├── SyncDaemon.csproj         # Ссылки: SyncCore
│   ├── Program.cs                # DI, гард одиночности, CommandPoller; Kestrel нет
│   ├── SingleInstanceGuard.cs    # Advisory lock (pg_try_advisory_lock) — защита от двойного запуска
│   ├── CommandPoller.cs          # BackgroundService: опрос SyncCommands каждые 2 с
│   ├── ISourceDb.cs / IDestinationDb.cs / ISyncEngine.cs / ISyncService.cs
│   ├── SyncEngine.cs             # Чтение, классификация, применение
│   ├── SyncService.cs            # Start/Stop/Execute/Status/History (цикл)
│   ├── StubDbSource.cs           # In-memory заглушка (оба интерфейса)
│   ├── SyncRecord.cs / SyncResult.cs  # Единица синхронизации, результат итерации
│   ├── SyncSettings.cs           # Настройки (Source/Destination/Interval/Batch/опрос)
│   └── appsettings.json          # ConnectionStrings + секция Sync
│
├── ReportDaemon/                 # Демон отчётов: ReportPoller + провайдеры (N экземпляров)
│   ├── ReportDaemon.csproj       # Ссылки: SyncCore, ReportContracts, SampleProvider
│   ├── Program.cs                # DI, явная регистрация провайдеров, MigrateAsync при старте
│   ├── InstanceIdProvider.cs     # Уникальный ID экземпляра (для OwnerInstanceId / stale-reset)
│   ├── ReportPoller.cs           # BackgroundService: атомарный claim, маршрутизация по ProviderId
│   ├── ReportNotificationListener.cs  # BackgroundService: LISTEN/NOTIFY (report_tasks), будит опрос
│   ├── ReportTaskSignal.cs       # Идемпотентный сигнал мгновенного пробуждения опросного цикла
│   ├── ReportSettings.cs         # Настройки отчётов (интервалы, параллельность)
│   └── appsettings.json          # ConnectionStrings + секция Report
│
├── ReportContracts/              # Контракт отчётов: IReportProvider + DTO (без зависимостей)
│   ├── ReportContracts.csproj
│   └── IReportProvider.cs        # IReportProvider, ReportRequest, GeneratedReportResult
│
├── SampleProvider/               # Эталонный провайдер отчётов (stub-данные, файл .xls)
│   ├── SampleProvider.csproj     # Ссылки: ReportContracts + Logging.Abstractions
│   └── SampleReportProvider.cs   # ProviderId = "sample", генерация .xls со stub-данными
│
└── SyncManager/                  # Веб-панель: мониторинг + управление (DB-driven)
    ├── SyncManager.csproj
    ├── Program.cs                # Kestrel 127.0.0.1:5000, MVC, DI; HttpClient нет
    ├── appsettings.json          # Секции Proxy + Report
    ├── ProxySettings.cs          # Настройки заголовков nginx (UserHeader не используется — debug-заглушка)
    ├── ReportUiSettings.cs       # Статический список провайдеров для формы
    ├── Controllers/
    │   ├── SyncController.cs     # Dashboard + /api/sync/* (пишет в SyncCommands, читает статус/историю из БД)
    │   └── ReportsController.cs  # /reports + api/reports/* (задания, cancel/retry, скачивание из БД)
    ├── Hubs/
    │   └── SyncHub.cs            # SignalR-хаб realtime-обновлений (/hubs/sync), только серверный push
    ├── Middleware/
    │   └── ForwardedUserMiddleware.cs  # Заголовки nginx → ClaimsPrincipal (+ debug-заглушка)
    ├── Models/
    │   └── CreateReportTaskRequest.cs  # Запрос создания задания отчёта
    ├── Services/
    │   └── DbChangeListener.cs   # BackgroundService: LISTEN/NOTIFY (sync_ui_changes, report_tasks) → сигнал refresh
    ├── Views/
    │   ├── _ViewImports.cshtml / _ViewStart.cshtml
    │   ├── Shared/_Layout.cshtml       # Bootstrap + jQuery (локально) + Chart.js + DataTables (CDN) + SignalR
    │   ├── Sync/Dashboard.cshtml       # Панель + Chart + Grid
    │   └── Reports/Index.cshtml        # Раздел отчётов
    └── wwwroot/
        ├── css/site.css
        ├── js/dashboard.js             # SignalR "refresh" (sync/reports) + резервный polling 30 с, Chart.js/DataTables, кнопки
        ├── js/reports.js               # SignalR "refresh" (reports) + резервный polling 30 с, создание, скачивание
        ├── js/notify.js                # Десктоп-уведомления об ошибках (Web Notification API)
        └── lib/                        # Локальные Bootstrap, jQuery, jquery-validation, signalr.min.js
```

**Ключевые файлы:**
- `SyncDaemon/Program.cs` — DI, гард одиночности, регистрация `CommandPoller`; HTTP API нет
- `SyncDaemon/SingleInstanceGuard.cs` — advisory lock против двойного запуска
- `SyncDaemon/CommandPoller.cs` — опрос `SyncCommands`, восстановление цикла из БД
- `SyncDaemon/SyncService.cs` — управление жизненным циклом синхронизации
- `SyncDaemon/SyncEngine.cs` — бизнес-логика синхронизации
- `ReportDaemon/ReportPoller.cs` — атомарный claim заданий отчётов, загрузка файла в БД
- `SyncManager/Program.cs` — DI, Kestrel (5000), Middleware; без HttpClient
- `SyncManager/Controllers/SyncController.cs` — /api/sync/* поверх БД + View
- `SyncManager/Controllers/ReportsController.cs` — раздел отчётов, скачивание из БД
- `SyncManager/Services/DbChangeListener.cs` — LISTEN/NOTIFY (каналы `sync_ui_changes`, `report_tasks`) → SignalR-сигнал `refresh`
- `SyncManager/Middleware/ForwardedUserMiddleware.cs` — аутентификация (+ debug-заглушка)
- `SyncManager/wwwroot/js/dashboard.js`, `wwwroot/js/reports.js` — клиентская логика

## 4. Рабочий процесс разработки

### Соглашения кода
- **Язык комментариев и ответов — русский** (правило зафиксировано в `AGENTS.md`; файла `.continue/rules/ru-rule.md` в репозитории нет)
- Имена типов и методов — на английском, PascalCase; поля-члены — `_camelCase`
- Nullable reference types включены (`<Nullable>enable</Nullable>`)
- XML-документация (`/// <summary>`) на публичных типах
- Все асинхронные методы принимают `CancellationToken`
- Логирование — через `ILogger<T>` с структурированными сообщениями

### Тестирование
- Формального подхода нет. Валидация — по логам и через браузер
- Рекомендуется покрыть `SyncEngine.SyncAsync` юнит-тестами с моками `ISourceDb`/`IDestinationDb`

### Сборка и деплой
- Сборка: `dotnet build` / `dotnet publish -c Release`
- CI/CD, Dockerfile **в репозитории отсутствуют**
- `bin/` и `obj/` в `.gitignore`

### Вклад в проект
- Одна ветка `main`; коммиты — описательные
- Изменения в DI-регистрации — в `Program.cs`

## 5. Ключевые понятия

| Термин | Описание |
|---|---|
| **Source / Destination** | Источник изменений и целевая БД |
| **SyncRecord** | Единица синхронизации: `PrimaryKey`, `LastModified`, `Data` |
| **lastSyncTime** | Метка времени последней итерации (UTC). Хранится в БД (`SyncStatus.LastSyncTime`) и восстанавливается при перезапуске демона |
| **BatchSize** | Размер пакета при постраничном чтении |
| **Insert/Update классификация** | `SyncEngine` определяет: новая (нет PK) или обновляемая (PK есть) |
| **StubDbSource** | In-memory заглушка, реализующая оба интерфейса |
| **ForwardedUserMiddleware** | Заголовки nginx → `ClaimsPrincipal`; локально работает через debug-заглушку (пользователь `"Denis"`, роль `sync-admins` принудительно) — см. «Известные отклонения» |
| **Шина команд** | Таблица `SyncCommands` (Postgres): SyncManager пишет команду, `CommandPoller` демона опрашивает её каждые 2 с |
| **DB-driven API** | `SyncController` (`/api/sync/*`) работает напрямую с БД: статус/история читаются из `SyncStatus`/`SyncHistory`, команды пишутся в `SyncCommands`; HTTP-прокси на демон отсутствует |
| **Realtime-обновления** | `DbChangeListener` (SyncManager, LISTEN/NOTIFY по каналам `sync_ui_changes` и `report_tasks`) шлёт клиентам SignalR-хаба `/hubs/sync` сигнал `refresh` со scope; данные браузер запрашивает через REST-эндпоинты — см. раздел «Realtime-обновления и уведомления» |
| **Polling (резерв)** | Клиент опрашивает REST-эндпоинты каждые 30 с — подхватывает изменения при обрыве WebSocket / LISTEN-соединения |
| **sync-admins** | Группа с правом Start/Stop; остальные — только чтение |

**Используемые паттерны:**
- **Worker / BackgroundService** — фоновая периодическая задача (только в демоне)
- **Dependency Injection + Options pattern** — `IOptions<SyncSettings>`
- **Strategy** — `ISourceDb`/`IDestinationDb` позволяют заменить заглушку
- **Batching / cursor по времени** — постраничное чтение по `LastModified`
- **Middleware pipeline** — ForwardedHeaders → ForwardedUser → MVC
- **Command bus (Postgres)** — веб-панель управляет демоном через таблицу `SyncCommands`, статус/история читаются из БД
- **LISTEN/NOTIFY + SignalR** — серверный realtime: триггеры БД шлют `pg_notify`, `DbChangeListener` мапит канал на scope и пушит сигнал `refresh`; резерв — polling 30 с (аналогично ReportDaemon)

## 6. Подсистема отчётов

**ReportDaemon** формирует отчёты по заданиям из таблицы `ReportTasks`. Запуск формирования — **событийный**: миграция создаёт на таблице триггер `TRG_ReportTasks_Notify` (функция `NotifyReportTask`), который шлёт `pg_notify('report_tasks', <Id>)` при **вставке задания в статусе `pending`** и при **переходе статуса в `pending`** (retry, stale-reset — любой источник записи в БД). `ReportNotificationListener` (BackgroundService в ReportDaemon) держит собственное постоянное соединение с подпиской `LISTEN report_tasks`; по уведомлению он мгновенно будит опросный цикл через `ReportTaskSignal`, и задание берётся **атомарным claim** без ожидания следующего витка. Периодический опрос (`Report:PollIntervalSeconds`) остаётся резервным механизмом — он подхватывает задания, созданные в то время, когда демон был недоступен или соединение LISTEN было разорвано (слушатель переподключается автоматически).

`ReportPoller` (BackgroundService в ReportDaemon) берёт старейшее задание в статусе `pending` и **маршрутизирует его в провайдера отчётов** по значению колонки `ProviderId`. Провайдеры — разнородные библиотеки, подключаемые к демону без изменения его кода.

Задания обрабатываются **параллельно**: одновременно формируется до `Report:MaxParallelReports` отчётов (по умолчанию **3**). Каждое задание выполняется в собственном DI-скоупе и контексте БД, поэтому параллельные задания не делят `DbContext`. Зависшее задание отменяется по тайм-ауту `Report:ProcessingTimeoutMinutes` и помечается `error`.

**Многоэкземплярность:** задание берётся **атомарным условным UPDATE** (claim) — его обрабатывает ровно один экземпляр, победитель фиксирует себя в `ReportTasks.OwnerInstanceId`. Сброс зависших заданий (stale-reset) не трогает собственные задания экземпляра. После успеха файл отчёта читается с локального диска и **сохраняется в базу** (`ReportTaskFile`, bytea), поэтому скачивается через SyncManager с любой машины — локальный путь остаётся лишь метаданными.

> **Важно:** провайдеры регистрируются как singletons и вызываются из нескольких потоков одновременно — **реализации должны быть потокобезопасными** (эталонный `SampleReportProvider` stateless).

### Схема зависимостей

```
                    ┌──────────────────┐
                    │  ReportContracts │  IReportProvider + DTO, ноль зависимостей
                    └────────▲─────────┘
                             │ ProjectReference
        ┌────────────────────┼─────────────────────┐
        │                    │                       │
┌───────┴────────┐   ┌───────┴────────┐      ┌──────┴────────┐
│ SampleProvider │   │ Провайдер ИС-A │      │ Провайдер ИС-B │  (примеры)
└───────▲────────┘   └───────▲────────┘      └──────▲────────┘
        │                    │                       │
         └────────────────────┴───────────────────────┘
                              │ ProjectReference (по одному на провайдера)
                     ┌────────┴─────────┐
                      │   ReportDaemon   │  явная DI-регистрация + ReportPoller
                     └────────┬─────────┘
                              │
                     ┌────────┴─────────┐
                     │     SyncCore     │  сущности ReportTask (+ OwnerInstanceId), ReportTaskFile, миграции
                     └──────────────────┘
```

### Контракт (ReportContracts)

```csharp
public record ReportRequest(long TaskId, string ReportType, string? Payload);
public record GeneratedReportResult(byte[] Content, string FileName, int RowCount);

public interface IReportProvider
{
    string ProviderId { get; }  // совпадает со значением ReportTasks.ProviderId
    Task<GeneratedReportResult> GenerateAsync(ReportRequest request, CancellationToken ct);
}
```

- Провайдер возвращает содержимое отчёта (байты) и имя файла — файл на диск не пишется.
- Демон — антикоррупционный слой: мапит сущность `ReportTask` → `ReportRequest`, сам пишет `Status`/`FilePath`/`Result` в БД.

### Как подключить новую ИС

1. Создайте проект провайдера со ссылкой на `ReportContracts` (+ свой проект модели ИС при необходимости):
   ```xml
   <ProjectReference Include="..\ReportContracts\ReportContracts.csproj" />
   ```
2. Реализуйте `IReportProvider` — публичный неабстрактный класс с **уникальным** `ProviderId`.
3. Добавьте одну строку в `ReportDaemon/ReportDaemon.csproj`:
   ```xml
   <ProjectReference Include="..\MyIsProvider\MyIsProvider.csproj" />
   ```
4. Зарегистрируйте провайдер явно в `ReportDaemon/Program.cs` (маркер-комментарий «Провайдеры отчётов: регистрируем явно»):
   ```csharp
   services.AddSingleton<IReportProvider, MyIsProvider>();
   ```
5. Создавайте задания в `ReportTasks` с этим `ProviderId` (`Status='pending'`).

Регистрация явная (не рефлексия): провайдер попадает в DI только после добавления строки `AddSingleton` — опечатка в имени типа ловится компилятором, а забытая строка проявляется рантайм-ошибкой «провайдер не зарегистрирован». Обновление провайдера = пересборка решения + перезапуск ReportDaemon (`dotnet publish ReportDaemon -c Release` — все провайдеры попадают в выход транзитивно).

**Конвенции:**
- Согласованные версии общих библиотек моделей — единая сборка всего решения.
- Циклические project-ссылки запрещены: общий фрагмент выносится в нижележащий проект (например, `ReportContracts`).

### Режимы отказа

| Сбой | Поведение |
|---|---|
| Исключение в генерации отчёта | Задание → `error`, демон жив, остальные параллельные задания не затрагиваются |
| Зависшее задание (провайдер завис) | Отмена по тайм-ауту (`Report:ProcessingTimeoutMinutes`), задание → `error`, слот освобождается |
| Обрыв соединения LISTEN/NOTIFY | Слушатель переподключается через 5 с; в это время задания подхватывает периодический опрос (`Report:PollIntervalSeconds`) |
| Ошибка в одном из параллельных заданий | Задание → `error`; остальные завершаются независимо |
| DLL провайдера не грузится | Warning при старте, остальные провайдеры работают |
| Дубликат `ProviderId` | Демон не стартует (fail-fast) |
| `ProviderId` пуст / неизвестен | Задание → `error` с явным сообщением |

## 7. Realtime-обновления и уведомления

Веб-панель обновляет данные **в момент изменения в БД**, а не ожидая следующего опроса:

- **Детекция изменений** — триггеры PostgreSQL шлют `pg_notify`: канал `sync_ui_changes` (любой UPDATE `SyncStatus` + INSERT в `SyncHistory`, функция `NotifySyncUiChanges`) и канал `report_tasks` (вставка задания и **любое** изменение статуса, функция `NotifyReportTask`).
- **Серверная часть** — `DbChangeListener` (BackgroundService в SyncManager) держит постоянное соединение с подпиской `LISTEN sync_ui_changes, report_tasks`; по уведомлению мапит канал на scope (`sync_ui_changes` → `"sync"`, `report_tasks` → `"reports"`) и шлёт всем подключённым клиентам SignalR-хаба `/hubs/sync` сигнал `refresh`. Сервер не дублирует данные — браузер запрашивает их через существующие REST-эндпоинты (единый источник истины).
- **Клиентская часть** — клиентская библиотека SignalR завендорена локально (`wwwroot/lib/signalr/signalr.min.js`); по сигналу `refresh`: scope `"sync"` → статус + история (дашборд), scope `"reports"` → сводка и сетка заданий. При первом подключении и после переподключения — полный refresh страницы (защита от пропущенных изменений).
- **Резерв** — polling каждые 30 с по тем же REST-эндпоинтам подхватывает изменения, пока WebSocket или LISTEN-соединение недоступны.

**Десктоп-уведомления об ошибках** (Web Notification API, `wwwroot/js/notify.js`): кнопка «🔔 Уведомления» в navbar запрашивает разрешение браузера; скоп — **только бизнес-ошибки**:

1. новая запись истории синхронизации со статусом `error` → уведомление «Ошибка синхронизации» (время + длительность);
2. переход задания отчёта в статус `error` → уведомление «Отчёт №N завершился ошибкой» (текст `Result`, обрезка ~200 символов).

По клику на уведомление вкладка фокусируется и открывается соответствующая страница. Ошибки, существовавшие до открытия страницы, уведомлениями не показываются (baseline при первом refresh); повторные уведомления одного типа заменяют предыдущее (один `Notification.tag`) — не спамить.

> **Ограничение:** уведомления приходят только пока вкладка открыта в браузере; закрытая вкладка/браузер ничего не получит — это свойство web-приложения.

### Режимы отказа

| Сбой | Поведение |
|---|---|
| Обрыв WebSocket в браузере | SignalR автоматически переподключается; после `reconnected` — полный refresh; до этого данные свежие не старше 30 с (резервный polling) |
| Обрыв LISTEN-соединения в SyncManager | Переподключение через 5 с; пропущенные изменения подхватят polling 30 с и первичный refresh страницы |
| Restарт SyncManager во время изменений | При старте listener подписывается заново; пропущенное закрывает первый refresh страницы + polling |
| ReportDaemon просыпается чаще (триггер на любой смене статуса) | Claim идемпотентен (`WHERE Status='pending'`) — лишних заданий не возникает |
| Notification permission denied / не запрошен | Кнопка показывает состояние; уведомления молча пропускаются, ошибки видны в сетках |
| БД недоступна | REST-эндпоинты вернут 500 (как сейчас); опросы будут завершаться с ошибкой — уведомлений об инфраструктуре нет по решению (скоп только бизнес-ошибки) |

## 8. Многоэкземплярное развёртывание

Система рассчитана на многоэкземплярный запуск:

| Компонент | Экземпляров | Защита / координация |
|---|---|---|
| **SyncDaemon** | **1** | Гард одиночности: advisory lock `pg_try_advisory_lock` — второй запуск завершается с кодом 1 |
| **ReportDaemon** | **N** | Атомарный claim заданий (условный UPDATE) + `OwnerInstanceId`; stale-reset исключает собственные задания |
| **SyncManager** | 1 (достаточно) | DB-driven, без локального состояния; скачивает отчёты из БД |

### Как это работает

- **SyncDaemon** — единая точка синхронизации и команд. При старте открывает соединение с Postgres и вызывает `pg_try_advisory_lock(<const>)`. Если замок уже занят другим экземпляром — лог + выход с кодом 1 (fail-fast). Замок привязан к сессии БД: при смерти процесса база освобождает его автоматически, поэтому перезапуск работает. Авто-failover намеренно не делается — восстановление перезапуском (supervisor/оператор).
- **ReportDaemon** — масштабируется горизонтально. Каждый экземпляр генерирует уникальный `InstanceId` на старт. Взятие задания — атомарный условный UPDATE: `UPDATE ReportTasks SET Status='processing', OwnerInstanceId=<self> WHERE Id=<id> AND Status='pending'`. Если affected rows = 0 — задание забрал другой экземпляр, пропускаем. Stale-reset (сброс зависших заданий) не трогает собственные задания (`OwnerInstanceId <> <self>`).
- **Файлы отчётов** хранятся в БД (`ReportTaskFile`, bytea). Провайдер возвращает содержимое отчёта (байты) напрямую, демон сохраняет их в БД. SyncManager скачивает из БД — неважно, какой экземпляр сформировал отчёт.

### Требования к развёртыванию

- **Все экземпляры ReportDaemon обязаны иметь одинаковый `Report:ProcessingTimeoutMinutes`** — иначе экземпляр с меньшим тайм-аутом может сбросить задание, которое другой честно обрабатывает.
- Одинаковый `Report:PollIntervalSeconds` на всех экземплярах ReportDaemon.
- SyncDaemon и все ReportDaemon указывают на одну и ту же `ConnectionStrings:SyncBus`.

### Режимы отказа

| Сбой | Поведение |
|---|---|
| Второй запуск SyncDaemon | Выход с кодом 1 (лог «уже запущен») |
| Смерть ReportDaemon во время обработки | Задание остаётся `processing`; после тайм-аута другой экземпляр сбрасывает (stale-reset) и доводит до конца |
| Провайдер завис | Задание → `error` по тайм-ауту, слот освобождается |
| Очень большой отчёт (bytea) | Лимит Postgres ~1 ГБ; жёсткого лимита в коде нет (риск очень больших отчётов) |

## 9. Частые задачи

### Добавить реальную реализацию БД
1. Создайте класс `PostgresSourceDb : ISourceDb` и `PostgresDestinationDb : IDestinationDb`
2. В `SyncDaemon/Program.cs` замените регистрации:
   ```csharp
   services.AddSingleton<ISourceDb, PostgresSourceDb>();
   services.AddSingleton<IDestinationDb, PostgresDestinationDb>();
   ```
3. Заполните `Sync.Source.*` / `Sync.Destination.*` в `SyncDaemon/appsettings.json`

### Изменить интервал или размер пакета
- Интервал цикла задаётся **payload команды `start`** (дефолт 30 с) — ключ `Sync.IntervalSeconds` на цикл не влияет
- Размер пакета: `SyncDaemon/appsettings.json` → `Sync.BatchSize`, или переменная окружения: `SYNC__BATCHSIZE=500`

### Изменить интервал опроса команд
- `SyncDaemon/appsettings.json` → `Sync.CommandPollIntervalSeconds` (дефолт 2 с), или `SYNC__COMMANDPOLLINTERVALSECONDS=1`

### Добавить новую операцию (delete)
- `IDestinationDb.DeleteAsync` уже существует
- Добавьте шаг в `SyncEngine.SyncAsync`

### Настроить nginx
```nginx
location / {
    proxy_pass http://127.0.0.1:5000;
    proxy_set_header X-Remote-User $remote_user;
    proxy_set_header X-Remote-Group $remote_groups;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Host $host;
}

# WebSocket для SignalR (realtime-обновления дашборда и отчётов)
location /hubs/ {
    proxy_pass http://127.0.0.1:5000;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_read_timeout 3600s;
}
```

Без блока `location /hubs/` SignalR автоматически отпадёт на лонгполлинг — приложение продолжит работать, но медленнее (известное отклонение).

## 10. Частые проблемы

| Проблема | Решение |
|---|---|
| Команда не выполняется (статус не меняется) | SyncDaemon не запущен или не опрашивает `SyncCommands`. Запустите `cd SyncDaemon && dotnet run`, смотрите логи демона |
| 401 при открытии дашборда | Локально не возникает: debug-заглушка в `ForwardedUserMiddleware` подставляет пользователя. В продакшене — нет заголовка `X-Remote-User` от nginx |
| 403 на Start/Stop | Локально не возникает: заглушка принудительно добавляет роль `sync-admins`. В продакшене — нет группы `sync-admins` в `X-Remote-Group` |
| «No changes found» | `lastSyncTime` = `DateTime.UtcNow` при старте. Изменения до старта не синхронизируются |
| Chart.js / DataTables не загружаются | CDN недоступен. Скачайте файлы локально в `wwwroot/lib/` и замените ссылки в `_Layout.cshtml` |
| `ForwardedHeaders` не работает | Список доверенных прокси захардкожен (`127.0.0.1`) в `SyncManager/Program.cs`; убедитесь, что nginx передаёт запрос с этого IP |
| Порт 5000 занят | Измените в `SyncManager/Program.cs`: `options.ListenLocalhost(5002)` |
| `dotnet run` падает с ошибкой TFM (SyncDaemon) | Установите .NET 8 SDK |

**Советы по отладке:**
- `Logging__LogLevel__Default=Debug` в appsettings.json
- Вся логика синхронизации логируется с подсчётом записей
- Отладка: `dotnet run --launch-profile` / Visual Studio / Rider

## 11. Известные отклонения / TODO кода

Места, где код расходится с «идеальной» архитектурой. Документация описывает текущее поведение; исправление — отдельная задача.

| # | Отклонение | Где | Что сделать |
|---|-----------|-----|-------------|
| 1 | `Sync.IntervalSeconds` **не влияет** на цикл: интервал берётся из payload команды `start` (дефолт 30 с) | `SyncDaemon/CommandPoller.cs` (`ParseIntervalFromPayload`), `SyncDaemon/SyncService.cs` | Либо читать `SyncSettings.IntervalSeconds` как дефолт, либо убрать ключ из конфига и документации |
| 2 | **Debug-заглушка аутентификации** (только в режиме Development): пользователь захардкожен (`"Denis"`), роль `sync-admins` добавляется принудительно — локально 401/403 невозможны. В продакшене middleware читает заголовки nginx | `SyncManager/Middleware/ForwardedUserMiddleware.cs` | Заглушка активна только при `env.IsDevelopment()`; в продакшене — реальные заголовки |
| 3 | Файл `.continue/rules/ru-rule.md`, на который ссылались правила, **отсутствует** в репозитории | — | Правило «комментировать по-русски» зафиксировано в `AGENTS.md`; либо восстановить файл, либо убрать ссылки |

## 12. Ссылки

- [ASP.NET Core MVC](https://learn.microsoft.com/aspnet/core/mvc/)
- [BackgroundService](https://learn.microsoft.com/dotnet/api/microsoft.extensions.hosting.backgroundservice)
- [ForwardedHeaders](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer)
- [Chart.js](https://www.chartjs.org/docs/)
- [DataTables](https://datatables.net/)
- [Options pattern](https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/options)
- Правила работы с репозиторием: `AGENTS.md`
