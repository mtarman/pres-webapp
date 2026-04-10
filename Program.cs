using PresAnalysis;
using PresAnalysis.Models;
using PresAnalysis.Services;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;

var builder = WebApplication.CreateBuilder(args);
StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<CsvDataService>();
var app = builder.Build();
app.UsePathBase("/pres");
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapPost("/api/upload-csv", async (HttpRequest request, CsvDataService dataService) =>
{
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null) return Results.Redirect("/load");
    using var reader = new StreamReader(file.OpenReadStream());
    var content = await reader.ReadToEndAsync();
    dataService.LoadFromCsv(content);
    return Results.Redirect("/");
}).DisableAntiforgery();

app.MapGet("/api/export-range", (string? from, string? to, CsvDataService dataService) =>
{
    if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || !dataService.HasData)
        return Results.NoContent();

    var records = dataService.GetRecordsForRange(from, to).ToList();
    var dates   = records.Select(r => r.DateLocal).Distinct().OrderBy(d => d).ToList();
    var users   = records.Select(r => r.UserId).Distinct().OrderBy(u => ExportInitials(u)).ToList();
    var cells   = records.ToDictionary(r => (r.UserId, r.DateLocal));

    using var wb = new XLWorkbook();
    var ws = wb.Worksheets.Add("Range");

    ws.Cell(1, 1).Value = "User";
    for (int c = 0; c < dates.Count; c++)
    {
        var cell = ws.Cell(1, c + 2);
        cell.Value = dates[c];
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        cell.Style.Font.Bold = true;
    }
    ws.Row(1).Style.Font.Bold = true;
    ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromArgb(0x21, 0x25, 0x29);
    ws.Row(1).Style.Font.FontColor = XLColor.White;

    for (int r = 0; r < users.Count; r++)
    {
        var user = users[r];
        ws.Cell(r + 2, 1).Value = ExportInitials(user);
        ws.Cell(r + 2, 1).Style.Font.Bold = true;

        for (int c = 0; c < dates.Count; c++)
        {
            var exCell = ws.Cell(r + 2, c + 2);
            exCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            if (cells.TryGetValue((user, dates[c]), out var rec))
            {
                var prod    = rec.Available + rec.Busy + rec.DoNotDisturb;
                var nonProd = rec.Away + rec.Offline + rec.Unknown;
                exCell.Value = $"{ExportFmt(prod)} / {ExportFmt(nonProd)}";
                exCell.Style.Fill.BackgroundColor = prod >= nonProd
                    ? XLColor.FromArgb(0xd1, 0xec, 0xe1)
                    : XLColor.FromArgb(0xf8, 0xd7, 0xda);
            }
            else
            {
                exCell.Value = "—";
            }
        }
    }

    ws.Columns().AdjustToContents();

    using var ms = new MemoryStream();
    wb.SaveAs(ms);
    return Results.File(ms.ToArray(),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        $"presence_range_{from}_to_{to}.xlsx");
});

app.Run();

static string ExportInitials(string userId) => userId switch
{
    "52333526-20d7-4213-af27-447d72d070c3" => "AG",
    "171f480f-cf1c-4fed-9a78-7057b936792f" => "CC",
    "a166b56d-06d1-409c-a926-b91f2c2f3d51" => "EMS",
    "ef4b1bb3-5bec-44ad-ac91-0d98c6cc4cc5" => "GS",
    "5e356082-ef1a-429b-a389-f0ef66eaae37" => "JAG",
    "a78c4c10-ac09-49a2-82fe-6b519231f521" => "JD",
    "9df94eb6-73d2-4736-8d7c-1c4de0bfbdc8" => "LER",
    "5aa20ed9-a241-4ed5-9d2e-94604b455877" => "MMT",
    "4c97f094-fc22-4814-84c2-802abd80b300" => "RJO",
    "cd0f3b55-257d-4c5d-9bdc-9c6a0905c6fb" => "VA",
    "aa075bf8-f526-4512-a779-98b73c6baa39" => "MV",
    "2f711f1e-785b-4b25-bb56-49dc32a72338" => "LC",
    _ => userId[..8]
};

static string ExportFmt(int minutes)
{
    if (minutes == 0) return "0m";
    var h = minutes / 60;
    var m = minutes % 60;
    if (h == 0) return $"{m}m";
    if (m == 0) return $"{h}h";
    return $"{h}h {m}m";
}
