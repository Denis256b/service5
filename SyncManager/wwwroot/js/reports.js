$(function () {
    "use strict";

    // Данные обновляются в realtime по SignalR (серверный сигнал "refresh" при
    // изменениях ReportTasks); опрос — редкий резерв, каждые 30 с.
    var POLL_INTERVAL_MS = 30000;
    var isAdmin = window.REPORTS_IS_ADMIN === true;

    // === Бейджи статусов ===
    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
        });
    }

    function statusBadge(status) {
        switch (status) {
            case "pending":
                return '<span class="badge bg-secondary">Ожидает</span>';
            case "processing":
                return '<span class="badge bg-primary"><span class="spinner-border spinner-border-sm me-1"></span>Выполняется</span>';
            case "done":
                return '<span class="badge bg-success">Готово</span>';
            case "error":
                return '<span class="badge bg-danger">Ошибка</span>';
            case "cancelled":
                return '<span class="badge bg-dark">Отменено</span>';
            case "created":
                return '<span class="badge bg-info text-dark">Создано</span>';
            case "retry":
                return '<span class="badge bg-warning text-dark">Повтор</span>';
            default:
                return '<span class="badge bg-light text-dark">' + escapeHtml(status) + '</span>';
        }
    }

    function fmtDate(d) {
        return d ? new Date(d).toLocaleString("ru-RU") : "—";
    }

    // === Кнопки действий по строке (видимость — от роли и статуса) ===
    function renderActions(row) {
        var html = '<div class="btn-group" role="group">';
        // Кнопка «Скачать» — только если файл реально есть в базе (hasFile из API),
        // а не просто по статусу done (иначе клик ведёт на 404).
        if (row.status === "done" && row.hasFile) {
            html += '<button class="btn btn-sm btn-outline-success" data-action="download" data-id="' + row.id + '">Скачать</button> ';
        }
        if (isAdmin) {
            if (row.status === "pending") {
                html += '<button class="btn btn-sm btn-outline-danger" data-action="cancel" data-id="' + row.id + '">Отменить</button> ';
            }
            if (row.status === "error" || row.status === "cancelled") {
                html += '<button class="btn btn-sm btn-outline-primary" data-action="retry" data-id="' + row.id + '">Повторить</button> ';
            }
        }
        html += '<button class="btn btn-sm btn-outline-secondary" data-action="details" data-id="' + row.id + '">Детали</button>';
        return html + '</div>';
    }

    // === DataTables: задания ===
    var table = $("#tasksGrid").DataTable({
        data: [],
        columns: [
            { title: "ID", data: "id" },
            { title: "Тип", data: "reportType" },
            { title: "Провайдер", data: "providerId", render: function (d) { return d || "—"; } },
            { title: "Статус", data: "status", render: statusBadge },
            { title: "Создано", data: "createdAt", render: fmtDate },
            { title: "Начато", data: "processedAt", render: fmtDate },
            { title: "Повторы", data: "retryCount" },
            // Колонка без data: DataTables 1.13 передаёт undefined первым аргументом,
            // полная строка — третьим (row). Отсюда "row is undefined" в renderActions.
            { title: "Действия", orderable: false, searchable: false, render: function (data, type, row) { return renderActions(row); } }
        ],
        pageLength: 20,
        lengthMenu: [10, 20, 50],
        order: [[0, "desc"]],
        language: {
            "sEmptyTable": "Нет данных",
            "sInfo": "Показаны _START_–_END_ из _TOTAL_ записей",
            "sInfoEmpty": "Показано 0 из 0 записей",
            "sInfoFiltered": "(отфильтровано из _MAX_ записей)",
            "sLengthMenu": "Показывать _MENU_ записей",
            "sLoadingRecords": "Загрузка...",
            "sProcessing": "Обработка...",
            "sSearch": "Поиск:",
            "sZeroRecords": "Совпадений не найдено",
            "oPaginate": {
                "sFirst": "Первая",
                "sLast": "Последняя",
                "sNext": "Следующая",
                "sPrevious": "Предыдущая"
            },
            "oAria": {
                "sSortAscending": ": сортировка по возрастанию",
                "sSortDescending": ": сортировка по убыванию"
            }
        }
    });

    // === Сводка по статусам (карточки) ===
    function refreshSummary() {
        $.getJSON("/api/reports/summary")
            .done(function (rows) {
                var counts = { pending: 0, processing: 0, done: 0, error: 0, cancelled: 0 };
                (rows || []).forEach(function (r) {
                    if (counts.hasOwnProperty(r.status)) {
                        counts[r.status] = r.count;
                    }
                });
                $("#countPending").text(counts.pending);
                $("#countProcessing").text(counts.processing);
                $("#countDone").text(counts.done);
                $("#countError").text(counts.error);
                $("#countCancelled").text(counts.cancelled);
            })
            .fail(function () {
                console.warn("Не удалось получить сводку по отчётам");
            });
    }

    // === Обновление таблицы ===
    function refresh() {
        $.getJSON("/api/reports/tasks?limit=500")
            .done(function (rows) {
                trackTaskErrors(rows);
                // draw(false) — не сбрасывать текущую страницу и фильтр поиска
                // при каждом обновлении (иначе пользователь «выбрасывается» на 1-ю страницу).
                table.clear().rows.add(rows).draw(false);
            })
            .fail(function () {
                console.warn("Не удалось получить задания");
            });
        refreshSummary();
    }

    // === Десктоп-уведомления об ошибках заданий (notify.js) ===
    // taskId → последний увиденный статус. Первый refresh — только baseline:
    // старые ошибки уведомлениями не показываем; дальше переход задания в 'error'
    // (оно было в другом статусе) даёт десктоп-уведомление (даже если вкладка неактивна).
    var lastTaskStatuses = new Map();
    var tasksBaselineLoaded = false;

    function trackTaskErrors(rows) {
        (rows || []).forEach(function (row) {
            var known = lastTaskStatuses.has(row.id);
            var prev = known ? lastTaskStatuses.get(row.id) : null;

            if (tasksBaselineLoaded && known && prev !== "error" && row.status === "error") {
                // Текст ошибки — из Result, обрезка ~200 символов.
                var result = row.result ? String(row.result).slice(0, 200) : "—";
                window.showDesktopError(
                    "Отчёт №" + row.id + " завершился ошибкой",
                    result,
                    "/reports"
                );
            }

            lastTaskStatuses.set(row.id, row.status);
        });

        tasksBaselineLoaded = true;
    }

    // === Realtime через SignalR: серверный сигнал "refresh" (scope "reports") при
    // изменениях ReportTasks. Саму данные клиент запрашивает через REST-эндпоинты.
    function setupHub() {
        if (typeof signalR === "undefined") {
            console.warn("Клиент SignalR не загружен — работаем только по резервному опросу (30 с)");
            return;
        }

        var connection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/sync")
            .withAutomaticReconnect()
            .build();

        connection.on("refresh", function (msg) {
            if (msg && msg.scope === "reports") {
                refresh();
            }
        });

        // После переподключения — полный refresh: во время обрыва могли пройти изменения.
        connection.onreconnected(function () {
            refresh();
        });

        connection.start().then(function () {
            // Первое подключение — тоже полный refresh (защита от пропущенных изменений).
            refresh();
        }).catch(function (err) {
            console.warn("Не удалось подключиться к SignalR-хабу", err);
        });
    }

    // === Модалка деталей + таймлайн аудита ===
    function openDetails(id) {
        $.getJSON("/api/reports/tasks/" + id)
            .done(function (data) {
                var t = data.task;
                $("#detailId").text(t.id);
                $("#detailType").text(t.reportType);
                $("#detailProvider").text(t.providerId || "—");
                $("#detailStatus").html(statusBadge(t.status));
                $("#detailRetryCount").text(t.retryCount);
                $("#detailPayload").text(t.payload || "—");
                $("#detailResult").text(t.result || "—");
                $("#detailFilePath").text(t.filePath || "—");

                // Доступность скачивания — по наличию файла в базе (hasFile из API),
                // а не по FilePath (метаданные пути на машине-генераторе).
                var $dl = $("#detailDownload");
                if (t.status === "done" && data.hasFile) {
                    $dl.attr("href", "/api/reports/tasks/" + id + "/download").show();
                } else {
                    $dl.removeAttr("href").hide();
                }

                var $tl = $("#detailTimeline").empty();
                (data.events || []).forEach(function (e) {
                    $('<div class="py-1 border-bottom">')
                        .append($('<span class="text-muted me-2">').text(new Date(e.timestamp).toLocaleString("ru-RU")))
                        .append($(statusBadge(e.status)))
                        .append($('<span class="ms-2 text-primary small">').text(e.actor || ""))
                        .append($('<div class="small text-muted">').text(e.message || ""))
                        .appendTo($tl);
                });
                if (!$tl.children().length) {
                    $tl.append('<div class="text-muted small">Событий нет</div>');
                }

                $("#detailsModal").modal("show");
            })
            .fail(function () {
                alert("Не удалось загрузить детали задания");
            });
    }

    // === Обработчики кнопок в таблице (делегирование) ===
    $("#tasksGrid tbody").on("click", "button[data-action]", function () {
        var $btn = $(this).prop("disabled", true);
        var action = $btn.data("action");
        var id = $btn.data("id");

        if (action === "download") {
            window.location.href = "/api/reports/tasks/" + id + "/download";
            return;
        }

        if (action === "details") {
            openDetails(id);
            return;
        }

        // cancel / retry
        $.ajax({ url: "/api/reports/tasks/" + id + "/" + action, method: "POST" })
            .done(function () {
                refresh();
            })
            .fail(function (xhr) {
                var msg = "Неизвестная ошибка";
                if (xhr.status === 409) {
                    msg = "Задание уже выполняется или изменено";
                } else if (xhr.status === 403) {
                    msg = "Недостаточно прав для этого действия";
                } else if (xhr.responseJSON && xhr.responseJSON.error) {
                    msg = xhr.responseJSON.error;
                }
                alert(msg);
            })
            .always(function () {
                $btn.prop("disabled", false);
            });
    });

    // === Форма создания задания (sync-admins) ===
    if (isAdmin) {
        // Загрузка списка провайдеров из конфигурации
        $.getJSON("/api/reports/providers")
            .done(function (providers) {
                var $sel = $("#newProviderId");
                (providers || []).forEach(function (p) {
                    $sel.append($('<option>').val(p.id).text(p.name + " (" + p.id + ")"));
                });
            })
            .fail(function () {
                console.warn("Не удалось получить список провайдеров");
            });

        $("#btnCreateTask").on("click", function () {
            var $btn = $(this).prop("disabled", true);
            $.ajax({
                url: "/api/reports/tasks",
                method: "POST",
                contentType: "application/json",
                data: JSON.stringify({
                    reportType: $("#newReportType").val().trim(),
                    providerId: $("#newProviderId").val(),
                    payload: $("#newPayload").val()
                })
            })
                .done(function () {
                    $("#newReportType").val("");
                    $("#newPayload").val("");
                    refresh();
                })
                .fail(function (xhr) {
                    var msg = "Ошибка создания задания";
                    if (xhr.status === 403) {
                        msg = "Недостаточно прав для создания задания";
                    } else if (xhr.responseJSON && xhr.responseJSON.error) {
                        msg = xhr.responseJSON.error;
                    }
                    alert(msg);
                })
                .always(function () {
                    $btn.prop("disabled", false);
                });
        });
    }

    // === Инициализация + опрос ===
    refresh();
    setInterval(refresh, POLL_INTERVAL_MS);
    setupHub();
});
