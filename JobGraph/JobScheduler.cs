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
    public Task<ScheduleResult> RunAsync(IEnumerable<Job> jobs, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
