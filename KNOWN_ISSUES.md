# Known issues

Open items from code review of `JobGraph/JobScheduler.cs` (as of c86442e). Where a test can show
an issue, the test is named under it. Those tests are in the "Edge cases found in code review"
section of `JobGraph.Test/JobSchedulerTests.cs`. A test for an open bug fails until the bug is fixed.

At c86442e the build has no warnings and all 28 tests pass.

## 1. Tidying left over from the warnings fix

No test.

- `TryGetByName`'s `out job!` (`JobScheduler.cs:200`) hides the nullable warning rather than
  fixing it. `[MaybeNullWhen(false)] out Job job` says what actually happens. It would then warn at
  line 62, where `failingJob` is used without checking the result, which is the real gap (it can't
  be null there in practice, so an assertion or `!` at that call is the honest place for it).
- `TreatWarningsAsErrors` isn't set in either csproj, so the warnings can come back unnoticed.
- Trailing whitespace on lines 60, 73, 74, 87, 197 and 233. Line 87 is new in 711b883: a blank
  line became whitespace-only.
- `Unstarted()` is built twice in a row (lines 56 and 58), and there's a stray blank line before
  the `}` of `StartNext` (line 239).
- The comment at line 61 says a cycle must exist, but an unknown dependency also gets there.

## 2. Cycle message repeats the job name

No test (cosmetic). The message is now
`Dependency cycle detected in cyc-a: [cyc-a -> cyc-b -> cyc-c -> cyc-a]`. That's correct, but
"detected in cyc-a" just repeats the first entry of the path. `Dependency cycle: cyc-a -> cyc-b ->
cyc-c -> cyc-a` would say the same thing once. The README's Later list still has "names the jobs in
the cycle" as future work; that's done now apart from moving the names into a property of the
exception rather than only the message.

## 3. A job that ignores cancellation now holds up `RunAsync` (by design)

Test: `Cancelled_WaitsForRunningJobsBeforeThrowing` (passes).

Since 711b883, `RunAsync` waits for running jobs before throwing `OperationCanceledException`. The
flip side is that a job that ignores its token keeps `RunAsync` from returning for as long as it
runs. That's the right trade (callers can rely on nothing still running), but neither the README nor
the `RunAsync` doc comment says `RunAsync` waits, so a caller can't know. One sentence in each would
cover it.

## 4. Cancellation at the very end discards a finished run (by design)

Test: `CancelledAfterEveryJobFinished_StillThrows_ByDesign` (passes; it pins the current behaviour).

The `ThrowIfCancellationRequested()` after the loop throws even when every job has already
finished, if cancellation arrived at the last moment. That matches the README's rule ("once it's
cancelled ... `RunAsync` throws"), so it isn't a bug. "Cancellation returns what had finished" in
the README's Later list would change it, and the test with it.

## Resolved

- **`RunAsync` threw while jobs were still running** (711b883). On cancellation it stopped reading
  completions and returned while started jobs carried on. It now stops starting jobs, keeps draining
  completions until none are running, then throws. Test: `Cancelled_WaitsForRunningJobsBeforeThrowing`.
- **Cycle message was backwards and included jobs outside the cycle** (8faf3c5). The path now runs
  in dependency order, starts and ends at the repeated job, and leaves out the jobs leading into the
  cycle. Test: `CycleMessage_NamesJustTheCycle_InOrder`.
- **Compiler warnings** (c86442e). Both nullable warnings are gone and the unused
  `System.Collections.Immutable` using is removed. See issue 1 for what's left.
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
