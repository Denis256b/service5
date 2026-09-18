using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using SyncCore;

namespace SyncManager.Controllers;

[Authorize]
public class SyncController : Controller
{
    private readonly SyncBusContext _context;

    public SyncController(SyncBusContext context)
    {
        _context = context;
    }

    [HttpGet]
    public IActionResult Dashboard() => View();

    [HttpGet("api/sync/status")]
    public async Task<IActionResult> GetStatus()
    {
        try
        {
            var status = await _context.SyncStatus.FirstOrDefaultAsync();
            if (status == null)
            {
                return NotFound("Status not found");
            }
            
            // Convert to SyncStatus for response
            var syncStatus = new SyncCore.SyncStatus
            {
                IsRunning = status.IsRunning,
                StartTime = status.LastSyncTime,
                TotalInserted = status.TotalInserted,
                TotalUpdated = status.TotalUpdated,
                TotalCopied = status.TotalInserted + status.TotalUpdated,
                LastSyncTime = status.LastSyncTime,
                IntervalSeconds = status.IntervalSeconds,
                BatchSize = status.BatchSize,
                Uptime = (long)(DateTime.UtcNow - status.LastSyncTime).TotalSeconds
            };
            
            return Ok(syncStatus);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpPost("api/sync/start")]
    [Authorize(Roles = "sync-admins")]
    public async Task<IActionResult> Start()
    {
        try
        {
            var command = new SyncCommand
            {
                CommandType = "start",
                Status = "pending",
                CreatedAt = DateTime.UtcNow
            };
            
            _context.SyncCommands.Add(command);
            await _context.SaveChangesAsync();
            
            return Ok(new { success = true, message = "Команда принята" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpPost("api/sync/stop")]
    [Authorize(Roles = "sync-admins")]
    public async Task<IActionResult> Stop()
    {
        try
        {
            var command = new SyncCommand
            {
                CommandType = "stop",
                Status = "pending",
                CreatedAt = DateTime.UtcNow
            };
            
            _context.SyncCommands.Add(command);
            await _context.SaveChangesAsync();
            
            return Ok(new { success = true, message = "Команда принята" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpPost("api/sync/execute")]
    public async Task<IActionResult> ExecuteOnce()
    {
        try
        {
            var command = new SyncCommand
            {
                CommandType = "execute",
                Status = "pending",
                CreatedAt = DateTime.UtcNow
            };
            
            _context.SyncCommands.Add(command);
            await _context.SaveChangesAsync();
            
            return Ok(new { success = true, message = "Команда принята" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }

    [HttpGet("api/sync/history")]
    public async Task<IActionResult> GetHistory(int limit = 100)
    {
        try
        {
            var history = await _context.SyncHistory
                .OrderByDescending(h => h.Timestamp)
                .Take(limit)
                .ToListAsync();
                
            return Ok(history);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal server error: {ex.Message}");
        }
    }
}