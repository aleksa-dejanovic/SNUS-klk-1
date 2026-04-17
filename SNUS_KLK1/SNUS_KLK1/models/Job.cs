namespace SNUS_KLK1.models
{
    public class Job
    {
        public Guid Id { get; set; }
        public JobType Type { get; set; }
        public string Payload { get; set; }
        public int Priority { get; set; }
        public DateTime SubmittedAt { get; set; }
        public int RetryCount { get; set; }
        
        public Job Clone()
        {
            return new Job
            {
                Id = Id,
                Type = Type,
                Payload = Payload,
                Priority = Priority,
                SubmittedAt = SubmittedAt,
                RetryCount = RetryCount
            };
        }
    }
}