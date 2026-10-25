# Server tests and game data

Most server behaviour reads the client's own tables (`data/config`, `data/sceneeditor`, `data/skills`, `data/nav`,
`msgtable.tsv`), which you export from your own client and which are not in git. A server test therefore runs on one of
two data sets:

| Data set | Attribute | Runs in CI | Use it for |
|---|---|---|---|
| The synthetic fixture, `tests/Ro3Emu.Tests/Fixtures/GameData` | `[Fact]` / `[Theory]` | yes | rules that hold whatever the content is: a failed save never loses a reward, a restart settles once, who owns what, escrow and receipts, reconnects |
| The client's export, `data/` | `[ClientDataFact]` / `[ClientDataTheory]` | no (skipped) | the real content: a real template's award, a boss's skills, a quest chain, a shop's prices |

Prefer the fixture. Use the client's data only when the assertion is about the real numbers or rows, and keep such a
test next to its fixture sibling rather than moving the rule test onto client data.

## Writing a test on the fixture

Start a whole server on it:

```csharp
[Fact]
public async Task AClaimIsGrantedOnce()
{
    await using var host = await SyntheticHost.StartAsync(o => o.SeasonStart = "2026-10-05");
    await using var alice = await host.InWorldAsync("Alice");   // logs in, creates a role, enters the town (10111)
    var mail = host.Service<MailService>();
    ...
    await host.RestartAsync();                                  // same data dir, new server
}
```

Or keep a class's own host setup and point it at the fixture: `o.GameDataDir = SyntheticData.Dir`. Always set
`GameDataDir` (also for a bare `AccountStore`): without it the server looks for `data/config` in the folders above the
test binaries and silently uses your client's export, so the test passes on your machine and not in CI.

When a class has both kinds of tests, put the host and helpers in an abstract base with an abstract `GameDataDir` and
derive one class per data set (see `GuildHuntingHost`, `MailTestsHost`, `AuctionTestsHost`).

## Adding to the fixture

- Every value is invented. Never copy rows from the client's tables: they are the publisher's data. Ids the server
  code names (an activity, a mail template, the starter kit's items) have to match, everything else is made up.
- Add only the rows and fields the code under test reads, with the field names the server reads (`GameData.Int(row,
  "_iFoo")`). A table is a JSON object keyed by id, or an array of rows with `_iID`. A missing table is empty.
- The server log names every table it looked for and did not find (`No ...\config\X.json`): that is the list to check
  when a fixture test fails.
- Once `MapConfig` has a row, scenes it does not know are refused; add the map (and a scene editor file with its born
  point) before warping to it. There is no `msgtable.tsv` (unrouted requests get no reply) and no nav grid (every map
  is walkable everywhere).
- `SyntheticFixtureTests` preloads every table, so a malformed file fails there first.

What it holds today: the town map 10111 and the guild home 4000101 with their scene editor points; the starter kit,
the currencies 1, 2, 102 and the virtual EXP items 100/101; bag capacities and a three-level EXP curve; the mail
templates of mail, market, auction and Guild Hunt; one auction lot item with its `AuctionConfig` row; the guild icons;
the Hunting Challenge schedule, boss, rewards and award groups; the six denomination tables the economy catalogue reads;
one Tavern room type (`BarroomTypeConfig`).

## Checking what CI runs

`RO3EMU_NO_CLIENT_DATA=1` makes `ClientData.Available` false on a machine that has the export, so the client-data
tests skip as they do in CI:

```bash
RO3EMU_NO_CLIENT_DATA=1 dotnet test tests/Ro3Emu.Tests --logger "trx;LogFileName=server.trx" --results-directory TestResults
pwsh tools/test-summary.ps1 -Results TestResults   # server: N executed (0 failed), M skipped (M need client data)
```

CI prints the same line per test project in its job summary.

## Running tests while you work

CI runs every test on Linux and Windows for each push, so you don't need the whole suite for each change. Run what
your change touches:

```bash
tools/test-changed.sh              # builds, then runs the tests of what changed since origin/main
tools/test-changed.sh --no-build   # you just built
tools/test-changed.sh --print      # only show what it would run
```

It compares your branch (committed, staged, unstaged and untracked files) with its merge base on `origin/main`:

- a test file runs its test classes;
- a server, bot or load-test `.cs` file runs, for each type it declares, the test class named after it
  (`MailService` -> `MailTests`, `GameLoop` -> `GameLoopTests`) and every test class whose file names the type. Over 30
  classes it keeps the ones named after the type; `--wide` runs them all;
- a server data file (`Reconstructed/*.json`) counts as the code that names it;
- the patcher and the setup tool run their own test project; docs and client-side projects run nothing;
- a change that can reach every test (a project file or `Directory.*.props`, the fixture, `content/`, a deleted file,
  types no test names) runs the CI set instead. `--all` runs the CI set whatever changed.

The client-data tests of the picked classes run when you have the export. On Windows run the script from Git Bash, or
pick the classes yourself:

| What | Command | Time on 4 cores |
|---|---|---|
| A few classes | `dotnet test tests/Ro3Emu.Tests -c Release --no-build --filter "FullyQualifiedName~Ro3Emu.Tests.TavernTests."` | seconds |
| One big class | `--filter "FullyQualifiedName~Ro3Emu.Tests.DungeonTests."` (130 tests) | ~1 min |
| What CI runs | `RO3EMU_NO_CLIENT_DATA=1` with `--filter "Category!=Slow"` (955 tests) | ~70 s |
| The whole suite | `--filter "Category!=Slow"` (2,325 tests) | ~7.5 min |

Join several classes with `|`: `--filter "FullyQualifiedName~PetTests.|FullyQualifiedName~PetAoiTests."`; the dot
after the name keeps `PetTests` from matching `PetTestsHost`. Run the whole suite yourself when a change reaches most
of the server (the game loop, sessions, persistence, `GameData`), and the `Category=Slow` walks (`QuestWalkTests`
alone takes ~3.5 min) when you change what they walk.

### Why a run takes as long as it does

Every server test starts its own server, so a whole run starts about 2,000 of them. The test project shares what
those servers would otherwise each redo (`tests/Ro3Emu.Tests/TestRunSetup.cs`): the parsed client tables, scene
editors and nav grids (`ProcessCache`), one gate RSA key, and a lower password hash count. Because the nav grids are
shared, so is the Fly Wing reachability flood fill each server ran per map (`NavGrid.Reachable`), which was ~40% of
the run's CPU. The project also turns off profile-guided recompilation (`TieredPGO`, about 30% of the run's CPU). None
of this changes how a real server runs.

Most tests then wait on game time (ticks, cooldowns, casts) rather than compute, which leaves the cores idle:

- `xunit.runner.json` runs two test classes per core (`maxParallelThreads: 2x`). On 4 cores that took the whole suite
  from 8m53s to 7m56s; before the flood fill was shared, the run was CPU-bound and 8 threads were slower than 4.
- `SplitClassFramework` runs a class of more than 16 tests as parts of 16 at once, since xUnit runs one class's tests
  one after another: DungeonTests alone went from 3.5 min to 1 min, SkillCombatTests from ~6.5 min to 75 s, and the
  whole suite to 7m22s. A named `[Collection]` stays in one part.
- `LongestFirstOrderer` starts the longest classes first.

The whole suite fits in one run in 16 GB (peak ~8 GB). A few tests depend on timing and can fail when the machine is
busy. Re-run a failing test on its own before you chase it.

## Snapshot tests

Some tests compare what the server does with a checked-in list in `tests/Ro3Emu.Tests/Snapshots/`:

- `dispatcher-admission.txt`: how the dispatcher admits every message of the client's table (client data).
- `receipt-field-names.txt`: the field and property names that `EventPlanFingerprint` and `EconomyChildIntent`
  write into receipts for a `DbPlan` and the values it holds. A rename makes an operation recorded before it refuse
  its replay, so this one runs without client data, in CI too.

A failing snapshot test lists what changed. Regenerate the file with `RO3EMU_UPDATE_SNAPSHOTS=1` only for an intended
change, and say in the pull request why it is safe.
