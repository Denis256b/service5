using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyncCore;
using SyncManager.Models;

namespace SyncManager.Controllers;

/// <summary>
/// Раздел отчётов: страница и API заданий на формирование отчётов.
/// Просмотр и скачивание — для всех аутентифицированных,
/// создание/отмена/повтор — только для группы sync-admins.
/// </summary>
[Authorize]
public class ReportsController : Controller
{
    private readonly SyncBusContext _context;
    private readonly ReportUiSettings _settings;

    public ReportsController(SyncBusContext context, IOptions<ReportUiSettings> settings)
    {
        _context = context;
        _settings = settings.Value;
    }

    /// <summary>
    /// Страница раздела «Отчёты».
    /// </summary>
    [HttpGet("reports")]
    public IActionResult Index() => View();

    /// <summary>
    /// Список заданий (newest first), опциональная фильтрация по статусу.
    /// </summary>
    [HttpGet("api/reports/tasks")]
    public async Task<IActionResult> GetTasks(string? status, int limit = 50)
    {
        try
        {
            var query = _context.ReportTasks.AsQueryable();
            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(t => t.Status == status);
            }

            // HasFile — наличие файла отчёта в базе (ReportTaskFiles):
            // по нему UI решает, показывать ли кнопку «Скачать» (FilePath — лишь метаданные).
            var tasks = await query
                .OrderByDescending(t => t.Id)
                .Take(Math.Clamp(limit, 1, 500))
                .Select(t => new
                {
                    t.Id,
                    t.ReportType,
                    t.Payload,
                    t.ProviderId,
                    t.Status,
                    t.CreatedAt,
                    t.ProcessedAt,
                    t.FilePath,
                    t.Result,
                    t.RetryCount,
                    HasFile = _context.ReportTaskFiles.Any(f => f.TaskId == t.Id)
                })
                .ToListAsync();

            return Ok(tasks);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Детали задания + события аудита.
    /// </summary>
    [HttpGet("api/reports/tasks/{id:long}")]
    public async Task<IActionResult> GetTask(long id)
    {
        try
        {
            var task = await _context.ReportTasks.FirstOrDefaultAsync(t => t.Id == id);
            if (task == null)
            {
                return NotFound(new { error = "Задание не найдено" });
            }

            var events = await _context.ReportTaskEvents
                .Where(e => e.TaskId == id)
                .OrderBy(e => e.Timestamp)
                .ThenBy(e => e.Id)
                .ToListAsync();

            // Файл хранится в базе (ReportTaskFile) — наличие файла, а не FilePath,
            // определяет доступность скачивания.
            var hasFile = await _context.ReportTaskFiles.AnyAsync(f => f.TaskId == id);

            return Ok(new { task, events, hasFile });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Создание задания (sync-admins).
    /// </summary>
    [HttpPost("api/reports/tasks")]
    [Authorize(Roles = "sync-admins")]
    public async Task<IActionResult> CreateTask([FromBody] CreateReportTaskRequest request)
    {
        try
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ReportType))
            {
                return BadRequest(new { error = "Укажите тип отчета" });
            }

            if (string.IsNullOrWhiteSpace(request.ProviderId))
            {
                return BadRequest(new { error = "Укажите провайдера отчета" });
            }

            var actor = User.Identity?.Name;

            var task = new ReportTask
            {
                ReportType = request.ReportType.Trim(),
                ProviderId = request.ProviderId.Trim(),
                Payload = string.IsNullOrWhiteSpace(request.Payload) ? null : request.Payload,
                Status = "pending",
                CreatedAt = DateTime.UtcNow
            };

            _context.ReportTasks.Add(task);
            await _context.SaveChangesAsync();

            _context.ReportTaskEvents.Add(new ReportTaskEvent
            {
                TaskId = task.Id,
                Status = "created",
                Timestamp = DateTime.UtcNow,
                Message = "Задание создано",
                Actor = actor
            });
            await _context.SaveChangesAsync();

            return Ok(new { id = task.Id, success = true, message = "Задание создано" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Отмена задания (sync-admins). Только pending; гонка с claim демоном → 409.
    /// </summary>
    [HttpPost("api/reports/tasks/{id:long}/cancel")]
    [Authorize(Roles = "sync-admins")]
    public async Task<IActionResult> CancelTask(long id)
    {
        try
        {
            var exists = await _context.ReportTasks.AnyAsync(t => t.Id == id);
            if (!exists)
            {
                return NotFound(new { error = "Задание не найдено" });
            }

            // Условное обновление: только pending (защита от гонки с claim демоном)
            var updated = await _context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"ReportTasks\" SET \"Status\" = 'cancelled' WHERE \"Id\" = {id} AND \"Status\" = 'pending'");

            if (updated == 0)
            {
                return Conflict(new { error = "Задание уже выполняется или изменено" });
            }

            var actor = User.Identity?.Name;
            _context.ReportTaskEvents.Add(new ReportTaskEvent
            {
                TaskId = id,
                Status = "cancelled",
                Timestamp = DateTime.UtcNow,
                Message = "Задание отменено",
                Actor = actor
            });
            await _context.SaveChangesAsync();

            return Ok(new { success = true, message = "Задание отменено" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Повтор задания (sync-admins). Только error/cancelled; RetryCount++, очищается FilePath.
    /// </summary>
    [HttpPost("api/reports/tasks/{id:long}/retry")]
    [Authorize(Roles = "sync-admins")]
    public async Task<IActionResult> RetryTask(long id)
    {
        try
        {
            var task = await _context.ReportTasks.FirstOrDefaultAsync(t => t.Id == id);
            if (task == null)
            {
                return NotFound(new { error = "Задание не найдено" });
            }

            if (task.Status != "error" && task.Status != "cancelled")
            {
                return Conflict(new { error = "Повтор доступен только для заданий в статусе error или cancelled" });
            }

            task.Status = "pending";
            task.RetryCount++;
            task.FilePath = null;
            task.ProcessedAt = null;

            // Удаляем файл отчета из базы (согласованно с очисткой FilePath)
            var file = await _context.ReportTaskFiles.FirstOrDefaultAsync(f => f.TaskId == id);
            if (file != null)
            {
                _context.ReportTaskFiles.Remove(file);
            }

            var actor = User.Identity?.Name;
            _context.ReportTaskEvents.Add(new ReportTaskEvent
            {
                TaskId = id,
                Status = "retry",
                Timestamp = DateTime.UtcNow,
                Message = $"Повторная попытка (№{task.RetryCount})",
                Actor = actor
            });
            await _context.SaveChangesAsync();

            return Ok(new { success = true, message = "Задание поставлено в очередь" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Скачивание файла отчета. Файл хранится в базе (ReportTaskFile) —
    /// скачивается с любой машины, локальный диск не используется.
    /// </summary>
    [HttpGet("api/reports/tasks/{id:long}/download")]
    public async Task<IActionResult> DownloadTask(long id)
    {
        try
        {
            var task = await _context.ReportTasks.FirstOrDefaultAsync(t => t.Id == id);
            if (task == null)
            {
                return NotFound(new { error = "Задание не найдено" });
            }

            // Файл отчета хранится в базе данных (ReportTaskFile)
            var file = await _context.ReportTaskFiles.FirstOrDefaultAsync(f => f.TaskId == id);
            if (file == null)
            {
                return NotFound(new { error = "Файл отчета не найден в базе" });
            }

            // Имя файла: из метаданных FilePath, fallback — report_{type}_{id}.xls
            var fileName = string.IsNullOrWhiteSpace(task.FilePath)
                ? $"report_{task.ReportType}_{id}.xls"
                : Path.GetFileName(task.FilePath);

            return File(file.Content, file.ContentType ?? "application/vnd.ms-excel", fileName);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Счётчики заданий по статусам (для сводки на дашборде).
    /// </summary>
    [HttpGet("api/reports/summary")]
    public async Task<IActionResult> GetSummary()
    {
        try
        {
            var groups = await _context.ReportTasks
                .GroupBy(t => t.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync();

            return Ok(groups);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    /// <summary>
    /// Статический список провайдеров из конфигурации (для select в форме).
    /// </summary>
    [HttpGet("api/reports/providers")]
    public IActionResult GetProviders()
    {
        return Ok(_settings.Providers);
    }
}
