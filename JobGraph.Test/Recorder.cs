using System.Collections.Concurrent;
using JobGraph;

namespace JobGraph.Test;

/// <summary>
/// Builds jobs that log when they start and end, and tracks how many run at once.
/// </summary>
internal sealed class Recorder
{
    private readonly ConcurrentQueue<string> _events = new();
    private readonly ConcurrentDictionary<string, int> _runCounts = new();
    private int _running;
    private int _maxRunning;

    public static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(2);

    public IReadOnlyList<string> Events => _events.ToList();
    public int MaxRunning => Volatile.Read(ref _maxRunning);
    public int RunCount(string name) => _runCounts.GetValueOrDefault(name);
    public bool Ran(string name) => RunCount(name) > 0;

    /// <summary>A job that does nothing but record itself.</summary>
    public Job Job(string name, params string[] dependsOn) =>
        Job(name, async _ => await Task.Yield(), dependsOn);

    /// <summary>A job that records itself around <paramref name="body"/>.</summary>
    public Job Job(string name, Func<CancellationToken, Task> body, params string[] dependsOn) =>
        new(name, dependsOn, async ct =>
        {
            _runCounts.AddOrUpdate(name, 1, (_, n) => n + 1);
            var now = Interlocked.Increment(ref _running);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxRunning)) &&
                   Interlocked.CompareExchange(ref _maxRunning, now, seen) != seen) { }
            _events.Enqueue($"start:{name}");
            try
            {
                await body(ct);
            }
            finally
            {
                _events.Enqueue($"end:{name}");
                Interlocked.Decrement(ref _running);
            }
        });

    public int IndexOf(string evt)
    {
        var events = Events;
        for (var i = 0; i < events.Count; i++)
            if (events[i] == evt) return i;
        return -1;
    }

    /// <summary>Asserts every job started only after each of its dependencies ended.</summary>
    public void AssertDependenciesRespected(IEnumerable<Job> jobs)
    {
        foreach (var job in jobs)
        foreach (var dep in job.DependsOn)
        {
            var depEnd = IndexOf($"end:{dep}");
            var start = IndexOf($"start:{job.Name}");
            Assert.That(depEnd, Is.GreaterThanOrEqualTo(0), $"{dep} never ended, but {job.Name} depends on it");
            Assert.That(start, Is.GreaterThan(depEnd), $"{job.Name} started before its dependency {dep} ended");
        }
    }
}

/// <summary>A one-shot signal that jobs can raise and wait on.</summary>
internal sealed class Gate(string name)
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open() => _tcs.TrySetResult();

    public async Task WaitAsync(string waiter)
    {
        try
        {
            await _tcs.Task.WaitAsync(Recorder.GateTimeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"{waiter} waited {Recorder.GateTimeout.TotalSeconds}s for '{name}' and it never happened. " +
                "Jobs that should be running at the same time were not.");
        }
    }
}
