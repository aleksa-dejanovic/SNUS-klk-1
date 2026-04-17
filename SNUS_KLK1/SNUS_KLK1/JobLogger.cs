using System.Text;

namespace SNUS_KLK1;

public class JobLogger
{
    private readonly string _logFilePath;
    private readonly SemaphoreSlim _logSemaphore = new(1, 1);

    public JobLogger(string logFilePath)
    {
        _logFilePath = logFilePath;
    }

    public void Subscribe(ProcessingSystem system)
    {
        system.JobCompleted += (job, result) =>
        {
            _ = LogAsync($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [COMPLETED] {job.Id}, Result={result}");
        };

        system.JobFailed += (job, message) =>
        {
            _ = LogAsync($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [FAILED] {job.Id}, {message}");
        };
    }

    private async Task LogAsync(string line)
    {
        await _logSemaphore.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
        }
        finally
        {
            _logSemaphore.Release();
        }
    }
}