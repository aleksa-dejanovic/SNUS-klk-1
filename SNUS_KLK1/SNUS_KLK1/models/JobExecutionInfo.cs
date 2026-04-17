namespace SNUS_KLK1.models
{

    public class JobExecutionInfo
    {
        public Guid JobId { get; set; }
        public JobType Type { get; set; }
        public bool Success { get; set; }
        public bool Aborted { get; set; }
        public int? Result { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime FinishedAt { get; set; }
        public string Message { get; set; } = string.Empty;

    }
}