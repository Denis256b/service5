// Десктоп-уведомления об ошибках (Web Notification API).
// Показывает только бизнес-ошибки: запись истории синхронизации со статусом error
// и переход задания отчёта в status 'error'. Уведомление приходит, пока вкладка
// открыта в браузере; закрытая вкладка/браузер ничего не получит (свойство web-приложения).
(function () {
    "use strict";

    // Пользовательское включение/выключение поверх разрешения браузера:
    // permission 'granted' дается один раз, а выключить уведомления без перехода
    // в настройки сайта иначе нельзя — держим отдельный флаг.
    var ENABLED_STORAGE_KEY = "sync.desktopNotifications.enabled";

    function isSupported() {
        return typeof window !== "undefined" && "Notification" in window;
    }

    function isEnabled() {
        try {
            return localStorage.getItem(ENABLED_STORAGE_KEY) === "1";
        } catch (e) {
            return false; // приватный режим / storage недоступен
        }
    }

    function setEnabled(value) {
        try {
            localStorage.setItem(ENABLED_STORAGE_KEY, value ? "1" : "0");
        } catch (e) {
            // без сохранения флаг живёт до перезагрузки страницы
        }
    }

    // Состояние кнопки: granted / denied / default / unsupported
    function updateButton() {
        var $btn = $("#notifyToggle");
        if (!$btn.length || !isSupported()) {
            return;
        }

        var permission = Notification.permission;
        var label;

        if (permission === "granted") {
            label = isEnabled() ? "🔔 Уведомления: вкл" : "🔕 Уведомления: выкл";
        } else if (permission === "denied") {
            label = "🚫 Заблокировано";
        } else {
            label = "🔔 Уведомления";
        }

        $btn.text(label);
    }

    // Клик по кнопке: запросить разрешение (или переключить локальный флаг при granted).
    function onToggleClick() {
        if (!isSupported()) {
            alert("Браузер не поддерживает десктоп-уведомления");
            return;
        }

        var permission = Notification.permission;

        if (permission === "granted") {
            // Разрешение уже есть — переключаем локальный флаг вкл/выкл.
            setEnabled(!isEnabled());
        } else if (permission === "default") {
            Notification.requestPermission().then(function (result) {
                if (result === "granted") {
                    setEnabled(true);
                }
                updateButton();
            });
        } else if (permission === "denied") {
            alert("Уведомления заблокированы браузером. Разрешите их в настройках сайта (значок замка рядом с адресной строкой).");
        }

        updateButton();
    }

    // Публичная функция для dashboard.js / reports.js: десктоп-уведомление об ошибке.
    // Работает только при разрешении браузера и включённом локальном флаге;
    // повторные уведомления с одним tag заменяют предыдущее (не спамить).
    window.showDesktopError = function (title, body, url) {
        if (!isSupported() || Notification.permission !== "granted" || !isEnabled()) {
            return;
        }

        try {
            var notification = new Notification(title, {
                body: body || "",
                icon: "/favicon.ico",
                // tag устойчив для типа ошибки (url+title) — новое уведомление
                // того же типа заменяет предыдущее вместо наложения друг на друга.
                tag: (url || "") + "|" + title
            });

            notification.onclick = function () {
                window.focus();
                if (url) {
                    window.location.href = url;
                }
                this.close();
            };
        } catch (e) {
            console.warn("Не удалось создать десктоп-уведомление", e);
        }
    };

    $(function () {
        updateButton();
        $("#notifyToggle").on("click", onToggleClick);
    });
})();
