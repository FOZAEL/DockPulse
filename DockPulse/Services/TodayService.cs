using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using DockPulse.Helpers;

namespace DockPulse.Services;

public sealed record TodayChecklistItem(int Id, string Title, bool Done);

public sealed record TodaySnapshot(
    DateOnly Date,
    int Water,
    int WaterGoal,
    int CoffeeTimeMinutes,
    bool CoffeeDone,
    bool IsCoffeeDue,
    int DoneCount,
    int TotalCount,
    IReadOnlyList<TodayChecklistItem> Items);

public sealed class TodayService
{
    private readonly object _lock = new();
    private readonly string _filePath;

    private DateOnly _date;
    private int _nextId = 1;
    private int _water;
    private int _waterGoal = 8;
    private int _coffeeTimeMinutes = 10 * 60;
    private bool _coffeeDone;
    private string _lastCoffeeNotifiedDate = string.Empty;
    private List<TodayChecklistItem> _items = [];

    public event Action? StateChanged;

    public TodayService()
    {
        _filePath = DockPulseHelpers.GetSettingsFilePath("today.json");
        _date = DateOnly.FromDateTime(DateTime.Now);

        LoadOrInitialize();
        EnsureCurrentDay();
    }

    public TodaySnapshot GetSnapshot()
    {
        bool changed;
        TodaySnapshot snapshot;
        lock (_lock)
        {
            changed = EnsureCurrentDayLocked();
            snapshot = BuildSnapshotLocked();
        }

        if (changed)
        {
            StateChanged?.Invoke();
        }

        return snapshot;
    }

    public bool EnsureCurrentDay()
    {
        bool changed;
        lock (_lock)
        {
            changed = EnsureCurrentDayLocked();
        }

        if (changed)
        {
            StateChanged?.Invoke();
        }

        return changed;
    }

    public void CheckAndSendCoffeeNotification()
    {
        bool changed = false;
        int notifyCoffeeMinutes = -1;

        lock (_lock)
        {
            changed = EnsureCurrentDayLocked();

            var now = DateTime.Now;
            int nowMinutes = (now.Hour * 60) + now.Minute;
            string todayKey = _date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            if (!_coffeeDone &&
                nowMinutes >= _coffeeTimeMinutes &&
                !string.Equals(_lastCoffeeNotifiedDate, todayKey, StringComparison.Ordinal))
            {
                _lastCoffeeNotifiedDate = todayKey;
                SaveLocked();
                notifyCoffeeMinutes = _coffeeTimeMinutes;
            }
        }

        if (changed)
        {
            StateChanged?.Invoke();
        }

        if (notifyCoffeeMinutes >= 0)
        {
            WindowsNotificationHelper.ShowToast(
                $"Coffee Time — {FormatMinutes(notifyCoffeeMinutes)}",
                "Time for your daily coffee! Check it off in DockPulse Today.");
        }
    }

    public int AddWater(int delta)
    {
        int newWater;
        lock (_lock)
        {
            EnsureCurrentDayLocked();
            _water = Math.Max(0, _water + delta);
            newWater = _water;
            SaveLocked();
        }

        StateChanged?.Invoke();
        return newWater;
    }

    public void ResetWater()
    {
        lock (_lock)
        {
            EnsureCurrentDayLocked();
            _water = 0;
            SaveLocked();
        }

        StateChanged?.Invoke();
    }

    public bool ToggleCoffee()
    {
        bool done;
        lock (_lock)
        {
            EnsureCurrentDayLocked();
            _coffeeDone = !_coffeeDone;
            done = _coffeeDone;
            SaveLocked();
        }

        StateChanged?.Invoke();
        return done;
    }

    public bool ToggleItem(int id)
    {
        bool found = false;
        lock (_lock)
        {
            EnsureCurrentDayLocked();
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Id == id)
                {
                    _items[i] = _items[i] with { Done = !_items[i].Done };
                    found = true;
                    break;
                }
            }

            if (found)
            {
                SaveLocked();
            }
        }

        if (found)
        {
            StateChanged?.Invoke();
        }

        return found;
    }

    public TodayChecklistItem? TryGetItem(int id)
    {
        lock (_lock)
        {
            EnsureCurrentDayLocked();
            return _items.FirstOrDefault(x => x.Id == id);
        }
    }

    public TodayChecklistItem? AddItem(string title)
    {
        string trimmed = title.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        TodayChecklistItem added;
        lock (_lock)
        {
            EnsureCurrentDayLocked();
            added = new TodayChecklistItem(_nextId++, trimmed, Done: false);
            _items.Add(added);
            SaveLocked();
        }

        StateChanged?.Invoke();
        return added;
    }

    public bool EditItem(int id, string newTitle)
    {
        string trimmed = newTitle.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        bool found = false;
        lock (_lock)
        {
            EnsureCurrentDayLocked();
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Id == id)
                {
                    _items[i] = _items[i] with { Title = trimmed };
                    found = true;
                    break;
                }
            }

            if (found)
            {
                SaveLocked();
            }
        }

        if (found)
        {
            StateChanged?.Invoke();
        }

        return found;
    }

    public string? RemoveItem(int id)
    {
        string? removedTitle = null;
        lock (_lock)
        {
            EnsureCurrentDayLocked();
            int idx = _items.FindIndex(x => x.Id == id);
            if (idx >= 0)
            {
                removedTitle = _items[idx].Title;
                _items.RemoveAt(idx);
                SaveLocked();
            }
        }

        if (removedTitle is not null)
        {
            StateChanged?.Invoke();
        }

        return removedTitle;
    }

    public void UpdateSettings(int coffeeTimeMinutes, int waterGoal)
    {
        lock (_lock)
        {
            EnsureCurrentDayLocked();
            _coffeeTimeMinutes = Math.Clamp(coffeeTimeMinutes, 0, (24 * 60) - 1);
            _waterGoal = Math.Clamp(waterGoal, 1, 20);
            SaveLocked();
        }

        StateChanged?.Invoke();
    }

    public void ResetForNewDay()
    {
        lock (_lock)
        {
            _date = DateOnly.FromDateTime(DateTime.Now);
            ResetDailyProgressLocked();
            SaveLocked();
        }

        StateChanged?.Invoke();
    }

    public static string FormatMinutes(int totalMinutes)
    {
        int wrapped = ((totalMinutes % 1440) + 1440) % 1440;
        return $"{wrapped / 60:00}:{wrapped % 60:00}";
    }

    public static bool TryParseHhMm(string? value, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (TimeOnly.TryParseExact(value.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            minutes = (t.Hour * 60) + t.Minute;
            return true;
        }

        return false;
    }

    private bool EnsureCurrentDayLocked()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today == _date)
        {
            return false;
        }

        _date = today;
        ResetDailyProgressLocked();
        SaveLocked();
        return true;
    }

    private void ResetDailyProgressLocked()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            _items[i] = _items[i] with { Done = false };
        }

        _coffeeDone = false;
        _water = 0;
    }

    private TodaySnapshot BuildSnapshotLocked()
    {
        var now = DateTime.Now;
        int nowMinutes = (now.Hour * 60) + now.Minute;
        bool isCoffeeDue = !_coffeeDone && nowMinutes >= _coffeeTimeMinutes;

        int doneCount = _items.Count(x => x.Done) + (_coffeeDone ? 1 : 0);
        int totalCount = _items.Count + 1;

        return new TodaySnapshot(
            Date: _date,
            Water: _water,
            WaterGoal: _waterGoal,
            CoffeeTimeMinutes: _coffeeTimeMinutes,
            CoffeeDone: _coffeeDone,
            IsCoffeeDue: isCoffeeDue,
            DoneCount: doneCount,
            TotalCount: totalCount,
            Items: [.. _items]);
    }

    private void LoadOrInitialize()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                SeedDefaultItemsLocked();
                SaveLocked();
                return;
            }

            string json = File.ReadAllText(_filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                SeedDefaultItemsLocked();
                SaveLocked();
                return;
            }

            if (DockPulseHelpers.TryReadString(root, "date", out var dateStr) &&
                DateOnly.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                _date = parsedDate;
            }

            if (DockPulseHelpers.TryReadInt(root, "water", out int water))
            {
                _water = Math.Max(0, water);
            }

            if (DockPulseHelpers.TryReadInt(root, "waterGoal", out int waterGoal))
            {
                _waterGoal = Math.Clamp(waterGoal, 1, 20);
            }

            if (DockPulseHelpers.TryReadInt(root, "coffeeTimeMinutes", out int coffeeMins))
            {
                _coffeeTimeMinutes = Math.Clamp(coffeeMins, 0, (24 * 60) - 1);
            }

            if (DockPulseHelpers.TryReadBool(root, "coffeeDone", out bool coffeeDone))
            {
                _coffeeDone = coffeeDone;
            }

            if (DockPulseHelpers.TryReadString(root, "lastCoffeeNotifiedDate", out var lastNotif))
            {
                _lastCoffeeNotifiedDate = lastNotif;
            }

            if (root.TryGetProperty("items", out var itemsElem) && itemsElem.ValueKind == JsonValueKind.Array)
            {
                var loaded = new List<TodayChecklistItem>();
                int maxId = 0;

                foreach (var elem in itemsElem.EnumerateArray())
                {
                    if (elem.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!DockPulseHelpers.TryReadString(elem, "title", out var title) || string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    int id = DockPulseHelpers.TryReadInt(elem, "id", out int parsedId) && parsedId > 0
                        ? parsedId
                        : maxId + 1;
                    bool done = DockPulseHelpers.TryReadBool(elem, "done", out bool parsedDone) && parsedDone;

                    maxId = Math.Max(maxId, id);
                    loaded.Add(new TodayChecklistItem(id, title.Trim(), done));
                }

                _items = loaded;
                _nextId = DockPulseHelpers.TryReadInt(root, "nextId", out int savedNextId)
                    ? Math.Max(savedNextId, maxId + 1)
                    : maxId + 1;
            }
            else
            {
                SeedDefaultItemsLocked();
            }
        }
        catch
        {
            SeedDefaultItemsLocked();
        }
    }

    private void SeedDefaultItemsLocked()
    {
        _items =
        [
            new TodayChecklistItem(1, "Morning walk", false),
            new TodayChecklistItem(2, "Take vitamins", false),
            new TodayChecklistItem(3, "Read 20 pages", false),
            new TodayChecklistItem(4, "Inbox zero", false),
            new TodayChecklistItem(5, "Stretch 10 min", false),
        ];
        _nextId = 6;
        _water = 0;
        _waterGoal = 8;
        _coffeeTimeMinutes = 10 * 60;
        _coffeeDone = false;
    }

    private void SaveLocked()
    {
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("date", _date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                writer.WriteNumber("nextId", _nextId);
                writer.WriteNumber("water", _water);
                writer.WriteNumber("waterGoal", _waterGoal);
                writer.WriteNumber("coffeeTimeMinutes", _coffeeTimeMinutes);
                writer.WriteBoolean("coffeeDone", _coffeeDone);
                writer.WriteString("lastCoffeeNotifiedDate", _lastCoffeeNotifiedDate);
                writer.WriteStartArray("items");
                foreach (var item in _items)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", item.Id);
                    writer.WriteString("title", item.Title);
                    writer.WriteBoolean("done", item.Done);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            File.WriteAllBytes(_filePath, stream.ToArray());
        }
        catch
        {
            // Ignore write errors
        }
    }
}
