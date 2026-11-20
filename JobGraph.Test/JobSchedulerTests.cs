using JobGraph;

namespace JobGraph.Test;

/// <summary>
/// Work through these roughly in order: each stage builds on the one before.
/// Every RunAsync call is wrapped in a timeout, so a deadlock fails the test instead of hanging it.
/// </summary>
public class JobSchedulerTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(10);

    private Recorder _rec = null!;

    [SetUp]
    public void SetUp() => _rec = new Recorder();

    private static Task<ScheduleResult> Run(IEnumerable<Job> jobs, int maxConcurrency = int.MaxValue,
        CancellationToken ct = default) =>
        new JobScheduler(maxConcurrency).RunAsync(jobs, ct).WaitAsync(RunTimeout);

    /// <summary>The interview problem: C depends on A and B, E depends on C, D stands alone.</summary>
    private Job[] InterviewGraph() =>
    [
        _rec.Job("A"),
        _rec.Job("B"),
        _rec.Job("C", "A", "B"),
        _rec.Job("D"),
        _rec.Job("E", "C"),
    ];

    // ---------------------------------------------------------------------------------------
    // Stage 1: validation. Nothing runs until the graph is known to be runnable.
    // ---------------------------------------------------------------------------------------

    [Test]
    public void Constructor_RejectsConcurrencyBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new JobScheduler(0));
    }

    [Test]
    public async Task EmptyGraph_ReturnsEmptyResult()
    {
        var result = await Run([]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Completed, Is.Empty);
            Assert.That(result.Failed, Is.Empty);
            Assert.That(result.Skipped, Is.Empty);
        });
    }

    [Test]
    public async Task SingleJob_Runs()
    {
        var result = await Run([_rec.Job("A")]);

        Assert.That(result.Completed, Is.EquivalentTo(new[] { "A" }));
        Assert.That(_rec.RunCount("A"), Is.EqualTo(1));
    }

    [Test]
    public async Task DuplicateNames_Throw()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Run([_rec.Job("A"), _rec.Job("A")]));
        Assert.That(_rec.Events, Is.Empty, "no job should run when validation fails");
    }

    [Test]
    public async Task UnknownDependency_Throws_AndNothingRuns()
    {
        var ex = await Assert.ThrowsAsync<UnknownDependencyException>(() =>
            Run([_rec.Job("A"), _rec.Job("B", "Nope")]));

        Assert.Multiple(() =>
        {
            Assert.That(ex.Job, Is.EqualTo("B"));
            Assert.That(ex.Dependency, Is.EqualTo("Nope"));
            Assert.That(_rec.Events, Is.Empty, "no job should run when validation fails");
        });
    }

    [Test]
    public async Task SelfDependency_IsACycle()
    {
        await Assert.ThrowsAsync<DependencyCycleException>(() => Run([_rec.Job("A", "A")]));
        Assert.That(_rec.Events, Is.Empty);
    }

    [Test]
    public async Task Cycle_Throws_AndNothingRuns_EvenJobsOutsideTheCycle()
    {
        await Assert.ThrowsAsync<DependencyCycleException>(() => Run(
        [
            _rec.Job("A", "C"),
            _rec.Job("B", "A"),
            _rec.Job("C", "B"),
            _rec.Job("D"), // innocent bystander: must not run either
        ]));

        Assert.That(_rec.Events, Is.Empty);
    }

    [Test]
    public async Task CycleDownstreamOfValidJobs_StillDetected()
    {
        // A is fine; B and C form a cycle that hangs off A.
        await Assert.ThrowsAsync<DependencyCycleException>(() => Run(
        [
            _rec.Job("A"),
            _rec.Job("B", "A", "C"),
            _rec.Job("C", "B"),
        ]));

        Assert.That(_rec.Events, Is.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // Stage 2: ordering. Every job runs once, after its dependencies.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task InterviewGraph_RespectsDependencies()
    {
        var jobs = InterviewGraph();

        var result = await Run(jobs);

        Assert.That(result.Completed, Is.EquivalentTo(new[] { "A", "B", "C", "D", "E" }));
        _rec.AssertDependenciesRespected(jobs);
    }

    [Test]
    public async Task Diamond_SharedDependentRunsExactlyOnce()
    {
        // A -> B, A -> C, B and C -> D
        Job[] jobs =
        [
            _rec.Job("A"),
            _rec.Job("B", "A"),
            _rec.Job("C", "A"),
            _rec.Job("D", "B", "C"),
        ];

        await Run(jobs);

        Assert.Multiple(() =>
        {
            foreach (var name in new[] { "A", "B", "C", "D" })
                Assert.That(_rec.RunCount(name), Is.EqualTo(1), $"{name} should run exactly once");
        });
        _rec.AssertDependenciesRespected(jobs);
    }

    [Test]
    public async Task InputOrder_DoesNotMatter()
    {
        // A 50-job chain J0 <- J1 <- ... <- J49, listed last-first.
        var jobs = Enumerable.Range(0, 50)
            .Select(i => i == 0 ? _rec.Job("J0") : _rec.Job($"J{i}", $"J{i - 1}"))
            .Reverse()
            .ToArray();

        var result = await Run(jobs);

        Assert.That(result.Completed, Has.Count.EqualTo(50));
        _rec.AssertDependenciesRespected(jobs);
    }

    // ---------------------------------------------------------------------------------------
    // Stage 3: parallelism. Independent jobs overlap; nothing waits longer than it must.
    // These use gates: a job blocks until another job has started. Run serially, the gate
    // times out and the job fails with a message saying so.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task IndependentJobs_RunAtTheSameTime()
    {
        var aStarted = new Gate("A started");
        var bStarted = new Gate("B started");

        var result = await Run(
        [
            _rec.Job("A", async _ => { aStarted.Open(); await bStarted.WaitAsync("A"); }),
            _rec.Job("B", async _ => { bStarted.Open(); await aStarted.WaitAsync("B"); }),
        ]);

        Assert.That(result.Failed, Is.Empty, () => string.Join("\n", result.Failed.Values.Select(e => e.Message)));
    }

    [Test]
    public async Task InterviewGraph_AllRootsRunAtOnce()
    {
        // A, B and D have no dependencies; each waits until all three are running.
        var started = new[] { "A", "B", "D" }.ToDictionary(n => n, n => new Gate($"{n} started"));

        Func<CancellationToken, Task> Root(string me) => async _ =>
        {
            started[me].Open();
            foreach (var (other, gate) in started)
                if (other != me) await gate.WaitAsync(me);
        };

        Job[] jobs =
        [
            _rec.Job("A", Root("A")),
            _rec.Job("B", Root("B")),
            _rec.Job("C", "A", "B"),
            _rec.Job("D", Root("D")),
            _rec.Job("E", "C"),
        ];

        var result = await Run(jobs);

        Assert.That(result.Failed, Is.Empty, () => string.Join("\n", result.Failed.Values.Select(e => e.Message)));
        _rec.AssertDependenciesRespected(jobs);
    }

    [Test]
    public async Task DependentStarts_AsSoonAsItsOwnDependenciesFinish_NotInWaves()
    {
        // A is quick and B is slow. C needs only A, so C must not wait for B.
        // B refuses to finish until C has started: a scheduler that runs "level by level"
        // (wait for A and B, then start C) deadlocks here.
        var cStarted = new Gate("C started");

        Job[] jobs =
        [
            _rec.Job("A"),
            _rec.Job("B", async _ => await cStarted.WaitAsync("B")),
            _rec.Job("C", _ => { cStarted.Open(); return Task.CompletedTask; }, "A"),
            _rec.Job("D", "B", "C"),
        ];

        var result = await Run(jobs);

        Assert.That(result.Failed, Is.Empty, () => string.Join("\n", result.Failed.Values.Select(e => e.Message)));
        _rec.AssertDependenciesRespected(jobs);
    }

    [Test]
    public async Task MaxConcurrency_IsNeverExceeded_AndIsReached()
    {
        var jobs = Enumerable.Range(0, 12)
            .Select(i => _rec.Job($"J{i}", _ => Task.Delay(30)))
            .ToArray();

        var result = await Run(jobs, maxConcurrency: 3);

        Assert.Multiple(() =>
        {
            Assert.That(result.Completed, Has.Count.EqualTo(12));
            Assert.That(_rec.MaxRunning, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task MaxConcurrencyOfOne_RunsSerially_InDependencyOrder()
    {
        var jobs = InterviewGraph();

        var result = await Run(jobs, maxConcurrency: 1);

        Assert.That(result.Completed, Has.Count.EqualTo(5));
        Assert.That(_rec.MaxRunning, Is.EqualTo(1));
        _rec.AssertDependenciesRespected(jobs);
    }

    [Test]
    public async Task LargeRandomGraph_EveryJobOnce_InOrder()
    {
        // Shakes out races: 500 jobs, each depending on up to 4 earlier ones, with small random delays.
        var random = new Random(1234);
        var jobs = new List<Job>();
        for (var i = 0; i < 500; i++)
        {
            var deps = i == 0
                ? []
                : Enumerable.Range(0, random.Next(0, 5)).Select(_ => $"J{random.Next(0, i)}").Distinct().ToArray();
            var delay = random.Next(0, 3);
            jobs.Add(_rec.Job($"J{i}", _ => Task.Delay(delay), deps));
        }
        jobs = jobs.OrderBy(_ => random.Next()).ToList();

        var result = await Run(jobs, maxConcurrency: 16);

        Assert.That(result.Completed, Has.Count.EqualTo(500));
        Assert.That(_rec.MaxRunning, Is.LessThanOrEqualTo(16));
        Assert.Multiple(() =>
        {
            foreach (var job in jobs)
                Assert.That(_rec.RunCount(job.Name), Is.EqualTo(1), $"{job.Name} should run exactly once");
        });
        _rec.AssertDependenciesRespected(jobs);
    }

    // ---------------------------------------------------------------------------------------
    // Stage 4: failure. A failed job takes down what depends on it, and nothing else.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task FailedJob_SkipsEverythingDownstream_AndOthersFinish()
    {
        var boom = new InvalidOperationException("A broke");

        var result = await Run(
        [
            _rec.Job("A", _ => throw boom),
            _rec.Job("B"),
            _rec.Job("C", "A", "B"),
            _rec.Job("D"),
            _rec.Job("E", "C"),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Failed.Keys, Is.EquivalentTo(new[] { "A" }));
            Assert.That(result.Failed["A"], Is.SameAs(boom));
            Assert.That(result.Skipped, Is.EquivalentTo(new[] { "C", "E" }));
            Assert.That(result.Completed, Is.EquivalentTo(new[] { "B", "D" }));
            Assert.That(_rec.Ran("C"), Is.False, "C depends on a failed job and must not run");
            Assert.That(_rec.Ran("E"), Is.False, "E depends on a skipped job and must not run");
        });
    }

    [Test]
    public async Task AsyncFailure_IsCaughtToo()
    {
        var result = await Run(
        [
            _rec.Job("A", async _ => { await Task.Delay(10); throw new InvalidOperationException("late"); }),
            _rec.Job("B", "A"),
        ]);

        Assert.That(result.Failed.Keys, Is.EquivalentTo(new[] { "A" }));
        Assert.That(result.Skipped, Is.EquivalentTo(new[] { "B" }));
    }

    [Test]
    public async Task Failure_DoesNotStopSlowUnrelatedWork()
    {
        // A fails immediately; B is slow and unrelated, and so is everything after it.
        var result = await Run(
        [
            _rec.Job("A", _ => throw new InvalidOperationException()),
            _rec.Job("B", _ => Task.Delay(100)),
            _rec.Job("C", "B"),
            _rec.Job("D", "A", "C"),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Completed, Is.EquivalentTo(new[] { "B", "C" }));
            Assert.That(result.Skipped, Is.EquivalentTo(new[] { "D" }));
        });
    }

    [Test]
    public async Task EveryJobIsAccountedFor_ExactlyOnce()
    {
        var result = await Run(
        [
            _rec.Job("A", _ => throw new InvalidOperationException()),
            _rec.Job("B", "A"),
            _rec.Job("C", "A"),
            _rec.Job("D", "B", "C"),
            _rec.Job("E"),
        ]);

        var all = result.Completed.Concat(result.Failed.Keys).Concat(result.Skipped).ToList();
        Assert.That(all, Is.EquivalentTo(new[] { "A", "B", "C", "D", "E" }),
            "each job appears in exactly one of Completed, Failed or Skipped");
    }

    // ---------------------------------------------------------------------------------------
    // Stage 5: cancellation.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task AlreadyCancelled_Throws_AndNothingRuns()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.CatchAsync<OperationCanceledException>(() => Run(InterviewGraph(), ct: cts.Token));
        Assert.That(_rec.Events, Is.Empty);
    }

    [Test]
    public async Task CancelledMidRun_NoNewJobsStart()
    {
        using var cts = new CancellationTokenSource();

        // A cancels the run and then finishes normally. C and E were waiting on A: they must never start.
        await Assert.CatchAsync<OperationCanceledException>(() => Run(
        [
            _rec.Job("A", _ => { cts.Cancel(); return Task.CompletedTask; }),
            _rec.Job("C", "A"),
            _rec.Job("E", "C"),
        ], ct: cts.Token));

        Assert.Multiple(() =>
        {
            Assert.That(_rec.Ran("C"), Is.False);
            Assert.That(_rec.Ran("E"), Is.False);
        });
    }

    [Test]
    public async Task RunningJobs_SeeTheCancellation()
    {
        using var cts = new CancellationTokenSource();
        var started = new Gate("A started");

        // A would run forever unless the token it was handed gets cancelled.
        var run = Run([_rec.Job("A", async ct => { started.Open(); await Task.Delay(Timeout.Infinite, ct); })],
            ct: cts.Token);

        await Assert.CatchAsync<OperationCanceledException>(async () =>
        {
            await started.WaitAsync("the test");
            cts.Cancel();
            await run;
        });
    }
}
