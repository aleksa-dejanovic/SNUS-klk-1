namespace SNUS_KLK1;

using System.Collections.Concurrent;
using SNUS_KLK1.models;

public class ProcessingSystem
{
    // ── Configuration ────────────────────────────────────────────────────────

    private readonly SystemConfig _cfg;
    private const string ReportsDir = "reports";

    // ── Job tracking ─────────────────────────────────────────────────────────

    private readonly PriorityQueue<Job, int> _pendingJobs = new();
    private readonly Lock _queueLock = new();

    private readonly ConcurrentDictionary<Guid, Job> _allJobs = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<int>> _jobResults = new();

    // ── Execution history ────────────────────────────────────────────────────

    private readonly List<JobExecutionInfo> _historyLog = new();
    private readonly Lock _historyLock = new();

    // ── Worker pool ──────────────────────────────────────────────────────────

    private int _availableThreads;
    private readonly object _threadsLock = new();

    // ── Background task infrastructure ───────────────────────────────────────

    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = new();

    // ── Randomness ───────────────────────────────────────────────────────────

    private readonly Random _rnd = new();
    private readonly Lock _rndLock = new();

    // ── Events ───────────────────────────────────────────────────────────────

    public event Action<Job, int>? JobCompleted;
    public event Action<Job, string>? JobFailed;

    // ── Constructor ──────────────────────────────────────────────────────────

    public ProcessingSystem(SystemConfig cfg)
    {
        this._cfg = cfg;
        _availableThreads = cfg.WorkerCount;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public void Start()
    {
        _loops.Add(Task.Run(() => ConsumeLoop(_cts.Token)));
        _loops.Add(Task.Run(() => ReportLoop(_cts.Token)));
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        _queueSignal.Release();
        await Task.WhenAll(_loops);
    }

    public JobHandle Submit(Job incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        lock (_queueLock)
        {
            if (_allJobs.ContainsKey(incoming.Id))
                throw new InvalidOperationException($"Job {incoming.Id} already exists.");

            if (_pendingJobs.Count >= _cfg.MaxQueueSize)
                throw new InvalidOperationException("Job queue is at capacity.");

            incoming.SubmittedAt = DateTime.UtcNow;
            incoming.RetryCount  = 0;

            _pendingJobs.Enqueue(incoming, incoming.Priority);
            _allJobs[incoming.Id] = incoming;
        }

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobResults[incoming.Id] = tcs;
        _queueSignal.Release();

        return new JobHandle { Id = incoming.Id, Result = tcs.Task };
    }


    public Job? GetJob(Guid id)
    {
        _allJobs.TryGetValue(id, out var found);
        return found?.Clone();
    }

    public IEnumerable<Job> GetTopJobs(int count)
    {
        if (count <= 0)
            return [];

        lock (_queueLock)
        {
            return _pendingJobs.UnorderedItems
                .OrderBy(x => x.Priority)
                .ThenBy(x => x.Element.SubmittedAt)
                .Take(count)
                .Select(x => x.Element)
                .ToList();
        }
    }

    private List<JobExecutionInfo> GetExecutionHistorySnapshot()
    {
        lock (_historyLock)
        {
            return _historyLog.ToList();
        }
    }

    // ── Background loops ─────────────────────────────────────────────────────

    private async Task ConsumeLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(cancellationToken);

                var job = DequeueJob();
                if (job == null) continue;

                var requiredWorkers = GetRequiredWorkerCount(job);

                if (!TryReserveWorkers(requiredWorkers))
                {
                    RequeueJob(job);
                    _queueSignal.Release();
                    await Task.Delay(50, cancellationToken);
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteJobWithRetriesAsync(job);
                    }
                    finally
                    {
                        ReleaseWorkers(requiredWorkers);
                        _queueSignal.Release();
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Consume loop: {ex.Message}");
            }
        }
    }

    private async Task ReportLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                ReportGenerator.GenerateReport(GetExecutionHistorySnapshot(), ReportsDir);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Report loop: {ex.Message}");
            }
        }
    }

    // ── Queue helpers ─────────────────────────────────────────────────────────

    private Job? DequeueJob()
    {
        lock (_queueLock)
        {
            return _pendingJobs.Count > 0 ? _pendingJobs.Dequeue() : null;
        }
    }

    private void RequeueJob(Job job)
    {
        lock (_queueLock)
        {
            _pendingJobs.Enqueue(job, job.Priority);
        }
    }

    // ── Worker pool helpers ───────────────────────────────────────────────────

    private bool TryReserveWorkers(int count)
    {
        lock (_threadsLock)
        {
            if (_availableThreads < count) return false;
            _availableThreads -= count;
            return true;
        }
    }

    private void ReleaseWorkers(int count)
    {
        lock (_threadsLock)
        {
            _availableThreads += count;
        }
    }

    // ── Job execution ─────────────────────────────────────────────────────────

    private int GetRequiredWorkerCount(Job job) => job.Type switch
    {
        JobType.Prime => Math.Clamp(ParsePayloadInt(job.Payload, "threads", defaultValue: 1), 1, 8),
        _ => 1
    };

    private async Task ExecuteJobWithRetriesAsync(Job job)
    {
        const int maxAttempts = 3;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            job.RetryCount = attempt - 1;
            var attemptStart = DateTime.UtcNow;

            try
            {
                if (DateTime.UtcNow - job.SubmittedAt > TimeSpan.FromSeconds(2))
                    throw new TimeoutException($"Job {job.Id} exceeded the total allowed wait time.");

                var result = await ExecuteWithTimeoutAsync(job);

                RecordExecution(job, attempt, attemptStart, success: true, result: result);

                if (_jobResults.TryGetValue(job.Id, out var tcs))
                    tcs.TrySetResult(result);

                JobCompleted?.Invoke(job, result);
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                var isFinalAttempt = attempt == maxAttempts;

                RecordExecution(job, attempt, attemptStart, success: false, result: null,
                    aborted: isFinalAttempt, message: ex.Message);

                JobFailed?.Invoke(job, isFinalAttempt
                    ? $"ABORT: {ex.Message}"
                    : $"Retry {attempt} failed: {ex.Message}");

                if (!isFinalAttempt) continue;
                if (_jobResults.TryGetValue(job.Id, out var tcs))
                    tcs.TrySetException(new Exception($"Job {job.Id} aborted after {maxAttempts} failed attempts.", ex));

                return;
            }
        }

        if (_jobResults.TryGetValue(job.Id, out var fallbackTcs))
            fallbackTcs.TrySetException(lastException ?? new Exception("Unknown job failure."));
    }

    private void RecordExecution(Job job, int attempt, DateTime startedAt,
        bool success, int? result, bool aborted = false, string? message = null)
    {
        var duration = DateTime.UtcNow - startedAt;

        var resolvedMessage = message == null
            ? $"Success on attempt {attempt}"
            : aborted
                ? $"ABORT after attempt {attempt}: {message}"
                : $"Failed attempt {attempt}: {message}";

        lock (_historyLock)
        {
            _historyLog.Add(new JobExecutionInfo
            {
                JobId    = job.Id,
                Type     = job.Type,
                Success  = success,
                Aborted  = aborted,
                Result   = result,
                Duration = duration,
                FinishedAt = DateTime.UtcNow,
                Message  = resolvedMessage
            });
        }
    }

    private async Task<int> ExecuteWithTimeoutAsync(Job job)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var executionTask = DispatchJobAsync(job,  cts.Token);
        try
        {
            return await executionTask;
        } catch (OperationCanceledException)
        {
            throw new TimeoutException($"Job {job.Id} exceeded the execution time limit.");
        }
    }

    private Task<int> DispatchJobAsync(Job job, CancellationToken token) => job.Type switch
    {
        JobType.IO    => ExecuteIoJobAsync(job.Payload, token),
        JobType.Prime => ExecutePrimeJobAsync(job.Payload, token),
        _ => throw new InvalidOperationException($"Unknown job type: {job.Type}")
    };

    // ── IO job ────────────────────────────────────────────────────────────────

    private async Task<int> ExecuteIoJobAsync(string payload, CancellationToken token)
    {
        var delay = ParsePayloadInt(payload, "delay", throwOnMissing: true);
        await Task.Delay(delay, token);
        lock (_rndLock)
        {
            return _rnd.Next(0, 101);
        }
        
    }

    // ── Prime job ─────────────────────────────────────────────────────────────

    private Task<int> ExecutePrimeJobAsync(string payload, CancellationToken token)
    {
        var limit= ParsePayloadInt(payload, "numbers", throwOnMissing: true);
        var threadCount = Math.Clamp(ParsePayloadInt(payload, "threads", defaultValue: 1), 1, 8);

        return Task.Run(() =>
        {
            var total = 0;
            var rangeSize = limit / threadCount;
            object lockObj = new();

            Parallel.For(
                2,
                limit + 1,
                new ParallelOptions { MaxDegreeOfParallelism = threadCount },
                () => 0, 
                (i, state, localCount) => 
                {
                    if (IsPrime(i))
                        localCount++;
                    return localCount;
                },
                (localCount) => 
                {
                    Interlocked.Add(ref total, localCount);
                }
            );


            return total;
        }, token);
    }

    private static bool IsPrime(int n)
    {
        switch (n)
        {
            case < 2:
                return false;
            case 2:
                return true;
        }

        if (n % 2 == 0) return false;

        var limit = (int)Math.Sqrt(n);
        for (var i = 3; i <= limit; i += 2)
            if (n % i == 0) return false;

        return true;
    }

    // ── Payload parsing ───────────────────────────────────────────────────────
    private static int ParsePayloadInt(
        string payload,
        string key,
        int defaultValue = 0,
        bool throwOnMissing = false)
    {
        foreach (var part in payload.Split(','))
        {
            var trimmed = part.Trim();
            if (!trimmed.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)) continue;

            var raw = trimmed[(key.Length + 1)..].Replace("_", "");
            if (int.TryParse(raw, out int value))
                return Math.Max(0, value);
        }

        return throwOnMissing ? throw new FormatException($"Payload is missing required key '{key}'.") : defaultValue;
    }
}