# JobGraph

Practice project: a dependency-aware job scheduler that runs independent jobs in parallel.
Implement `JobScheduler.RunAsync` in `JobGraph/JobScheduler.cs` until every test passes.
Its doc comment is the spec; the tests in `JobGraph.Test/JobSchedulerTests.cs` pin it down.

    dotnet test
    dotnet test --filter "Name~Cycle"     # just one area

The tests are grouped in stages, roughly easiest first:

1. **Validation:** duplicates, unknown dependencies and cycles are rejected before anything runs.
2. **Ordering:** every job runs once, after its dependencies.
3. **Parallelism:** independent jobs overlap, a job starts as soon as *its own* dependencies are done
   (not in waves), and `maxConcurrency` is respected.
4. **Failure:** a failed job skips everything downstream of it, and nothing else.
5. **Cancellation:** no new job starts once the token is cancelled.

The Stage 3 tests use gates: a job waits until another one has started. If the two never run at the
same time, the gate times out after 2 seconds and the job fails with a message saying so. Every run
also has a 10-second timeout, so a deadlock fails the test instead of hanging it.

Test helpers are in `JobGraph.Test/Recorder.cs`.
