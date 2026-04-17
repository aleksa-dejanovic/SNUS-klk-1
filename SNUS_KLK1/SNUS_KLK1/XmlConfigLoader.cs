using System.Xml.Linq;
using SNUS_KLK1.models;

namespace SNUS_KLK1;

public static class XmlConfigLoader
{
    public static (SystemConfig Config, List<Job> Jobs) Load(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Element("SystemConfig")
            ?? throw new FormatException("Missing root element: SystemConfig.");

        int workerCount = int.Parse(root.Element("WorkerCount")?.Value
            ?? throw new FormatException("Missing WorkerCount element."));

        int maxQueueSize = int.Parse(root.Element("MaxQueueSize")?.Value
            ?? throw new FormatException("Missing MaxQueueSize element."));

        var config = new SystemConfig
        {
            WorkerCount = workerCount,
            MaxQueueSize = maxQueueSize
        };

        var jobs = new List<Job>();

        var jobsElement = root.Element("Jobs");
        if (jobsElement == null) return (config, jobs);
        foreach (var jobElement in jobsElement.Elements("Job"))
        {
            var typeText = jobElement.Attribute("Type")?.Value
                              ?? throw new FormatException("Job is missing Type attribute.");

            var payload = jobElement.Attribute("Payload")?.Value
                          ?? throw new FormatException("Job is missing Payload attribute.");

            var priorityText = jobElement.Attribute("Priority")?.Value
                               ?? throw new FormatException("Job is missing Priority attribute.");

            if (!Enum.TryParse<JobType>(typeText, true, out var type))
                throw new FormatException($"Unknown JobType: {typeText}");

            if (!int.TryParse(priorityText, out int priority))
                throw new FormatException($"Invalid Priority value: {priorityText}");

            jobs.Add(new Job
            {
                Id = Guid.NewGuid(),
                Type = type,
                Payload = payload,
                Priority = priority
            });
        }

        return (config, jobs);
    }
}