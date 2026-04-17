using SNUS_KLK1;
using SNUS_KLK1.models;

try
{
    // Loading from XML
    var path = Path.Combine(AppContext.BaseDirectory, "config.xml");
    var (config, jobs) = XmlConfigLoader.Load(path);

    var system = new ProcessingSystem(config);

    // File logger
    var logger = new JobLogger("joblog.txt");
    logger.Subscribe(system);

    // Debug output
    system.JobCompleted += (job, result) =>
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[COMPLETED] [{job.Type}] {result}");
        Console.ResetColor();
    };

    system.JobFailed += (job, msg) =>
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[FAILED] [{job.Type}] {msg}");
        Console.ResetColor();
    };

    // Insert initial jobs from XML
    foreach (var job in jobs)
    {
        system.Submit(job);
    }

    // Start system (worker + report loop)
    system.Start();

    // Start producer threads
    var producerCts = new CancellationTokenSource();
    var producerTasks = new List<Task>();

    var producerCount = config.WorkerCount;

    for (var p = 0; p < producerCount; p++)
    {
        var producerId = p + 1;

        producerTasks.Add(Task.Run(async () =>
        {
            var random = new Random(Guid.NewGuid().GetHashCode());

            while (!producerCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(random.Next(500, 2000), producerCts.Token);

                    Job newJob;

                    if (random.Next(2) == 0)
                    {
                        var delay = random.Next(500, 5000);
                        newJob = new Job
                        {
                            Id = Guid.NewGuid(),
                            Type = JobType.IO,
                            Payload = $"delay:{delay}",
                            Priority = random.Next(1, 4)
                        };
                    }
                    else
                    {
                        var number = random.Next(5000, 20000);
                        var threads = random.Next(1, 5);

                        newJob = new Job
                        {
                            Id = Guid.NewGuid(),
                            Type = JobType.Prime,
                            Payload = $"numbers:{number},threads:{threads}",
                            Priority = random.Next(1, 4)
                        };
                    }

                    system.Submit(newJob);
                    Console.WriteLine($"[PRODUCER {producerId}] Added {newJob.Type} job");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Producer {producerId} error: {ex.Message}");
                }
            }
        }));
    }

    Console.WriteLine("System running... (press ENTER to exit)");
    Console.ReadLine();

    // Stop producer(s)
    producerCts.Cancel();
    await Task.WhenAll(producerTasks);

    // Stop system
    await system.StopAsync();

    Console.WriteLine("System stopped.");
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
}