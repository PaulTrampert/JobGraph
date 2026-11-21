# Known issues

Open items from code review of `JobGraph/JobScheduler.cs`. All 24 tests pass; none of these are
caught by the current tests.

## 1. `CheckCycles` repeats work and has no depth limit

- `RunAsync` calls `CheckCycles` once per job, and each call creates **new** `visiting` / `done`
  sets. Every call walks that job's whole dependency tree again, so a chain of n jobs costs O(n²).
  Fix: create `done` once before the loop and pass it into every call, which brings it down to
  O(n + e).
- It's recursive, so a long enough chain (tens of thousands of jobs) overflows the stack. Fix: use
  an explicit stack, or detect cycles the Kahn's way (no ready jobs left, but fewer jobs processed
  than the total).

## 2. Cycle exception message

- The message says "cycled detected" instead of "cycle detected".
- `visiting` is a `HashSet`, which doesn't promise insertion order (especially after removals), so
  the path in the message can come out in the wrong order. It also includes the jobs that lead
  *into* the cycle, not just the ones in it. A `List<string>`/`Stack<string>` path fixes both, and
  gets most of the way to the "foo-git → bar → foo-git" message in the README's Later list.

## 3. `Task.WhenAny(running)` costs O(running) per completed job

`WhenAny` re-registers a continuation on every running task each time round the loop. With a
concurrency cap that's fine. With `int.MaxValue` and thousands of independent jobs, the total is
O(n²). A `Channel` that each `RunJob` writes its result to on completion avoids it. Low priority
for this exercise.

## 4. Cancellation at the very end discards a finished run (by design)

The `ThrowIfCancellationRequested()` after the loop throws even when every job has already
finished, if cancellation arrived at the last moment. This matches the README's rule ("once it's
cancelled ... `RunAsync` throws"), so it isn't a bug. "Cancellation returns what had finished" in
the README's Later list would change it.

## 5. Whitespace

`JobScheduler.cs:76` (the blank line after `var running = ...`) has trailing whitespace.
