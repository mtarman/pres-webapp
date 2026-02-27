namespace PresAnalysis.Models;

public class TimeRangeRecord
{
    public string UserId { get; set; } = "";
    public int Dawn { get; set; }      // 12am – 6am
    public int Business { get; set; }  // 6am  – 6pm
    public int Evening { get; set; }   // 6pm  – 12am
}
