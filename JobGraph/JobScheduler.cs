using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

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
        var jobCollection = new JobCollection(jobs);
        var scheduleValidation = new SchedulingState(jobCollection);
        while (scheduleValidation.HasNext)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = scheduleValidation.StartNext()!;
            scheduleValidation.Complete(job);
        }

        if (scheduleValidation.Unstarted().Any())
        {
            var failingJobName = scheduleValidation.Unstarted().First();
            jobCollection.TryGetByName(failingJobName, out var failingJob);
            
            // This should always throw, if we get here a cycle must exist
            CheckCycles(failingJob, jobCollection);
            throw new Exception("An unknown scheduling error occurred");
        }

        var runState = new SchedulingState(jobCollection);
        var completions = Channel.CreateUnbounded<(Job, Exception?)>(
            new UnboundedChannelOptions { SingleReader = true }
        );

        while (!runState.Done)
        {
            while (!cancellationToken.IsCancellationRequested 
                   && runState.NumRunning < MaxConcurrency 
                   && runState.HasNext)
            {
                var toStart = runState.StartNext()!;
                _ = RunJob(toStart, completions, cancellationToken);
            }

            if (runState.NumRunning == 0) break;

            var (doneJob, exception) = await completions.Reader.ReadAsync(CancellationToken.None);
            if (exception != null) runState.Fail(doneJob, exception);
            else runState.Complete(doneJob);
        }
        
        cancellationToken.ThrowIfCancellationRequested();
        return runState.GetResult();
    }

    private static async Task RunJob(Job job, ChannelWriter<(Job, Exception?)> resultWriter, CancellationToken cancellationToken)
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

        await resultWriter.WriteAsync((job, exception), CancellationToken.None);
    }

    private static void CheckCycles(Job job, JobCollection jobs)
    {
        var visiting = new HashSet<string>([job.Name]);
        var done = new HashSet<string>();
        // ReSharper disable once GenericEnumeratorNotDisposed
        var chain = new Stack<(Job, IEnumerator<string>)>([(job, job.DependsOn.GetEnumerator())]);
        try
        {
            while (chain.Count > 0)
            {
                var (current, deps) = chain.Peek();
                if (deps.MoveNext())
                {
                    if (!jobs.TryGetByName(deps.Current, out var dep))
                        throw new UnknownDependencyException(current.Name, deps.Current);
                    if (done.Contains(dep.Name)) continue;
                    if (visiting.Contains(dep.Name))
                    {
                        var cycle = chain.Select(j => j.Item1.Name)
                            .Reverse()
                            .SkipWhile(d => d != dep.Name)
                            .Append(dep.Name);
                        throw new DependencyCycleException($"Dependency cycle detected in {dep.Name}: " +
                                                           $"[{string.Join(" -> ", cycle)}]");
                    }

                    visiting.Add(dep.Name);
                    // ReSharper disable once GenericEnumeratorNotDisposed
                    chain.Push((dep, dep.DependsOn.GetEnumerator()));
                }
                else
                {
                    chain.Pop();
                    deps.Dispose();
                    visiting.Remove(current.Name);
                    done.Add(current.Name);
                }
            }
        }
        finally
        {
            while (chain.Count > 0)
            {
                var (_, deps) = chain.Pop();
                deps.Dispose();
            }
        }
    }

    private class JobCollection : IEnumerable<Job>
    {
        private readonly IReadOnlyList<Job> _jobs;
        private readonly IReadOnlyDictionary<string, Job> _jobsByName;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _jobsByDependency;

        public JobCollection(IEnumerable<Job> jobs)
        {
            _jobs = [.. jobs];
            var jobsByName = new Dictionary<string, Job>();
            var jobsByDependency = new Dictionary<string, List<string>>();
            foreach (var job in _jobs)
            {
                jobsByName.Add(job.Name, job);
                foreach (var dep in job.DependsOn)
                {
                    if (!jobsByDependency.TryGetValue(dep, out var dependents))
                    {
                        jobsByDependency.Add(dep, dependents = []);
                    }
                    dependents.Add(job.Name);
                }
            }
            _jobsByName = jobsByName;
            _jobsByDependency = jobsByDependency.ToDictionary(kv => kv.Key, IReadOnlyList<string> (kv) => kv.Value);
        }

        public IEnumerable<Job> GetDependents(string jobName)
        {
            if (!_jobsByDependency.TryGetValue(jobName, out var dependents))
                return [];
            return dependents.Select(d => _jobsByName[d]);
        }

        public IEnumerator<Job> GetEnumerator() =>
            _jobs.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
        
        public bool TryGetByName(string jobName, [MaybeNullWhen(false)] out Job job)
        {
            return _jobsByName.TryGetValue(jobName, out job);
        }
    }

    private class SchedulingState
    {
        private readonly JobCollection _jobs;
        private readonly Dictionary<string, int> _jobDegrees = new();
        private readonly HashSet<string> _running = new();
        private readonly Queue<Job> _readyQueue = new();
        private readonly HashSet<string> _completedJobs = new();
        private readonly Dictionary<string, Exception> _failedJobs = new();

        public bool HasNext => _readyQueue.Count > 0;
        public int NumRunning => _running.Count;

        public bool Done => _readyQueue.Count == 0 && _running.Count == 0;

        public SchedulingState(JobCollection jobs)
        {
            _jobs = jobs;
            foreach (var job in _jobs)
            {
                if (job.DependsOn.Count == 0)
                {
                    _readyQueue.Enqueue(job);
                }
                else
                {
                    _jobDegrees.Add(job.Name, job.DependsOn.Count);
                }
            }
        }
        
        public Job? StartNext()
        {
            if (!_readyQueue.TryDequeue(out var job)) return null;
            _running.Add(job.Name);
            return job;

        }

        public void Complete(Job job)
        {
            if (_running.Remove(job.Name))
            {
                _completedJobs.Add(job.Name);
                var dependents = _jobs.GetDependents(job.Name);
                foreach (var dependent in dependents)
                {
                    if (!_jobDegrees.TryGetValue(dependent.Name, out var value)) continue;
                    _jobDegrees[dependent.Name] = --value;
                    if (value != 0) continue;
                    _jobDegrees.Remove(dependent.Name);
                    _readyQueue.Enqueue(dependent);
                }
            }
        }

        public void Fail(Job job, Exception e)
        {
            if (_running.Remove(job.Name))
            {
                _failedJobs.Add(job.Name, e);
            }
        }

        public IEnumerable<string> Unstarted()
        {
            return [.. _jobDegrees.Keys, .. _readyQueue.Select(j => j.Name)];
        }

        public ScheduleResult GetResult()
        {
            return new ScheduleResult(_completedJobs, _failedJobs, [.. Unstarted()]);
        }
    }
}
