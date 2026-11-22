# Known issues

Open items from code review of `JobGraph/JobScheduler.cs` (as of 0a6174e). Where a test can show
an issue, the test is named under it. Those tests are in the "Edge cases found in code review"
section of `JobGraph.Test/JobSchedulerTests.cs`. A test for an open bug fails until the bug is fixed.

At 0a6174e the build has no warnings and all 28 tests pass.

## 1. `UnknownJobException` adds public API for a can't-happen case

No test (unreachable).

`GetByName` (`JobScheduler.cs:209`, from ddbf495) throws the new `UnknownJobException`. Its only
caller passes a name taken from `Unstarted()`, which only holds names that are in the collection, so
the throw can't happen. The exception is still `public`, which makes it part of the API even though
no caller can ever catch it. The README says not to redesign the API yet. It also doesn't match the
other two exceptions: they're `sealed` and expose the names as properties. Possible fixes:
- make it `internal sealed`,
- or drop it and use `_jobsByName[jobName]`, whose `KeyNotFoundException` is just as clear for a
  "can't happen".

## 2. Remaining tidying

No test.

- `TreatWarningsAsErrors` still isn't set in either csproj, so the warnings can come back unnoticed.
- The comment at line 65 still says a cycle must exist, but an unknown dependency also gets there.
- `JobScheduler.cs` and `UnknownJobException.cs` have no newline at the end of the file
  (`JobScheduler.cs` lost its trailing newline in the 0a6174e reformat).

## 3. Cycle message repeats the job name

No test (cosmetic). The message is now
`Dependency cycle detected in cyc-a: [cyc-a -> cyc-b -> cyc-c -> cyc-a]`. That's correct, but
"detected in cyc-a" just repeats the first entry of the path. `Dependency cycle: cyc-a -> cyc-b ->
cyc-c -> cyc-a` would say the same thing once. The README's Later list still has "names the jobs in
the cycle" as future work; that's done now apart from moving the names into a property of the
exception rather than only the message.

## 4. A job that ignores cancellation now holds up `RunAsync` (by design)

Test: `Cancelled_WaitsForRunningJobsBeforeThrowing` (passes).

Since 711b883, `RunAsync` waits for running jobs before throwing `OperationCanceledException`. The
flip side is that a job that ignores its token keeps `RunAsync` from returning for as long as it
runs. That's the right trade (callers can rely on nothing still running), but neither the README nor
the `RunAsync` doc comment says `RunAsync` waits, so a caller can't know. One sentence in each would
cover it.

## 5. Cancellation at the very end discards a finished run (by design)

Test: `CancelledAfterEveryJobFinished_StillThrows_ByDesign` (passes; it pins the current behaviour).

The `ThrowIfCancellationRequested()` after the loop throws even when every job has already
finished, if cancellation arrived at the last moment. That matches the README's rule ("once it's
cancelled ... `RunAsync` throws"), so it isn't a bug. "Cancellation returns what had finished" in
the README's Later list would change it, and the test with it.

## Resolved

- **Nullability of `TryGetByName` was hidden with `!`** (ddbf495). It now uses
  `[MaybeNullWhen(false)]`, and the validation fallback calls the new `GetByName` instead of
  ignoring `TryGetByName`'s result. See issue 1 for the exception `GetByName` throws.
- **Tidying** (ddbf495, 0a6174e). `Unstarted()` is built once, the trailing whitespace and the
  stray blank line in `StartNext` are gone, and the file has been reformatted.
- **`RunAsync` threw while jobs were still running** (711b883). On cancellation it stopped reading
  completions and returned while started jobs carried on. It now stops starting jobs, keeps draining
  completions until none are running, then throws. Test: `Cancelled_WaitsForRunningJobsBeforeThrowing`.
- **Cycle message was backwards and included jobs outside the cycle** (8faf3c5). The path now runs
  in dependency order, starts and ends at the repeated job, and leaves out the jobs leading into the
  cycle. Test: `CycleMessage_NamesJustTheCycle_InOrder`.
- **Compiler warnings** (c86442e). Both nullable warnings are gone and the unused
  `System.Collections.Immutable` using is removed.
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
