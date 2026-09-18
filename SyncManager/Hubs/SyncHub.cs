using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace SyncManager.Hubs;

/// <summary>
/// SignalR-хаб realtime-обновлений веб-панели. Клиенту шлётся только сигнал
/// «данные изменились» (scope: "sync" / "reports") — сами данные браузер запрашивает
/// через существующие REST-эндпоинты, поэтому источник истины един и сериализация не дублируется.
/// Исходящие методы у клиента нет (только серверный push), хаб пустой намеренно.
/// </summary>
[Authorize]
public class SyncHub : Hub
{
}
