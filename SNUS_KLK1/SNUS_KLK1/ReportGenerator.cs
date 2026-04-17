using System.Xml.Linq;
using SNUS_KLK1.models;

namespace SNUS_KLK1;
public static class ReportGenerator
{
    private const int MaxReportsOnDisk = 5;
    private const string ReportFilePrefix = "report_";

    // ── Public API ───────────────────────────────────────────────────────────

    public static void GenerateReport(IEnumerable<JobExecutionInfo> history, string folderPath)
    {
        Directory.CreateDirectory(folderPath);
        RotateOldReports(folderPath);

        var snapshot = history.ToList();
        var document = BuildReportDocument(snapshot);

        string filePath = Path.Combine(folderPath, $"{ReportFilePrefix}{DateTime.Now:yyyyMMdd_HHmmss}.xml");
        document.Save(filePath);
    }

    // ── Document construction ────────────────────────────────────────────────

    private static XDocument BuildReportDocument(List<JobExecutionInfo> snapshot)
    {
        return new XDocument(
            new XElement("Report",
                new XElement("GeneratedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                BuildSuccessCountSection(snapshot),
                BuildAverageDurationSection(snapshot),
                BuildFailureCountSection(snapshot)
            )
        );
    }

    private static XElement BuildSuccessCountSection(List<JobExecutionInfo> snapshot)
    {
        var entries = snapshot
            .Where(x => x.Success)
            .GroupBy(x => x.Type)
            .OrderBy(g => g.Key)
            .Select(g => JobTypeElement(g.Key, ("Count", g.Count())));

        return new XElement("ExecutedJobsByType", entries);
    }

    private static XElement BuildAverageDurationSection(List<JobExecutionInfo> snapshot)
    {
        var entries = snapshot
            .Where(x => x.Success)
            .GroupBy(x => x.Type)
            .OrderBy(g => g.Key)
            .Select(g => JobTypeElement(g.Key, ("AverageMilliseconds", g.Average(x => x.Duration.TotalMilliseconds))));

        return new XElement("AverageExecutionTimeByType", entries);
    }

    private static XElement BuildFailureCountSection(List<JobExecutionInfo> snapshot)
    {
        var entries = snapshot
            .Where(x => !x.Success)
            .GroupBy(x => x.Type)
            .OrderBy(g => g.Key)
            .Select(g => JobTypeElement(g.Key, ("Count", g.Count())));

        return new XElement("FailedJobsByType", entries);
    }

    private static XElement JobTypeElement(JobType type, (string Name, object Value) attribute)
    {
        return new XElement("JobType",
            new XAttribute("Type", type),
            new XAttribute(attribute.Name, attribute.Value));
    }

    // ── Report rotation ───────────────────────────────────────────────────────

    private static void RotateOldReports(string folderPath)
    {
        var toDelete = new DirectoryInfo(folderPath)
            .GetFiles($"{ReportFilePrefix}*.xml")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Skip(MaxReportsOnDisk - 1);

        foreach (var file in toDelete)
            file.Delete();
    }
}
