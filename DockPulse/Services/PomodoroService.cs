using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using DockPulse.Helpers;

namespace DockPulse.Services;

public enum PomodoroPhase
{
    Focus,
    ShortBreak,
    LongBreak,
}

public sealed record PomodoroDayCount(
    DateOnly Date,
    string Label,
    int Sessions,
    bool IsToday);

public sealed record PomodoroSnapshot(
    PomodoroPhase Phase,
    int SessionIndex,
    int SessionsPerCycle,
    int RemainingSeconds,
    int TotalSeconds,
    bool IsRunning,
    int FocusMinutes,
    int ShortBreakMinutes,
    int LongBreakMinutes,
    bool NotifyOnPhaseEnd,
    int TodaySessions,
    IReadOnlyList<PomodoroDayCount> Last7Days)
{
    public string PhaseSubtitle => Phase switch
    {
        PomodoroPhase.Focus => $"Focus {SessionIndex}/{SessionsPerCycle}",
        PomodoroPhase.ShortBreak => "Short break",
        _ => "Long break",
    };

    public string FormatDockSubtitle()
    {
        if (IsRunning)
        {
            return PhaseSubtitle;
        }

        return RemainingSeconds < TotalSeconds
            ? $"Paused • {PhaseSubtitle}"
            : PhaseSubtitle;
    }
}

public sealed partial class PomodoroService : IDisposable
{
    private readonly object _lock = new();
    private readonly string _filePath;
    private readonly Dictionary<DateOnly, int> _dailyCounts = [];

    private int _focusMinutes = 25;
    private int _shortBreakMinutes = 5;
    private int _longBreakMinutes = 15;
    private int _sessionsPerCycle = 4;
    private bool _notifyOnPhaseEnd = true;

    private PomodoroPhase _phase = PomodoroPhase.Focus;
    private int _sessionIndex = 1;
    private int _totalSeconds = 25 * 60;
    private int _remainingSeconds = 25 * 60;
    private bool _isRunning;
    private DateTimeOffset _phaseEndUtc;
    private Timer? _tickTimer;

    public event Action? StateChanged;

    public PomodoroService()
    {
        _filePath = DockPulseHelpers.GetSettingsFilePath("pomodoro.json");
        LoadConfig();

        _totalSeconds = GetPhaseDurationSecondsLocked(_phase);
        _remainingSeconds = _totalSeconds;
    }

    public PomodoroSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return BuildSnapshotLocked();
        }
    }

    public bool ToggleStartPause()
    {
        bool running;
        lock (_lock)
        {
            if (_isRunning)
            {
                SyncRemainingFromClockLocked();
                StopTimerLocked();
            }
            else
            {
                if (_remainingSeconds <= 0)
                {
                    _totalSeconds = GetPhaseDurationSecondsLocked(_phase);
                    _remainingSeconds = _totalSeconds;
                }

                StartTimerLocked();
            }

            running = _isRunning;
        }

        StateChanged?.Invoke();
        return running;
    }

    public void AddOneMinute()
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                SyncRemainingFromClockLocked();
                _remainingSeconds += 60;
                _phaseEndUtc = DateTimeOffset.UtcNow.AddSeconds(_remainingSeconds);
            }
            else
            {
                _remainingSeconds += 60;
            }

            _totalSeconds = Math.Max(_totalSeconds, _remainingSeconds);
        }

        StateChanged?.Invoke();
    }

    public void Reset()
    {
        lock (_lock)
        {
            StopTimerLocked();
            _totalSeconds = GetPhaseDurationSecondsLocked(_phase);
            _remainingSeconds = _totalSeconds;
        }

        StateChanged?.Invoke();
    }

    public PomodoroSnapshot SkipPhase()
    {
        PomodoroSnapshot snapshot;
        lock (_lock)
        {
            StopTimerLocked();
            AdvancePhaseLocked(completedByTimer: false);
            snapshot = BuildSnapshotLocked();
        }

        StateChanged?.Invoke();
        return snapshot;
    }

    public void UpdateSettings(
        int focusMinutes,
        int shortBreakMinutes,
        int longBreakMinutes,
        int sessionsPerCycle,
        bool notifyOnPhaseEnd)
    {
        lock (_lock)
        {
            bool atFull = !_isRunning && _remainingSeconds == _totalSeconds;

            _focusMinutes = Math.Clamp(focusMinutes, 1, 120);
            _shortBreakMinutes = Math.Clamp(shortBreakMinutes, 1, 60);
            _longBreakMinutes = Math.Clamp(longBreakMinutes, 1, 60);
            _sessionsPerCycle = Math.Clamp(sessionsPerCycle, 1, 8);
            _sessionIndex = Math.Clamp(_sessionIndex, 1, _sessionsPerCycle);
            _notifyOnPhaseEnd = notifyOnPhaseEnd;

            if (atFull)
            {
                _totalSeconds = GetPhaseDurationSecondsLocked(_phase);
                _remainingSeconds = _totalSeconds;
            }

            SaveConfigLocked();
        }

        StateChanged?.Invoke();
    }

    public static string FormatMmSs(int totalSeconds)
    {
        int clamped = Math.Max(0, totalSeconds);
        return $"{clamped / 60:00}:{clamped % 60:00}";
    }

    private void OnTimerTick()
    {
        string? notifyTitle = null;
        string? notifyBody = null;
        bool shouldNotifyStateChanged = false;

        lock (_lock)
        {
            if (!_isRunning)
            {
                return;
            }

            SyncRemainingFromClockLocked();
            shouldNotifyStateChanged = true;

            if (_remainingSeconds <= 0)
            {
                StopTimerLocked();
                AdvancePhaseLocked(completedByTimer: true);

                if (_notifyOnPhaseEnd)
                {
                    var snap = BuildSnapshotLocked();
                    notifyTitle = "Pomodoro — Phase Complete";
                    notifyBody = $"Up next: {snap.PhaseSubtitle} ({FormatMmSs(snap.TotalSeconds)})";
                }
            }
        }

        if (shouldNotifyStateChanged)
        {
            StateChanged?.Invoke();
        }

        if (notifyTitle is not null && notifyBody is not null)
        {
            WindowsNotificationHelper.ShowToast(notifyTitle, notifyBody);
        }
    }

    private void StartTimerLocked()
    {
        _phaseEndUtc = DateTimeOffset.UtcNow.AddSeconds(_remainingSeconds);
        _isRunning = true;
        _tickTimer?.Dispose();
        _tickTimer = new Timer(_ => OnTimerTick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void StopTimerLocked()
    {
        _isRunning = false;
        _tickTimer?.Dispose();
        _tickTimer = null;
    }

    private void SyncRemainingFromClockLocked()
    {
        if (!_isRunning)
        {
            return;
        }

        double secondsLeft = (_phaseEndUtc - DateTimeOffset.UtcNow).TotalSeconds;
        _remainingSeconds = Math.Max(0, (int)Math.Ceiling(secondsLeft));
    }

    private void AdvancePhaseLocked(bool completedByTimer)
    {
        if (_phase == PomodoroPhase.Focus)
        {
            if (completedByTimer)
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                _dailyCounts[today] = _dailyCounts.TryGetValue(today, out int cur) ? cur + 1 : 1;
                PruneOldCountsLocked(today);
                SaveConfigLocked();
            }

            _phase = _sessionIndex >= _sessionsPerCycle
                ? PomodoroPhase.LongBreak
                : PomodoroPhase.ShortBreak;
        }
        else if (_phase == PomodoroPhase.ShortBreak)
        {
            _sessionIndex = Math.Min(_sessionsPerCycle, _sessionIndex + 1);
            _phase = PomodoroPhase.Focus;
        }
        else
        {
            _sessionIndex = 1;
            _phase = PomodoroPhase.Focus;
        }

        _totalSeconds = GetPhaseDurationSecondsLocked(_phase);
        _remainingSeconds = _totalSeconds;
    }

    private int GetPhaseDurationSecondsLocked(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.Focus => _focusMinutes * 60,
        PomodoroPhase.ShortBreak => _shortBreakMinutes * 60,
        _ => _longBreakMinutes * 60,
    };

    private PomodoroSnapshot BuildSnapshotLocked()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        int todaySessions = _dailyCounts.TryGetValue(today, out int t) ? t : 0;

        var days = new List<PomodoroDayCount>(7);
        for (int offset = 6; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);
            string label = date.ToString("ddd", CultureInfo.InvariantCulture);
            int count = _dailyCounts.TryGetValue(date, out int c) ? c : 0;
            days.Add(new PomodoroDayCount(date, label, count, IsToday: offset == 0));
        }

        return new PomodoroSnapshot(
            Phase: _phase,
            SessionIndex: _sessionIndex,
            SessionsPerCycle: _sessionsPerCycle,
            RemainingSeconds: _remainingSeconds,
            TotalSeconds: _totalSeconds,
            IsRunning: _isRunning,
            FocusMinutes: _focusMinutes,
            ShortBreakMinutes: _shortBreakMinutes,
            LongBreakMinutes: _longBreakMinutes,
            NotifyOnPhaseEnd: _notifyOnPhaseEnd,
            TodaySessions: todaySessions,
            Last7Days: days);
    }

    private void PruneOldCountsLocked(DateOnly today)
    {
        var cutoff = today.AddDays(-30);
        var toRemove = _dailyCounts.Keys.Where(d => d < cutoff).ToList();
        foreach (var key in toRemove)
        {
            _dailyCounts.Remove(key);
        }
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            string json = File.ReadAllText(_filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (DockPulseHelpers.TryReadInt(root, "focusMinutes", out int fm))
            {
                _focusMinutes = Math.Clamp(fm, 1, 120);
            }

            if (DockPulseHelpers.TryReadInt(root, "shortBreakMinutes", out int sb))
            {
                _shortBreakMinutes = Math.Clamp(sb, 1, 60);
            }

            if (DockPulseHelpers.TryReadInt(root, "longBreakMinutes", out int lb))
            {
                _longBreakMinutes = Math.Clamp(lb, 1, 60);
            }

            if (DockPulseHelpers.TryReadInt(root, "sessionsPerCycle", out int spc))
            {
                _sessionsPerCycle = Math.Clamp(spc, 1, 8);
            }

            if (DockPulseHelpers.TryReadBool(root, "notifyOnPhaseEnd", out bool notify))
            {
                _notifyOnPhaseEnd = notify;
            }

            if (root.TryGetProperty("history", out var histElem) && histElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in histElem.EnumerateObject())
                {
                    if (DateOnly.TryParseExact(prop.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                        DockPulseHelpers.TryReadInt(histElem, prop.Name, out int count) &&
                        count > 0)
                    {
                        _dailyCounts[date] = count;
                    }
                }
            }
        }
        catch
        {
            // Keep default settings if config file is unreadable
        }
    }

    private void SaveConfigLocked()
    {
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("focusMinutes", _focusMinutes);
                writer.WriteNumber("shortBreakMinutes", _shortBreakMinutes);
                writer.WriteNumber("longBreakMinutes", _longBreakMinutes);
                writer.WriteNumber("sessionsPerCycle", _sessionsPerCycle);
                writer.WriteBoolean("notifyOnPhaseEnd", _notifyOnPhaseEnd);
                writer.WriteStartObject("history");
                foreach (var kvp in _dailyCounts.OrderBy(k => k.Key))
                {
                    writer.WriteNumber(kvp.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), kvp.Value);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            File.WriteAllBytes(_filePath, stream.ToArray());
        }
        catch
        {
            // Ignore write errors
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            StopTimerLocked();
        }
    }
}
