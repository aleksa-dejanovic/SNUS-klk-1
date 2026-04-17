namespace SNUS_KLK1.models
{
    public class JobHandle
    {
        public Guid Id { get; set; }
        public Task<int> Result { get; set; }
    }
}