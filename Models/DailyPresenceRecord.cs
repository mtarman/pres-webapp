namespace PresAnalysis.Models;

public class DailyPresenceRecord
{
    public string UserId { get; set; } = "";
    public string DateLocal { get; set; } = "";
    public int TotalMinutes { get; set; }
    public int Available { get; set; }
    public int Away { get; set; }
    // Busy total = sum of all four subcategories below
    public int Busy { get; set; }
    public int BusyInCall { get; set; }
    public int BusyInConference { get; set; }
    public int BusyInMeeting { get; set; }
    public int BusyOther { get; set; }
    public int DoNotDisturb { get; set; }
    public int Offline { get; set; }
    public int Unknown { get; set; }
}
