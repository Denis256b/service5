$(function () {
    "use strict";

    var pollTimer = null;
    // Данные обновляются в realtime по SignalR (серверный сигнал "refresh" при
    // изменениях в БД); опрос — редкий резерв, каждые 30 с.
    var POLL_INTERVAL_MS = 30000;
    var MAX_CHART_POINTS = 50;

    // === Chart.js: активность по итерациям ===
    var activityChart = new Chart(document.getElementById("activityChart").getContext("2d"), {
        type: "bar",
        data: {
            labels: [],
            datasets: [
                { label: "Вставлено", data: [], backgroundColor: "#198754" },
                { label: "Обновлено", data: [], backgroundColor: "#0dcaf0" }
            ]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: {
                title: { display: true, text: "Записи по итерациям" },
                legend: { position: "top" },
                tooltip: { mode: "index", intersect: false }
            },
            scales: {
                x: { title: { display: true, text: "Время" } },
                y: { beginAtZero: true, title: { display: true, text: "Записи" } }
            }
        }
    });

    function addChartPoint(inserted, updated) {
        activityChart.data.labels.push(new Date().toLocaleTimeString("ru-RU"));
        activityChart.data.datasets[0].data.push(inserted);
        activityChart.data.datasets[1].data.push(updated);
        while (activityChart.data.labels.length > MAX_CHART_POINTS) {
            activityChart.data.labels.shift();
            activityChart.data.datasets[0].data.shift();
            activityChart.data.datasets[1].data.shift();
        }
        activityChart.update();
    }

    // === DataTables: история ===
    var historyTable = $("#historyGrid").DataTable({
        data: [],
        columns: [
            {
                title: "Время", data: "timestamp",
                render: function (d) { return new Date(d).toLocaleString("ru-RU"); }
            },
            {
                title: "Тип", data: "trigger",
                // Демон пишет строчные "manual"/"automatic" — сравниваем без учёта регистра.
                render: function (d) {
                    return String(d).toLowerCase() === "manual"
                        ? '<span class="badge bg-primary">Ручной</span>'
                        : '<span class="badge bg-secondary">Авто</span>';
                }
            },
            {
                title: "Вставлено", data: "inserted",
                render: function (d) {
                    return '<span class="text-success fw-bold">' + d + '</span>';
                }
            },
            {
                title: "Обновлено", data: "updated",
                render: function (d) {
                    return '<span class="text-info fw-bold">' + d + '</span>';
                }
            },
            { title: "Всего", data: "total" },
            {
                title: "Длительность", data: "duration",
                // API отдаёт число (миллисекунды, long в БД), а не строку —
                // d.split() падал с TypeError. Форматируем как "1 234 мс".
                render: function (d) {
                    if (d === null || d === undefined) return "—";
                    var ms = Number(d);
                    if (isNaN(ms)) return String(d);
                    return ms.toLocaleString("ru-RU") + " мс";
                }
            },
            {
                title: "Статус", data: "status",
                // Демон пишет строчные "success"/"error" — сравниваем без учёта регистра,
                // иначе все строки (даже успешные) получали красный «ошибочный» бейдж.
                render: function (d) {
                    var cls = String(d).toLowerCase() === "success" ? "bg-success" : "bg-danger";
                    return '<span class="badge ' + cls + '">' + d + '</span>';
                }
            }
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

    // === Обновление статусной панели ===
    function updateStatusPanel(status) {
        var $ind = $("#statusIndicator");

        if (status.isRunning) {
            $ind.text("Выполняется")
                .removeClass("status-stopped").addClass("status-running");
            $("#btnStart").prop("disabled", true);
            $("#btnStop").prop("disabled", false);
        } else {
            $ind.text("Остановлен")
                .removeClass("status-running").addClass("status-stopped");
            $("#btnStart").prop("disabled", false);
            $("#btnStop").prop("disabled", true);
        }

        $("#uptimeDisplay").text(formatUptime(status.uptime));
        $("#insertedDisplay").text(status.totalInserted.toLocaleString("ru-RU"));
        $("#updatedDisplay").text(status.totalUpdated.toLocaleString("ru-RU"));
        $("#totalDisplay").text(status.totalCopied.toLocaleString("ru-RU"));

        if (status.lastSyncTime) {
            var dt = new Date(status.lastSyncTime);
            $("#lastSyncDisplay").text(dt.toLocaleString("ru-RU"));
        }

        $("#configDisplay").text(status.intervalSeconds + "с / " + status.batchSize);
    }

    function formatUptime(ts) {
        // API отдаёт число (секунды, long в БД), а не строку —
        // ts.split() падал с TypeError при uptime > 0. Форматируем как ЧЧ:ММ:СС.
        var total = Number(ts);
        if (isNaN(total) || total <= 0) return "00:00:00";
        var h = Math.floor(total / 3600);
        var m = Math.floor((total % 3600) / 60);
        var s = Math.floor(total % 60);
        function pad(n) { return n < 10 ? "0" + n : "" + n; }
        return pad(h) + ":" + pad(m) + ":" + pad(s);
    }

    // === Опрос статуса ===
    function pollStatus() {
        $.getJSON("/api/sync/status")
            .done(updateStatusPanel)
            .fail(function () {
                console.warn("Не удалось получить статус");
            });
    }

    // === Сводка по отчётам (карточка на дашборде) ===
    function refreshReportSummary() {
        $.getJSON("/api/reports/summary")
            .done(function (rows) {
                var counts = { pending: 0, processing: 0, done: 0, error: 0, cancelled: 0 };
                (rows || []).forEach(function (r) {
                    if (counts.hasOwnProperty(r.status)) {
                        counts[r.status] = r.count;
                    }
                });
                $("#reportPendingCount").text(counts.pending);
                $("#reportProcessingCount").text(counts.processing);
                $("#reportDoneCount").text(counts.done);
                $("#reportErrorCount").text(counts.error);
                $("#reportCancelledCount").text(counts.cancelled);
            })
            .fail(function () {
                console.warn("Не удалось получить сводку по отчётам");
            });
    }

    function refreshHistory() {
        $.getJSON("/api/sync/history")
            .done(function (rows) {
                trackHistoryErrors(rows);
                historyTable.clear().rows.add(rows).draw();
            })
            .fail(function () {
                console.warn("Не удалось получить историю");
            });
    }

    // === Десктоп-уведомления об ошибках синхронизации (notify.js) ===
    // Множество известных ID записей истории. Первый refresh — только baseline:
    // старые ошибки уведомлениями не показываем; дальше каждая НОВАЯ запись
    // со статусом error даёт десктоп-уведомление (даже если вкладка неактивна).
    var knownHistoryIds = new Set();
    var historyBaselineLoaded = false;

    function trackHistoryErrors(rows) {
        (rows || []).forEach(function (row) {
            var isNew = !knownHistoryIds.has(row.id);

            if (isNew && historyBaselineLoaded && String(row.status).toLowerCase() === "error") {
                var when = row.timestamp ? new Date(row.timestamp).toLocaleString("ru-RU") : "—";
                var duration = (row.duration !== null && row.duration !== undefined)
                    ? row.duration + " мс"
                    : "";
                window.showDesktopError(
                    "Ошибка синхронизации",
                    when + (duration ? " · " + duration : ""),
                    "/"
                );
            }

            knownHistoryIds.add(row.id);
        });

        historyBaselineLoaded = true;
    }

    // === Realtime через SignalR: серверный сигнал "refresh" при изменениях в БД.
    // Саму данные клиент запрашивает через существующие REST-эндпоинты —
    // единый источник истины, сериализация не дублируется.
    function setupHub() {
        if (typeof signalR === "undefined") {
            console.warn("Клиент SignalR не загружен — работаем только по резервному опросу (30 с)");
            return;
        }

        var refreshAll = function () {
            pollStatus();
            refreshHistory();
            refreshReportSummary();
        };

        var connection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/sync")
            .withAutomaticReconnect()
            .build();

        connection.on("refresh", function (msg) {
            if (!msg || !msg.scope) {
                return;
            }
            if (msg.scope === "sync") {
                pollStatus();
                refreshHistory();
            } else if (msg.scope === "reports") {
                refreshReportSummary();
            }
        });

        // После переподключения — полный refresh: во время обрыва могли пройти изменения.
        connection.onreconnected(refreshAll);

        connection.start().then(function () {
            // Первое подключение — тоже полный refresh: изменения между загрузкой
            // страницы и подключением к хабу иначе были бы пропущены.
            refreshAll();
        }).catch(function (err) {
            console.warn("Не удалось подключиться к SignalR-хабу", err);
        });
    }

    function startPolling() {
        stopPolling();
        pollTimer = setInterval(function () {
            pollStatus();
            refreshHistory();
            refreshReportSummary();
        }, POLL_INTERVAL_MS);
    }

    function stopPolling() {
        if (pollTimer) {
            clearInterval(pollTimer);
            pollTimer = null;
        }
    }

    // === Обработчики кнопок ===

    $("#btnStart").on("click", function () {
        var $btn = $(this).prop("disabled", true);
        $.post("/api/sync/start")
            .done(function (res) {
                if (res.success) {
                    pollStatus();
                    startPolling();
                }
            })
            .fail(function () {
                $btn.prop("disabled", false);
                alert("Ошибка запуска");
            });
    });

    $("#btnStop").on("click", function () {
        var $btn = $(this).prop("disabled", true);
        $.post("/api/sync/stop")
            .done(function (res) {
                if (res.success) {
                    pollStatus();
                    stopPolling();
                }
            })
            .fail(function () {
                $btn.prop("disabled", false);
                alert("Ошибка остановки");
            });
    });

    $("#btnExecute").on("click", function () {
        var $btn = $(this).prop("disabled", true);
        $.post("/api/sync/execute")
            .done(function (res) {
                if (res.success) {
                    addChartPoint(res.result.inserted, res.result.updated);
                    pollStatus();
                    refreshHistory();
                }
            })
            .always(function () {
                $btn.prop("disabled", false);
            });
    });

    // === Инициализация ===
    pollStatus();
    refreshHistory();
    refreshReportSummary();
    startPolling();
    setupHub();
});
