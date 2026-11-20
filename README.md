# JobGraph

Practice project: a dependency-aware job scheduler that runs independent jobs in parallel.

## Why this exists

The Microsoft hiring day (2026-11-19) had a coding round: "write a scheduler for 5 jobs, where C
depends on A and B, and E depends on C." It's a topological sort problem, which I hadn't done since
college, if at all. This project is practice for it, using the "actually run the jobs, in parallel"
version, since that's where an interviewer usually goes next.

It's also a warm-up for building AUR packages in PacmanManager, where packages have to be built in
dependency order and independent ones could build at the same time.

**Scope right now: practice the implementation.** Making it a reusable NuGet package is a later step
(see "Later" below). Don't redesign the API yet.

## The task

Implement `JobScheduler` in `JobGraph/JobScheduler.cs` (the constructor check and `RunAsync`) until
every test passes. Nothing else needs writing: `Job`, `ScheduleResult` and the two exceptions are
done. **No solution exists anywhere in the repo.** All 24 tests fail until you write it.

    dotnet test
    dotnet test --filter "Name~Cycle"     # one area at a time

The input is a list of `Job(Name, DependsOn, Run)`, where `Run` is an async delegate that takes a
`CancellationToken`. The rules (also in the doc comment on `RunAsync`):

- **Validate first; if validation fails, nothing runs.** Duplicate names → `ArgumentException`. A
  dependency that isn't in the list → `UnknownDependencyException`. A cycle, including a job that
  depends on itself → `DependencyCycleException`.
- **Each job runs exactly once, after all its dependencies completed.**
- **A job starts as soon as its own dependencies are done**, not when a whole "level" of the graph is
  done.
- **At most `maxConcurrency` jobs run at once.** The constructor rejects anything below 1 with
  `ArgumentOutOfRangeException`.
- **A job that throws goes in `Failed`** (with its exception). Everything downstream of it goes in
  `Skipped` and never runs. Unrelated jobs carry on. Job failures don't make `RunAsync` throw.
- **Cancellation:** jobs get a token that's cancelled when the caller's is. Once it's cancelled, no new
  job starts, and `RunAsync` throws `OperationCanceledException`.

## The tests, easiest first

`JobGraph.Test/JobSchedulerTests.cs`, in five stages:

1. **Validation:** duplicates, unknown dependencies, cycles (including one hanging off valid jobs).
2. **Ordering:** the interview graph, a diamond (D reached through both B and C runs once), and a
   50-job chain listed backwards so input order can't be relied on.
3. **Running in parallel:**
   - Independent jobs really do overlap.
   - The "not in waves" test: B won't finish until C starts, and C needs only A. A level-by-level
     scheduler gets stuck here.
   - The concurrency cap is respected and reached.
   - A 500-job random graph to catch race conditions.
4. **Failure:** downstream jobs are skipped, everything else finishes, and every job ends up in
   exactly one of Completed / Failed / Skipped.
5. **Cancellation:** already cancelled, cancelled partway through, and a running job seeing the
   cancellation.

A suggested path: get stages 1–2 passing with a plain one-at-a-time version first, then make it
parallel.

How the tests work, in case one fails confusingly:

- Stage 3 uses **gates** (`Gate` in `JobGraph.Test/Recorder.cs`): a job waits until another job has
  started. If they never run at the same time, the gate times out after 2 s and the job fails with
  a message saying so.
- Every run has a **10 s timeout**, so a deadlock fails the test instead of hanging it.
- `Recorder` logs `start:X` / `end:X` events and tracks how many jobs ran at once.

## Topological sort, the short version

Order the jobs so each one comes after everything it depends on. Kahn's algorithm:

1. Count each job's unfinished dependencies (its **in-degree**).
2. Every job with a count of 0 is ready.
3. When a job finishes, subtract 1 from the count of each job waiting on it. Any that hit 0 become
   ready.
4. If you run out of ready jobs before every job has been handled, there's a cycle.

The interview graph:

```
A ──┐
    ├──► C ──► E          in-degrees: A 0, B 0, D 0, C 2, E 1
B ──┘
D
```

The parallel version is the same idea, with "finishes" meaning the job's `Task` completed, and many
jobs in flight at once. Working out how to do that safely (shared counts, the concurrency cap,
failures, cancellation) is the exercise.

## Later (not now)

Ideas for turning this into a NuGet package PacmanManager could use. Each one changes the API and the
tests, so leave them until the practice version works:

- `DependencyCycleException` names the jobs in the cycle (for error messages like "foo-git → bar →
  foo-git")
- `Skipped` records *why* each job was skipped (which failed dependency)
- Cancellation returns what had finished, instead of throwing it away
- Validate the graph and get the order without running anything (a dry run)
- Progress events (started / finished / failed) for logging or a UI
- Maybe: jobs passing results to the jobs that depend on them (a generic `Job<TResult>`). That's the
  biggest API change
- Package metadata (ID, version, license, README, XML docs); check the name isn't taken on nuget.org
