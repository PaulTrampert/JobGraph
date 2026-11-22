# Known issues

Open items from code review of `JobGraph/JobScheduler.cs` (as of 327e9be). All 24 tests pass; none
of these are caught by the current tests.

## 1. Cancellation can make `RunAsync` return normally instead of throwing

If cancellation arrives after the last running job's result has been read from the channel, but
before the next start, the inner `while` starts nothing, `NumRunning == 0` hits the `break`, and
`RunAsync` returns a `ScheduleResult`. It doesn't throw `OperationCanceledException`, so it breaks
the README's rule ("once it's cancelled ... `RunAsync` throws"). Jobs that were never started
because of the cancellation are then reported in `Skipped`, which is meant to mean "a dependency
failed".

Reproduced with A → B, where A triggers `cts.Cancel()` from a background task just after it
returns: in 2000 runs, 775 returned `Completed=[A], Skipped=[B]` and didn't throw.
`CancelledMidRun_NoNewJobsStart` doesn't catch it because its job cancels *before* finishing, so
`ReadAsync` sees the token already cancelled.

Fix: call `cancellationToken.ThrowIfCancellationRequested()` after the loop (the previous version
had this; the rework dropped it), or before the `break`.

## 2. `RunAsync` throws while jobs are still running

On cancellation, `completions.Reader.ReadAsync(cancellationToken)` throws straight away, so
`RunAsync` exits while started jobs are still running. Their results go into a channel nobody reads.
The README doesn't say whether `RunAsync` waits for running jobs, so this may be acceptable. But a
caller (e.g. PacmanManager cleaning up a build directory) would reasonably assume nothing is still
running once `RunAsync` has finished. Reproduced: `RunAsync` threw while a 300 ms job was still
going.

Fix, if wanted: on cancellation, stop starting jobs but keep draining `completions` (with
`CancellationToken.None`) until `NumRunning == 0`, then throw.

## 3. Cycle exception message

- The typo is fixed ("cycle detected").
- `chain` is a `Stack`, and enumerating a `Stack` goes top-first, so the path prints **backwards**
  with the repeated job in the wrong place: for A → B → C → A it prints `[C, B, A, A]` rather than
  `[A, B, C, A]`. `chain.Reverse()` (or building the list bottom-up) fixes the order.
- It still includes the jobs leading *into* the cycle, not just the ones in it, and "detected in
  {job.Name}" names the starting job, which may not be part of the cycle. Trimming the path to start
  at `dep.Name` gives just the cycle, which gets most of the way to the "foo-git → bar → foo-git"
  message in the README's Later list.

## 4. Validation fallback relies on `CheckCycles` throwing

- The comment at `JobScheduler.cs:62` says a cycle must exist, but a job left unstarted can also be
  caused by an unknown dependency. `CheckCycles` handles both cases correctly; only the comment is
  wrong.
- If `CheckCycles` ever returns without throwing (a future bug), `RunAsync` carries on and runs a
  graph it has just found to be broken. Throwing `UnreachableException` after the call makes that
  fail loudly.

## 5. Compiler warnings and tidying

- Two nullable warnings:
  - `JobScheduler.cs:54`: `StartNext()` returns `Job?`, and the result is passed to `Complete(Job)`.
  - `JobScheduler.cs:193`: `TryGetByName`'s `out Job` needs `[MaybeNullWhen(false)]`.
- `using System.Collections.Immutable;` is unused.
- Trailing whitespace on lines 61, 73, 74, 190 and 226.
- `Unstarted()` is built twice in a row (lines 57 and 59), and there's a stray blank line before
  the `}` of `StartNext`.

## 6. Cancellation at the very end discards a finished run (by design, once #1 is fixed)

Once #1's `ThrowIfCancellationRequested()` is back, it throws even when every job has already
finished, if cancellation arrived at the last moment. That matches the README's rule, so it isn't a
bug. "Cancellation returns what had finished" in the README's Later list would change it.

## Resolved in 327e9be

- **`CheckCycles` repeated work and had no depth limit.** Validation is now one Kahn's pass, and
  `CheckCycles` runs only once, from a job that was left over, using an explicit stack, so it's
  O(n + e) and can't overflow the stack.
- **`Task.WhenAny(running)` cost O(running) per completed job.** Replaced by a `Channel` that each
  `RunJob` writes to on completion.
- **Message typo** ("cycled detected"). See #3 for what's left.
