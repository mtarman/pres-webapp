using System.Globalization;
using PresAnalysis.Models;

namespace PresAnalysis.Services;

public class CsvDataService
{
    private const string AutoLoadPath = @"C:\Users\mll_admin\Documents\pres\presence_log.csv";
    private const string DevelopmentAutoLoadPath = @"D:\blazorcode\data\presence_log.csv";

    private readonly object _cacheLock = new();
    private readonly PersistentSettingsService _settings;
    private List<(DateTime Ts, int PollMinutes, string UserId, string Availability, string Activity)>? _logCache;
    private HashSet<string> _availableDates = new(StringComparer.Ordinal);
    private HashSet<string> _availableUsers = new(StringComparer.Ordinal);
    private HashSet<string> _loadedMonths = new(StringComparer.Ordinal);
    private string? _lastFilePath;
    private string? _uploadedCsvContent;

    public bool HasData
    {
        get { lock (_cacheLock) return _availableDates.Count > 0; }
    }

    public bool CanReload => _lastFilePath != null;

    public event Action? DataChanged;

    public CsvDataService(PersistentSettingsService settings)
    {
        _settings = settings;
        var initialPath = ResolveAutoLoadPath();
        if (initialPath != null)
            LoadFromPath(initialPath);

        // Auto-refresh every 2 minutes
        var timer = new System.Timers.Timer(TimeSpan.FromMinutes(2).TotalMilliseconds);
        timer.Elapsed += (_, _) => { try { Reload(); } catch { /* file temporarily unavailable */ } };
        timer.AutoReset = true;
        timer.Start();
    }

    private static string? ResolveAutoLoadPath()
    {
        if (File.Exists(AutoLoadPath))
            return AutoLoadPath;

        if (File.Exists(DevelopmentAutoLoadPath))
            return DevelopmentAutoLoadPath;

        return null;
    }

    public void LoadFromPath(string path)
    {
        InitializeSource(File.ReadAllText(path), path, uploadedCsvContent: null);
    }

    public void Reload()
    {
        if (_lastFilePath == null) return;
        LoadFromPath(_lastFilePath);
        DataChanged?.Invoke();
    }

    // Called when the user picks a CSV with no known path (content-only fallback)
    public void LoadFromCsv(string csvContent)
    {
        InitializeSource(csvContent, filePath: null, uploadedCsvContent: csvContent);
        DataChanged?.Invoke();
    }

    private void InitializeSource(string csvContent, string? filePath, string? uploadedCsvContent)
    {
        var currentMonth = DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var availableDates = new HashSet<string>(StringComparer.Ordinal);
        var availableUsers = new HashSet<string>(StringComparer.Ordinal);
        var currentRows = ParseRows(
            csvContent,
            new HashSet<string>([currentMonth], StringComparer.Ordinal),
            availableDates,
            availableUsers);

        lock (_cacheLock)
        {
            _logCache = currentRows;
            _availableDates = availableDates;
            _availableUsers = availableUsers;
            _loadedMonths = [currentMonth];
            _lastFilePath = filePath;
            _uploadedCsvContent = uploadedCsvContent;
        }
    }

    private static List<(DateTime Ts, int PollMinutes, string UserId, string Availability, string Activity)> ParseRows(
        string csvContent,
        IReadOnlySet<string>? monthsToLoad = null,
        ISet<string>? availableDates = null,
        ISet<string>? availableUsers = null)
    {
        var lines = csvContent.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        if (lines.Length < 2) return new();

        var headers = lines[0].Split(',')
            .Select((h, i) => (Name: h.Trim(), Index: i))
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        // Accept any of these timestamp column names
        var tsCol = new[] { "ts_utc", "ts_ct", "ts_local", "ts", "timestamp" }
            .FirstOrDefault(headers.ContainsKey)
            ?? throw new InvalidOperationException(
                $"Cannot find timestamp column. Headers found: {string.Join(", ", headers.Keys)}");

        var result = new List<(DateTime, int, string, string, string)>();

        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = line.Split(',').Select(c => c.Trim('"')).ToArray();

            var ts = DateTime.Parse(cols[headers[tsCol]], null, DateTimeStyles.AssumeLocal);
            var dateKey = ts.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            availableDates?.Add(dateKey);
            availableUsers?.Add(cols[headers["user_id"]]);

            if (monthsToLoad != null && !monthsToLoad.Contains(dateKey[..7]))
                continue;

            result.Add((ts,
                int.Parse(cols[headers["poll_minutes"]]),
                cols[headers["user_id"]],
                cols[headers["availability"]],
                cols[headers["activity"]]
            ));
        }

        return result;
    }

    private void EnsureMonthsLoaded(IEnumerable<string> monthKeys)
    {
        lock (_cacheLock)
        {
            var missing = monthKeys
                .Where(IsMonthKey)
                .Where(month => !_loadedMonths.Contains(month))
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);

            if (missing.Count == 0) return;

            var csvContent = _uploadedCsvContent;
            if (csvContent == null && _lastFilePath != null)
                csvContent = File.ReadAllText(_lastFilePath);

            if (csvContent != null)
                _logCache!.AddRange(ParseRows(csvContent, missing));

            _loadedMonths.UnionWith(missing);
        }
    }

    private static bool IsMonthKey(string value)
        => DateTime.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _);

    private void EnsureDateLoaded(string date)
    {
        if (date.Length >= 7)
            EnsureMonthsLoaded([date[..7]]);
    }

    private void EnsureWeekLoaded(string weekKey)
    {
        if (!DateTime.TryParseExact(weekKey, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var monday))
            return;

        EnsureMonthsLoaded([
            monday.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            monday.AddDays(6).ToString("yyyy-MM", CultureInfo.InvariantCulture)
        ]);
    }

    private void EnsureRangeLoaded(string fromDate, string toDate)
    {
        if (!DateTime.TryParseExact(fromDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var from)
            || !DateTime.TryParseExact(toDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var to)
            || from > to)
            return;

        var months = new List<string>();
        for (var month = new DateTime(from.Year, from.Month, 1);
             month <= new DateTime(to.Year, to.Month, 1);
             month = month.AddMonths(1))
        {
            months.Add(month.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        }

        EnsureMonthsLoaded(months);
    }

    private List<(DateTime Ts, int PollMinutes, string UserId, string Availability, string Activity)> Rows()
    {
        lock (_cacheLock)
            return _logCache?
                .Where(row => _settings.IsUserVisible(row.UserId))
                .ToList()
                ?? new();
    }

    public IReadOnlyList<string> GetAvailableDates()
    {
        lock (_cacheLock)
            return _availableDates.OrderBy(d => d).ToList();
    }

    public IReadOnlyList<string> GetAvailableUsers()
    {
        lock (_cacheLock)
            return _availableUsers.OrderBy(userId => userId).ToList();
    }

    public IReadOnlyList<DailyPresenceRecord> GetRecordsForDate(string date)
    {
        EnsureDateLoaded(date);
        return Aggregate(Rows().Where(r => r.Ts.ToString("yyyy-MM-dd") == date), date);
    }

    private static IReadOnlyList<DailyPresenceRecord> Aggregate(
        IEnumerable<(DateTime Ts, int PollMinutes, string UserId, string Availability, string Activity)> rows,
        string dateLabel)
    {
        return rows
            .GroupBy(r => r.UserId)
            .Select(g =>
            {
                var available  = g.Where(r => r.Availability is "Available" or "AvailableIdle")
                                  .Sum(r => r.PollMinutes);

                var away       = g.Where(r => r.Availability == "Away")
                                  .Sum(r => r.PollMinutes);

                var busyRows   = g.Where(r => r.Availability is "Busy" or "BusyIdle").ToList();
                var inCall     = busyRows.Where(r => r.Activity == "InACall")
                                         .Sum(r => r.PollMinutes);
                var inConf     = busyRows.Where(r => r.Activity == "InAConferenceCall")
                                         .Sum(r => r.PollMinutes);
                var inMeeting  = busyRows.Where(r => r.Activity == "InAMeeting")
                                         .Sum(r => r.PollMinutes);
                var busyOther  = busyRows.Where(r => r.Activity is not ("InACall" or "InAConferenceCall" or "InAMeeting"))
                                         .Sum(r => r.PollMinutes);
                var busy       = inCall + inConf + inMeeting + busyOther;

                var dnd        = g.Where(r => r.Availability is "DoNotDisturb" or "DoNotDisturbIdle")
                                  .Sum(r => r.PollMinutes);

                var offline    = g.Where(r => r.Availability == "Offline")
                                  .Sum(r => r.PollMinutes);

                var total      = g.Sum(r => r.PollMinutes);
                var unknown    = Math.Max(0, total - available - away - busy - dnd - offline);

                return new DailyPresenceRecord
                {
                    UserId           = g.Key,
                    DateLocal        = dateLabel,
                    TotalMinutes     = total,
                    Available        = available,
                    Away             = away,
                    Busy             = busy,
                    BusyInCall       = inCall,
                    BusyInConference = inConf,
                    BusyInMeeting    = inMeeting,
                    BusyOther        = busyOther,
                    DoNotDisturb     = dnd,
                    Offline          = offline,
                    Unknown          = unknown,
                };
            })
            .ToList();
    }

    public IReadOnlyList<DailyPresenceRecord> GetRecordsForRange(string fromDate, string toDate)
    {
        EnsureRangeLoaded(fromDate, toDate);
        var rows = Rows().Where(r =>
        {
            var d = r.Ts.ToString("yyyy-MM-dd");
            return string.Compare(d, fromDate, StringComparison.Ordinal) >= 0
                && string.Compare(d, toDate,   StringComparison.Ordinal) <= 0;
        });

        return rows
            .GroupBy(r => (r.UserId, Date: r.Ts.ToString("yyyy-MM-dd")))
            .Select(g =>
            {
                var available = g.Where(r => r.Availability is "Available" or "AvailableIdle").Sum(r => r.PollMinutes);
                var away      = g.Where(r => r.Availability == "Away").Sum(r => r.PollMinutes);
                var busyRows  = g.Where(r => r.Availability is "Busy" or "BusyIdle").ToList();
                var busy      = busyRows.Sum(r => r.PollMinutes);
                var dnd       = g.Where(r => r.Availability is "DoNotDisturb" or "DoNotDisturbIdle").Sum(r => r.PollMinutes);
                var offline   = g.Where(r => r.Availability == "Offline").Sum(r => r.PollMinutes);
                var total     = g.Sum(r => r.PollMinutes);
                var unknown   = Math.Max(0, total - available - away - busy - dnd - offline);

                return new DailyPresenceRecord
                {
                    UserId       = g.Key.UserId,
                    DateLocal    = g.Key.Date,
                    TotalMinutes = total,
                    Available    = available,
                    Away         = away,
                    Busy         = busy,
                    DoNotDisturb = dnd,
                    Offline      = offline,
                    Unknown      = unknown,
                };
            })
            .ToList();
    }

    public int GetTotalMinutesForDate(string date)
    {
        EnsureDateLoaded(date);
        return Rows()
            .Where(r => r.Ts.ToString("yyyy-MM-dd") == date)
            .Select(r => r.Ts)
            .Distinct()
            .Count() * 2;
    }

    public IReadOnlyList<(DateTime Ts, int PollMinutes, string UserId, string Availability)>
        GetTimelineForDate(string date)
    {
        EnsureDateLoaded(date);
        return Rows()
            .Where(r => r.Ts.ToString("yyyy-MM-dd") == date)
            .Select(r => (r.Ts, r.PollMinutes, r.UserId, r.Availability))
            .ToList();
    }

    public IReadOnlyList<TimeRangeRecord> GetProductiveByRange(string date)
    {
        EnsureDateLoaded(date);
        return ProductiveByRangeCore(Rows().Where(r => r.Ts.ToString("yyyy-MM-dd") == date));
    }

    private static IReadOnlyList<TimeRangeRecord> ProductiveByRangeCore(
        IEnumerable<(DateTime Ts, int PollMinutes, string UserId, string Availability, string Activity)> source)
    {
        static bool IsProductive(string avail) =>
            avail is "Available" or "AvailableIdle"
                  or "Busy"      or "BusyIdle"
                  or "DoNotDisturb" or "DoNotDisturbIdle";

        static int Bucket(int hour)
        {
            if (hour >= 6 && hour < 18) return 0;
            if (hour >= 18)             return 1;
            return 2;
        }

        return source
            .Where(r => IsProductive(r.Availability))
            .GroupBy(r => r.UserId)
            .Select(g => new TimeRangeRecord
            {
                UserId   = g.Key,
                Business = g.Where(r => Bucket(r.Ts.Hour) == 0).Sum(r => r.PollMinutes),
                Evening  = g.Where(r => Bucket(r.Ts.Hour) == 1).Sum(r => r.PollMinutes),
                Dawn     = g.Where(r => Bucket(r.Ts.Hour) == 2).Sum(r => r.PollMinutes),
            })
            .ToList();
    }

    private static string WeekKey(DateTime dt)
    {
        var offset = ((int)dt.DayOfWeek + 6) % 7;
        return dt.AddDays(-offset).ToString("yyyy-MM-dd");
    }

    public IReadOnlyList<string> GetAvailableWeeks()
        => GetAvailableDates()
              .Select(d => WeekKey(DateTime.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture)))
              .Distinct(StringComparer.Ordinal)
              .OrderBy(w => w)
              .ToList();

    public IReadOnlyList<DailyPresenceRecord> GetRecordsForWeek(string weekKey)
    {
        EnsureWeekLoaded(weekKey);
        return Aggregate(Rows().Where(r => WeekKey(r.Ts) == weekKey), weekKey);
    }

    public int GetTotalMinutesForWeek(string weekKey)
    {
        EnsureWeekLoaded(weekKey);
        return Rows()
              .Where(r => WeekKey(r.Ts) == weekKey)
              .Select(r => r.Ts)
              .Distinct()
              .Count() * 2;
    }

    public IReadOnlyList<TimeRangeRecord> GetProductiveByRangeForWeek(string weekKey)
    {
        EnsureWeekLoaded(weekKey);
        return ProductiveByRangeCore(Rows().Where(r => WeekKey(r.Ts) == weekKey));
    }

    public IReadOnlyList<string> GetAvailableMonths()
        => GetAvailableDates()
              .Select(d => d[..7])
              .Distinct(StringComparer.Ordinal)
              .OrderBy(m => m)
              .ToList();

    public IReadOnlyList<DailyPresenceRecord> GetRecordsForMonth(string monthKey)
    {
        EnsureMonthsLoaded([monthKey]);
        return Aggregate(Rows().Where(r => r.Ts.ToString("yyyy-MM") == monthKey), monthKey);
    }

    public int GetTotalMinutesForMonth(string monthKey)
    {
        EnsureMonthsLoaded([monthKey]);
        return Rows()
              .Where(r => r.Ts.ToString("yyyy-MM") == monthKey)
              .Select(r => r.Ts)
              .Distinct()
              .Count() * 2;
    }

    public IReadOnlyList<TimeRangeRecord> GetProductiveByRangeForMonth(string monthKey)
    {
        EnsureMonthsLoaded([monthKey]);
        return ProductiveByRangeCore(Rows().Where(r => r.Ts.ToString("yyyy-MM") == monthKey));
    }
}
