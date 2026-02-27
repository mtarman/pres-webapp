using System.Globalization;
using PresAnalysis.Models;

namespace PresAnalysis.Services;

public class CsvDataService
{
    private const string AutoLoadPath = @"C:\Users\mll_admin\Documents\pres\presence_log.csv";

    private List<(DateTime Ts, int PollMinutes, string UserId, string Availability, string Activity)>? _logCache;
    private string? _lastFilePath;

    public bool HasData => _logCache is { Count: > 0 };
    public bool CanReload => _lastFilePath != null;

    public event Action? DataChanged;

    public CsvDataService()
    {
        if (File.Exists(AutoLoadPath))
            LoadFromPath(AutoLoadPath);

        // Auto-refresh every 2 minutes
        var timer = new System.Timers.Timer(TimeSpan.FromMinutes(2).TotalMilliseconds);
        timer.Elapsed += (_, _) => { try { Reload(); } catch { /* file temporarily unavailable */ } };
        timer.AutoReset = true;
        timer.Start();
    }

    public void LoadFromPath(string path)
    {
        _logCache = ParseRows(File.ReadAllText(path));
        _lastFilePath = path;
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
        _logCache = ParseRows(csvContent);
        DataChanged?.Invoke();
    }

    private static List<(DateTime Ts, int PollMinutes, string UserId, string Availability, string Activity)> ParseRows(
        string csvContent)
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

            result.Add((ts,
                int.Parse(cols[headers["poll_minutes"]]),
                cols[headers["user_id"]],
                cols[headers["availability"]],
                cols[headers["activity"]]
            ));
        }

        return result;
    }

    private List<(DateTime Ts, int PollMinutes, string UserId, string Availability, string Activity)> Rows()
        => _logCache ?? new();

    public IReadOnlyList<string> GetAvailableDates()
        => Rows()
              .Select(r => r.Ts.ToString("yyyy-MM-dd"))
              .Distinct()
              .OrderBy(d => d)
              .ToList();

    public IReadOnlyList<DailyPresenceRecord> GetRecordsForDate(string date)
        => Aggregate(Rows().Where(r => r.Ts.ToString("yyyy-MM-dd") == date), date);

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
        => Rows()
            .Where(r => r.Ts.ToString("yyyy-MM-dd") == date)
            .Select(r => r.Ts)
            .Distinct()
            .Count() * 2;

    public IReadOnlyList<(DateTime Ts, int PollMinutes, string UserId, string Availability)>
        GetTimelineForDate(string date)
        => Rows()
            .Where(r => r.Ts.ToString("yyyy-MM-dd") == date)
            .Select(r => (r.Ts, r.PollMinutes, r.UserId, r.Availability))
            .ToList();

    public IReadOnlyList<TimeRangeRecord> GetProductiveByRange(string date)
        => ProductiveByRangeCore(Rows().Where(r => r.Ts.ToString("yyyy-MM-dd") == date));

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
        => Rows()
              .Select(r => WeekKey(r.Ts))
              .Distinct()
              .OrderBy(w => w)
              .ToList();

    public IReadOnlyList<DailyPresenceRecord> GetRecordsForWeek(string weekKey)
        => Aggregate(Rows().Where(r => WeekKey(r.Ts) == weekKey), weekKey);

    public int GetTotalMinutesForWeek(string weekKey)
        => Rows()
              .Where(r => WeekKey(r.Ts) == weekKey)
              .Select(r => r.Ts)
              .Distinct()
              .Count() * 2;

    public IReadOnlyList<TimeRangeRecord> GetProductiveByRangeForWeek(string weekKey)
        => ProductiveByRangeCore(Rows().Where(r => WeekKey(r.Ts) == weekKey));

    public IReadOnlyList<string> GetAvailableMonths()
        => Rows()
              .Select(r => r.Ts.ToString("yyyy-MM"))
              .Distinct()
              .OrderBy(m => m)
              .ToList();

    public IReadOnlyList<DailyPresenceRecord> GetRecordsForMonth(string monthKey)
        => Aggregate(Rows().Where(r => r.Ts.ToString("yyyy-MM") == monthKey), monthKey);

    public int GetTotalMinutesForMonth(string monthKey)
        => Rows()
              .Where(r => r.Ts.ToString("yyyy-MM") == monthKey)
              .Select(r => r.Ts)
              .Distinct()
              .Count() * 2;

    public IReadOnlyList<TimeRangeRecord> GetProductiveByRangeForMonth(string monthKey)
        => ProductiveByRangeCore(Rows().Where(r => r.Ts.ToString("yyyy-MM") == monthKey));
}
