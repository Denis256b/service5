using System;

namespace SyncCore;

/// <summary>
/// Файл отчета, сохранённый в базе данных (bytea).
/// Отдельная таблица — чтобы списочные запросы по заданиям не тянули бинарные данные.
/// </summary>
public class ReportTaskFile
{
    /// <summary>
    /// Уникальный идентификатор записи.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Идентификатор задания (ReportTasks.Id); один файл на задание.
    /// </summary>
    public long TaskId { get; set; }

    /// <summary>
    /// Бинарное содержимое файла отчета.
    /// </summary>
    public byte[] Content { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Размер файла в байтах.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// MIME-тип файла (например, application/vnd.ms-excel).
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Время сохранения файла в базу данных.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Задание, к которому принадлежит файл (навигационное свойство).
    /// </summary>
    public ReportTask? Task { get; set; }
}
