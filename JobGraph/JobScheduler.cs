namespace JobGraph;

/// <summary>
/// Runs a graph of jobs, each as soon as everything it depends on has completed,
/// with independent jobs running concurrently.
/// </summary>
public sealed class JobScheduler
{
    /// <param name="maxConcurrency">
    /// Most jobs allowed to be running at once. Must be at least 1
    /// (<see cref="ArgumentOutOfRangeException"/> otherwise).
    /// </param>
    public JobScheduler(int maxConcurrency = int.MaxValue)
    {
        if (maxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrency),
                $"{nameof(maxConcurrency)} must be greater than 0");
        }
        MaxConcurrency = maxConcurrency;
    }

    public int MaxConcurrency { get; }

    /// <summary>
    /// Validates the graph, then runs it.
    /// <list type="bullet">
    /// <item>Validation happens before any job starts; if it fails, nothing runs.
    ///   Duplicate names: <see cref="ArgumentException"/>. A dependency not in the graph:
    ///   <see cref="UnknownDependencyException"/>. A cycle, including a job depending on itself:
    ///   <see cref="DependencyCycleException"/>.</item>
    /// <item>Each job runs exactly once, and only after all its dependencies completed.</item>
    /// <item>A job starts as soon as its own dependencies are done, not when a whole "wave" is done.</item>
    /// <item>No more than <see cref="MaxConcurrency"/> jobs run at once.</item>
    /// <item>A job that throws is reported in <see cref="ScheduleResult.Failed"/>; everything downstream of
    ///   it is skipped; unrelated jobs carry on. Job failures do not make this method throw.</item>
    /// <item>Jobs receive a token that is cancelled when <paramref name="cancellationToken"/> is. Once it is
    ///   cancelled, no new job starts, and this method throws <see cref="OperationCanceledException"/>.</item>
    /// </list>
    /// </summary>
    public async Task<ScheduleResult> RunAsync(IEnumerable<Job> jobs, CancellationToken cancellationToken = default)
    {
        var completedJobs = new List<string>();
        var failedJobs = new Dictionary<string, Exception>();
        var toSchedule = jobs.ToList();
        var jobsByName = toSchedule.ToDictionary(j => j.Name);
        var jobsByDependent = new Dictionary<string, List<string>>();
        var jobDegrees = new Dictionary<string, int>();
        var readyQueue = new Queue<Job>();
        foreach (var job in toSchedule)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckCycles(job, jobsByName);
            if (job.DependsOn.Count > 0)
            {
                jobDegrees.Add(job.Name, job.DependsOn.Count);
                foreach (var dep in job.DependsOn)
                {
                    if (!jobsByDependent.TryGetValue(dep, out var value))
                    {
                        value = [];
                        jobsByDependent.Add(dep, value);
                    }

                    value.Add(job.Name);
                }
            }
            else
            {
                readyQueue.Enqueue(job);
            }
        }

        var running = new HashSet<Task<(Job, Exception?)>>();
        
        while (readyQueue.Count > 0 || running.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                if (running.Count > 0)
                {
                    await Task.WhenAll(running);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            while (running.Count < MaxConcurrency && readyQueue.Count > 0)
            {
                var job = readyQueue.Dequeue();
                running.Add(RunJob(job, cancellationToken));
            }

            var completedTask = await Task.WhenAny(running);
            running.Remove(completedTask);
            var (completedJob, completedException) = await completedTask;
            if (completedException != null)
            {
                failedJobs.Add(completedJob.Name, completedException);
                continue;
            }
            completedJobs.Add(completedJob.Name);
            if (!jobsByDependent.Remove(completedJob.Name, out var jobsToUnblock)) continue;
            foreach (var jobToUnblock in jobsToUnblock)
            {
                jobDegrees[jobToUnblock]--;
                if (jobDegrees[jobToUnblock] != 0) continue;
                jobDegrees.Remove(jobToUnblock);
                readyQueue.Enqueue(jobsByName[jobToUnblock]);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new ScheduleResult(completedJobs, failedJobs, [.. jobDegrees.Keys]);
    }

    private static async Task<(Job, Exception?)> RunJob(Job job, CancellationToken cancellationToken)
    {
        Exception? exception = null;
        try
        {
            await Task.Run(() => job.Run(cancellationToken), cancellationToken);
        }
        catch (Exception e)
        {
            exception = e;
        }

        return (job, exception);
    }

    private static void CheckCycles(Job job, Dictionary<string, Job> jobsByName, HashSet<string>? visiting = null, HashSet<string>? done = null)
    {
        visiting ??= new HashSet<string>();
        done ??= new HashSet<string>();
        if (done.Contains(job.Name)) return;
        if (!visiting.Add(job.Name))
        {
            throw new DependencyCycleException($"Dependency cycled detected in {job.Name}: [{string.Join(", ", visiting)}, {job.Name}]");
        }
        foreach (var dependencyName in job.DependsOn)
        {
            if (!jobsByName.TryGetValue(dependencyName, out var dependency))
            {
                throw new UnknownDependencyException(job.Name, dependencyName);
            }
            CheckCycles(dependency, jobsByName, visiting, done);
        }

        visiting.Remove(job.Name);
        done.Add(job.Name);
    }
}
