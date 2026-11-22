# Known issues

Open items from code review of `JobGraph/JobScheduler.cs` (as of 0bc0dd9). Where a test can show
an issue, the test is named under it. Those tests are in the "Edge cases found in code review"
section of `JobGraph.Test/JobSchedulerTests.cs`, and they fail until the issue is fixed.

## 1. `RunAsync` throws while jobs are still running

Test: `Cancelled_WaitsForRunningJobsBeforeThrowing` (fails).

On cancellation, `completions.Reader.ReadAsync(cancellationToken)` throws straight away, so
`RunAsync` exits while started jobs are still running. Their results go into a channel nobody reads.
The README doesn't say whether `RunAsync` waits for running jobs, so this may be acceptable. But a
caller (e.g. PacmanManager cleaning up a build directory) would reasonably assume nothing is still
running once `RunAsync` has finished. If you decide not waiting is fine, delete the test.

Fix, if wanted: on cancellation, stop starting jobs but keep draining `completions` (with
`CancellationToken.None`) until `NumRunning == 0`, then throw.

## 2. Cycle exception message

Test: `CycleMessage_NamesJustTheCycle_InOrder` (fails). Currently, for
lead-in → cyc-a → cyc-b → cyc-c → cyc-a, the message is
`Dependency cycle detected in lead-in: [cyc-c, cyc-b, cyc-a, lead-in, cyc-a]`.

- `chain` is a `Stack`, and enumerating a `Stack` goes top-first, so the path prints **backwards**
  with the repeated job in the wrong place. `chain.Reverse()` (or building the list bottom-up)
  fixes the order.
- It includes the jobs leading *into* the cycle, not just the ones in it, and "detected in
  {job.Name}" names the starting job, which may not be part of the cycle. Trimming the path to start
  at `dep.Name` gives just the cycle, which gets most of the way to the "foo-git → bar → foo-git"
  message in the README's Later list.

## 3. Compiler warnings and tidying

No test. `TreatWarningsAsErrors` in the csproj would stop the warnings coming back.

- Two nullable warnings:
  - `JobScheduler.cs:54`: `StartNext()` returns `Job?`, and the result is passed to `Complete(Job)`.
  - `JobScheduler.cs:195`: `TryGetByName`'s `out Job` needs `[MaybeNullWhen(false)]`.
- `using System.Collections.Immutable;` is unused.
- Trailing whitespace on lines 61, 74, 75, 192 and 228.
- `Unstarted()` is built twice in a row (lines 57 and 59), and there's a stray blank line before
  the `}` of `StartNext`.
- The comment at line 62 says a cycle must exist, but an unknown dependency also gets there.

## 4. Cancellation at the very end discards a finished run (by design)

Test: `CancelledAfterEveryJobFinished_StillThrows_ByDesign` (passes; it pins the current behaviour).

The `ThrowIfCancellationRequested()` after the loop throws even when every job has already
finished, if cancellation arrived at the last moment. That matches the README's rule ("once it's
cancelled ... `RunAsync` throws"), so it isn't a bug. "Cancellation returns what had finished" in
the README's Later list would change it, and the test with it.

## Resolved

- **Cancellation could make `RunAsync` return normally instead of throwing** (0bc0dd9). If
  cancellation arrived after the last running job's result was read, the loop broke out and
  returned, reporting never-started jobs as `Skipped`. Fixed by throwing if cancellation was
  requested before returning. Test: `CancelledJustAfterTheLastRunningJobFinishes_StillThrows`. It's
  a race, so the test repeats 500 times; it failed within 2 attempts against f82271e.
- **Validation fallback relied on `CheckCycles` throwing** (0c1d52d). `RunAsync` now throws if
  `CheckCycles` returns. No test: that line is only reachable if `CheckCycles` is broken.
- **`CheckCycles` repeated work and had no depth limit** (327e9be). Validation is now one Kahn's
  pass, and `CheckCycles` runs only once, from a job that was left over, using an explicit stack.
- **`Task.WhenAny(running)` cost O(running) per completed job** (327e9be). Replaced by a `Channel`
  that each `RunJob` writes to on completion.
- **Message typo** "cycled detected" (327e9be).
