# Party EXP share

## Objective
Share monster-kill Base EXP among party members (Konsor request), instead of only the killer receiving it.

## Problem / why
`RewardService` grants kill Base EXP only to the killer; `TeamService.ExpBonus` only multiplies the killer's EXP by the
same-map member count (TeamExpBuffNum). Party members get nothing, so partying for leveling is not rewarding.

## Scope and constraints
- Rule (inferred, not from the client): "even share". Total kill Base EXP (after level-gap correction and the existing
  party bonus) is split equally among online party members on the same map (WorldScene) as the killer; each share is at
  least 1. Pursue/catch-up bonus (`WithPursueBonus`) applies per recipient by their own level.
- Behind a server option, default off (1:1 rule in AGENTS.md); marked `(inferred)` in code.
- Job EXP and item drops unchanged (killer only).
- Game state changes only on the game loop; each recipient updated under its own session lock.

## Tasks
- [x] T1 Server option + even-share EXP distribution in kill rewards, with tests (route: delegated writer — touches
      RewardService, ServerOptions, tests; 2+ non-trivial files).

## Acceptance criteria
- Option off: behaviour identical to today (existing tests pass).
- Option on, party of N on the same map: each member gets floor(total/N) (min 1) Base EXP; members on another map or
  offline get nothing; solo player unchanged.

## Checks
- `dotnet build src/Ro3Emu.Server/Ro3Emu.Server.csproj -c Release`
- `dotnet test tests/Ro3Emu.Tests -c Release --filter "Category!=Slow"` (focused filter during iteration)
- `dotnet format whitespace --folder --verify-no-changes` on touched files

## Progress
- Branch `feat/party-exp-share` created from `feat/purchase-url` (user WIP left untouched, uncommitted).

- T1 done (delegated writer). Files: `ServerOptions.cs` (`PartyExpShare`, default false, `(inferred)`),
  `TeamService.cs` (`SameMapMembers`), `RewardService.cs` (split in `ResolveKill` incl. test hook path; other members
  granted after the kill commit via `CharacterService.AddExpAsync`, own lock each, no nested locks),
  `docs/guide/hosting.md` (option line), `tests/Ro3Emu.Tests/PartyExpShareTests.cs` (off / on same map / other map).
- Test-first: RED observed (`OnSplitsBaseExpEvenlyOnTheSameMap`: expected (2, 4), actual (5, 4)), then GREEN.
- Verification: Release build 0 warnings 0 errors; focused filter 6 passed / 21 skipped (client-data); fast suite
  939 passed / 0 failed / 1161 skipped; `dotnet format whitespace --verify-no-changes` on touched .cs files clean.

## Next step
Parent: commit T1, native review assessment.
