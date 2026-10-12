using AccountOpeningTracker.Api.Data;
using AccountOpeningTracker.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AccountOpeningTracker.Api.Services;

public interface ISettingsService
{
    Task<int> GetSlaThresholdAsync();
    Task SetSlaThresholdAsync(int workingDays, int userId);
    Task<string> GetAutomationModeAsync();
    Task SetAutomationModeAsync(string mode, int userId);
}

/// <summary>
/// SLA threshold: Admin-configured value in SystemSettings, falling back to
/// appsettings SlaSettings:DefaultThresholdWorkingDays, then 7.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    public const int MinSla = 1;
    public const int MaxSla = 60;

    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _config;
    private int? _cached;

    public SettingsService(ApplicationDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public async Task<int> GetSlaThresholdAsync()
    {
        if (_cached.HasValue) return _cached.Value;

        var stored = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.SlaThresholdWorkingDays)
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        int value = int.TryParse(stored, out var parsed) && parsed >= MinSla && parsed <= MaxSla
            ? parsed
            : _config.GetValue("SlaSettings:DefaultThresholdWorkingDays", 7);

        _cached = value;
        return value;
    }

    public async Task SetSlaThresholdAsync(int workingDays, int userId)
    {
        if (workingDays < MinSla || workingDays > MaxSla)
            throw new ArgumentOutOfRangeException(nameof(workingDays), $"SLA must be between {MinSla} and {MaxSla} working days.");

        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.SlaThresholdWorkingDays);
        if (row == null)
        {
            row = new SystemSetting { Key = SettingKeys.SlaThresholdWorkingDays };
            _db.SystemSettings.Add(row);
        }
        row.Value = workingDays.ToString();
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedByUserId = userId;
        await _db.SaveChangesAsync();
        _cached = workingDays;
    }

    public async Task<string> GetAutomationModeAsync()
    {
        var stored = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.AiAutomationMode)
            .Select(s => s.Value)
            .FirstOrDefaultAsync();
        if (stored != null && AutomationModes.All.Contains(stored)) return stored;

        var configured = _config["Ai:AutomationMode"];
        return configured != null && AutomationModes.All.Contains(configured) ? configured : AutomationModes.Automatic;
    }

    public async Task SetAutomationModeAsync(string mode, int userId)
    {
        if (!AutomationModes.All.Contains(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), $"Mode must be one of {string.Join(", ", AutomationModes.All)}.");

        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.AiAutomationMode);
        if (row == null)
        {
            row = new SystemSetting { Key = SettingKeys.AiAutomationMode };
            _db.SystemSettings.Add(row);
        }
        row.Value = mode;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedByUserId = userId;
        await _db.SaveChangesAsync();
    }
}
