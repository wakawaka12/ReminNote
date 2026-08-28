# ReminNote — Master Development Plan for Codex

> Status: **Requirements interview complete / P0–P3 scope frozen**
>
> Plan version: **2026-08-27**
>
> Primary target: **Windows 10 22H2 x64 / Windows 11 x64**
>
> Client license target: **GPL-3.0-or-later**
>
> Future self-hosted sync server license target: **AGPL-3.0-or-later**

---

# 0. How Codex must use this document

This document is the current source of truth for ReminNote development.

Before modifying code, Codex must:

1. Inspect the real repository first.
2. Read this document plus:
   - `PRODUCT_RULES.md`
   - `ARCHITECTURE.md`
   - `DEVELOPMENT.md`
   - `TESTING.md`
   - `DECISIONS.md`
   if those files already exist.
3. Compare the repository's actual state with the current Slice.
4. Do not assume the repo matches this plan.
5. Do not invent product rules not stated here.
6. Do not reopen settled product questions unless a true blocker, architecture conflict, or data-safety conflict is discovered.
7. Do not implement future phases early merely because they are described here.
8. Keep each change limited to the active Vertical Slice.
9. Prefer proven open-source libraries/code where suitable, but only after license, maintenance, security, and fit review.
10. Never commit, push, tag, release, rebase, or force-push unless the user explicitly asks.

The user performs most end-to-end testing manually. After every meaningful change, Codex must provide exact manual test instructions:

- how to start the app;
- where to click;
- what to enter;
- what should happen;
- what counts as failure.

Automated tests should be focused on logic that benefits from automation rather than mechanically testing every UI detail.

---

# 1. Product definition

ReminNote is a **Windows desktop reminder + personal record application**.

The product has two primary domains:

1. `TASK / TODAY`
2. `ANIME`

The product is intentionally not a general project-management suite, time tracker, streaming client, or cloud SaaS.

The first usable Alpha is reached after **P3**, when the local Task + Reminder loop is stable.

---

# 2. Non-negotiable product boundaries

## 2.1 TASK is planning, not time tracking

A task represents what the user plans to do.

There are exactly three task time shapes:

```text
ANYTIME
TIME
RANGE
```

Examples:

```text
ANYTIME: 今天整理房间
TIME:    12:00 吃钙片
RANGE:   14:00–17:00 写代码
```

Do not add a fourth time state.

Do not evolve Task into a work-session tracker.

Do not record:

- real start time;
- real finish time;
- pause/resume;
- elapsed work duration;
- stopwatch sessions;
- task sessions.

A `TIME` value is a **reminder/planning anchor**, not a hard deadline.

A `RANGE` means:

> I plan to do this during this time range.

---

# 3. Task domain rules

## 3.1 Domain time model

Use:

```text
TaskTimeType
  ANYTIME
  TIME
  RANGE
```

Represent time semantics in the domain with a `TimeSpec` value-object family:

```text
AnytimeSpec
TimePointSpec
TimeRangeSpec
```

Invalid mixed states must be impossible or rejected.

## 3.2 Persistence model

Use one relational `Task` model/table, not separate tables per time type.

Expected relational time columns conceptually include:

```text
TaskTimeType
LocalDate
TimePoint
RangeStart
RangeEnd
```

Use database constraints so only legal combinations can exist.

Do not add a separate cross-midnight boolean.

For:

```text
23:00 -> 01:00
```

derive cross-midnight from:

```text
RangeEnd < RangeStart
```

## 3.3 Time storage

Local planning values are local civil values:

```text
2026-08-27
14:00:00
```

Absolute events use UTC / Unix-compatible instants.

Absolute events include at least:

- anime airing;
- `RecordedAt`;
- reminder trigger times;
- change journal timestamps;
- sync timestamps.

Use **Noda Time** as the Core time model.

Business code must not scatter:

```csharp
DateTime.Now
DateTime.Today
```

Use abstractions such as:

```text
IClock
IUserTimeZoneProvider
IWorkdayService
```

---

# 4. Task result rules

## 4.1 ANYTIME / TIME

Final results:

```text
COMPLETED
MISSED
```

## 4.2 RANGE

Final results:

```text
COMPLETED
PARTIAL
MISSED
```

When the range ends and the user has not confirmed a result, the task is:

```text
AWAITING RESULT
```

Do not automatically mark it `MISSED`.

## 4.3 PARTIAL

`PARTIAL` may include an optional short result note.

Example:

```text
做到第三章，剩下明天继续
```

No completion percentage is required.

Checklist state is preserved naturally.

## 4.4 RecordedAt

`RecordedAt` means:

> when the user recorded the result in ReminNote.

It must never be presented as the real-world completion time.

Do not mislead the user with UI such as:

```text
Completed at 16:37
```

when that timestamp only means the button was clicked then.

---

# 5. RANGE behavior

For:

```text
14:00–17:00 写代码
```

Before start:

```text
UPCOMING
```

Inside the range:

- show the task as currently planned;
- do not imply ReminNote knows whether the user is actively doing it.

After the range with no result:

```text
AWAITING RESULT
```

User may then choose:

```text
COMPLETED
PARTIAL
MISSED
```

The user may also mark the task done early.

The original planned range remains immutable as the historical plan:

```text
14:00–17:00
```

Do not rewrite it into a supposed actual completion period.

---

# 6. Rescheduling and continuation

If a future plan has not started, the user may directly change its date/time.

Do not manufacture a failed historical record for a future plan that was simply edited.

If the plan has already happened, editing must not erase history.

Example:

```text
Aug 27 20:00–22:00 写文档 -> PARTIAL
```

If work continues tomorrow, create a new plan:

```text
Aug 28 19:00–20:30 继续写文档
```

and maintain a relation equivalent to:

```text
ContinuedFrom
ContinuedTo
```

The original historical item remains on the original date.

---

# 7. Cross-midnight behavior

For:

```text
Aug 27 23:00 -> Aug 28 01:00
```

completion during that planned interval still belongs to the **Aug 27 plan**.

Do not move ownership to the next day merely because midnight passed.

---

# 8. Logical workday boundary

The user may configure a ReminNote workday boundary.

Default:

```text
00:00
```

Supported configuration should allow values such as:

```text
01:00
02:00
03:00
04:00
custom
```

This boundary affects:

- TODAY;
- daily statistics;
- rollover/review behavior;
- ANYTIME day completion.

It does **not** change:

- real calendar date;
- absolute reminder trigger timestamps.

When the workday boundary passes, background state may roll over silently.

Do not pop an intrusive midnight window.

On next meaningful user activity, ReminNote may show:

```text
YESTERDAY · NEEDS REVIEW · N
```

Meaningful activity includes:

- unlock;
- widget interaction;
- tray interaction;
- opening Main App.

---

# 9. Reminder is separate from task planning time

This is a core invariant:

> planned time != notification time

Example:

```text
今天整理房间
```

is still `ANYTIME` even if the user adds:

```text
18:00 remind me
```

Do not convert it to `TIME`.

---

# 10. Default reminder rules

## 10.1 ANYTIME

No mandatory clock-time notification by default.

It may still appear in:

- TODAY;
- daily digest;
- user-configured reminders.

## 10.2 TIME

Default reminder:

```text
TaskTime - global lead time
```

Example:

```text
12:00 task
global lead time = 10m
-> 11:50 reminder
```

Per-task override is allowed.

## 10.3 RANGE

May support:

- pre-start reminder;
- optional start reminder;
- end-of-range result prompt.

A task may have multiple reminder rules.

Normal UI should remain simple; advanced multi-reminder configuration can be expanded on demand.

---

# 11. Reminder architecture — frozen major decisions

Use three distinct layers:

```text
ReminderRule
    ->
ReminderSchedule
    ->
ReminderInstance
```

## 11.1 ReminderRule

Represents the business truth.

Example:

```text
TaskTime - 10m
```

The rule expresses why and how a reminder should exist.

## 11.2 ReminderSchedule

Represents derived executable scheduling data.

Conceptually includes:

```text
TriggerAtUtc
OccurrenceId
ScheduleRevision
```

A Schedule is rebuildable from the Rule.

The database stores both Rule and Schedule.

Rule is truth.
Schedule is derived execution state.

## 11.3 ReminderInstance

Represents a reminder that actually happened.

It records factual trigger history and lifecycle state.

Do not overload Schedule with both future planning and historical trigger semantics.

---

# 12. Reminder schedule revision behavior

When a task time or ReminderRule changes:

- do not mutate historical ReminderInstances;
- do not silently pretend an old Schedule never existed;
- invalidate the old pending Schedule;
- create a new revision.

Conceptually:

```text
Revision 4 | 17:50 | SUPERSEDED
Revision 5 | 18:50 | PENDING
```

Technical naming may be refined during implementation, but the behavior is fixed.

Already-triggered history is immutable.

---

# 13. Snooze semantics

Snooze must not modify the original ReminderRule.

Snooze must not rewrite the original schedule into a new meaning.

Create a derived temporary schedule for Snooze.

Example:

```text
18:00 original reminder -> TRIGGERED
user snoozes 30m
18:30 derived schedule -> PENDING
```

If snoozed again, create the next derived schedule.

The original business purpose remains intact.

Snooze should preserve the original reminder purpose and add a cause equivalent to:

```text
Cause = SNOOZE
```

Snooze is persisted and must survive process or machine restart.

---

# 14. Task completion vs future reminders

When a specific `TaskInstance` reaches a final result:

- cancel/invalidate all future pending ReminderSchedules for that TaskInstance;
- cancel/invalidate pending Snooze schedules for that TaskInstance;
- do not delete historical records;
- do not affect reminders belonging to future recurring instances.

Example:

```text
18:00 reminder triggered
18:00 snoozed to 18:30
18:10 task completed
18:30 schedule becomes cancelled/non-triggerable
```

---

# 15. Reminder semantic purpose

Reminder data must carry a semantic purpose/kind.

Examples may include:

```text
TASK_PRE_START
TASK_START
TASK_RANGE_END
TASK_CUSTOM
ANIME_PRE_AIRING
ANIME_AIRING
ANIME_CUSTOM
```

Exact enum names may be refined during implementation.

The important rule is that Scheduler/recovery logic must know **what a reminder means**, not merely when it fires.

This enables correct behavior after sleep/offline periods.

Example:

A pre-start reminder that is already hours late may be obsolete.

A range-end result prompt may still be meaningful after resume.

Snooze inherits the original purpose rather than inventing a new business purpose.

---

# 16. Reminder lifecycle

ReminderInstance lifecycle:

```text
UNREAD -> READ -> RESOLVED
```

Closing a Windows Toast:

```text
UNREAD -> READ
```

It does not mean the underlying reminder is resolved.

Resolution actions may include domain-specific actions such as:

```text
DONE
SNOOZE
WATCHED
WATCH_LATER
SKIP
IGNORE
```

Exact internal modeling can be decided during the P3 Slice.

`IGNORE` normally resolves only the current ReminderInstance.

It must not silently disable future reminder rules.

Advanced actions may separately support:

- silence reminders for today;
- mute until a time;
- disable this ReminderRule.

---

# 17. Reminder priority and pinning

Priority:

```text
LOW
NORMAL
HIGH
```

`PIN` is independent from priority.

Valid:

```text
PIN + HIGH
```

HIGH may use repeat behavior.

PIN + HIGH may use stronger repeat behavior.

Repeat settings may include:

- interval;
- maximum count;
- enabled/disabled.

Repeated Windows notifications should normally update the same logical Toast rather than flood the notification center.

Reminder History still records actual trigger attempts/events.

---

# 18. Scheduler

The Agent owns Reminder scheduling.

SQLite is the durable source for pending schedules.

At runtime, the Agent should maintain only the nearest required wakeup rather than creating one long-lived `Task.Delay()` per reminder.

Conceptually:

1. find next due schedule;
2. wait efficiently;
3. process all currently due schedules;
4. persist resulting instances/state;
5. recompute next wakeup.

After Agent restart, rebuild scheduler state from SQLite.

Commands must be idempotent.

---

# 19. Sleep / shutdown recovery

After resume or restart:

LOW/NORMAL:

- prefer summary behavior;
- do not create a chain of stale popups.

HIGH:

- may surface individually if still useful.

PIN + HIGH:

- may default to one strong reminder.

Anime:

- if airing already passed, move to WATCH LATER;
- do not send a stale “about to air” notification.

Recovery behavior must use Reminder purpose, not just timestamps.

---

# 20. Quiet Hours

Support multiple rules.

Examples:

```text
weekday 00:30–08:00
weekend 02:00–10:00
```

Also support temporary behavior equivalent to:

```text
Quiet until morning
```

During Quiet Hours:

- Scheduler still runs;
- Task state still evolves;
- Anime can still enter WATCH LATER;
- only notification presentation changes.

After Quiet Hours:

- normal reminders may be summarized;
- HIGH / PIN+HIGH may be elevated individually.

---

# 21. Notification channels and health

Reminder core must not depend on Windows Toast.

Toast is one channel.

Other channels include:

- Tray state/flash;
- Widget alert state;
- taskbar flash when appropriate.

The system must distinguish:

> reminder did not trigger

from:

> reminder triggered but a channel was unavailable/blocked

Notification health should be able to represent availability of:

- Windows Toast;
- Widget Alert;
- Tray;
- sound;
- wake timer.

If Windows notifications are disabled:

- inform the user without repeatedly nagging;
- keep reminder history truthful.

Conceptually history may show:

```text
TRIGGERED · WINDOWS TOAST BLOCKED
```

P3 acceptance must include functioning reminder state even if Toast is unavailable.

---

# 22. Wake timer

Wake-PC capability is supported eventually.

Global default:

```text
OFF
```

When enabled:

- LOW/NORMAL do not wake by default;
- HIGH may be configurable;
- PIN + HIGH may allow stronger defaults.

Per reminder:

```text
DEFAULT
YES
NO
```

Implementation detail is deferred until its Slice.

---

# 23. TODAY

Default grouping:

```text
OVERDUE
MORNING
AFTERNOON
EVENING
ANYTIME
COMPLETED
```

Alternative grouping modes:

```text
TIME
CATEGORY
PRIORITY
NONE
```

## NEEDS REVIEW

A finished RANGE without user result remains in its planned time position and also contributes to:

```text
NEEDS REVIEW · N
```

at the top of TODAY.

## OVERDUE

Avoid an overwhelming “wall of overdue tasks”.

General behavior:

- PIN + HIGH strongly elevated;
- HIGH expanded by default;
- NORMAL/LOW may collapse.

Exact visual thresholds are a development-time detail.

---

# 24. TODAY Widget

Conceptual information hierarchy:

```text
PRIMARY -> UPCOMING -> SUMMARY
```

Widget should automatically choose the most relevant current item.

Actions may include:

- DONE;
- context menu;
- Snooze;
- Reschedule;
- Move to tomorrow;
- PIN;
- SKIP;
- Open details.

Quick Add should expand directly inside the lightweight flow and reuse the same parsing/application service used by Main App.

For NEEDS REVIEW, Widget initially shows a compact count and opens a lightweight result panel.

---

# 25. Quick Add

Use deterministic syntax.

Do not build vague NLP in the first version.

Example:

```text
明天 18:00 买东西 #生活 !高
```

Syntax can support:

- date;
- time;
- tag;
- priority;
- template alias;
- repeat syntax.

Time shape should map deterministically to:

```text
ANYTIME
TIME
RANGE
```

Parser details are finalized during the relevant Slice.

---

# 26. Task secondary capabilities

Long-term supported capabilities include:

- primary category;
- multiple tags;
- checklist;
- plain-text notes;
- priority;
- PIN;
- custom recurrence;
- templates;
- attachments;
- simple dependency relationships;
- calendar dragging;
- multi-select batch operations;
- undo/redo;
- trash;
- history.

Explicitly do not implement geofencing/location reminders.

Do not pull these features into early Slices unless they are explicitly in scope.

---

# 27. Recurring tasks

Model:

```text
RecurringTaskSeries
TaskInstance
```

These are distinct.

Each occurrence has its own historical result.

Example:

```text
Aug27 COMPLETED
Aug28 MISSED
Aug29 COMPLETED
```

Future instances use a rolling generation window.

Initial target is roughly:

```text
60–90 days
```

When browsing farther dates, generate as needed.

Exact generation mechanics are implementation details for that Slice.

---

# 28. History

Task History:

- timeline;
- search;
- filters;
- lightweight statistics.

Retention is configurable.

Expected options include:

- 6 months;
- 1 year;
- custom;
- forever.

Reminder History is distinct from Task History.

Initial Reminder History retention target is around 90 days.

Anime History is also separate.

---

# 29. ANIME product scope

Anime module handles:

- tracking;
- airing countdown;
- update reminders;
- WATCH LATER;
- local watch progress;
- watch history;
- plan-to-watch;
- Anime -> Task association.

Explicitly do not implement:

- streaming;
- video sources;
- downloading;
- danmaku;
- piracy/resource aggregation.

Kazumi may be studied only for network/API access patterns relevant to mainland China, not copied as a player design.

---

# 30. Anime data sources

Primary data strategy:

- Bangumi;
- bangumi-data;
- custom providers;
- local cache.

Bangumi token:

- user pastes their own Personal Access Token;
- verify against `/v0/me`;
- store in Windows Credential Manager and/or DPAPI abstraction;
- never log it;
- never include it in backups;
- never commit it;
- never send it to untrusted public mirrors.

---

# 31. Bangumi network policy

Use:

```text
Official First + tiered failover
```

Public metadata:

```text
Official
  ->
trusted/configured Provider
  ->
Cache
```

Authenticated user sync:

```text
Official API
  ->
explicitly user-authorized custom proxy with authenticated capability
  ->
Offline / Retry
```

A public mirror must never automatically receive a Bangumi token.

---

# 32. Provider model

Provider profile conceptually contains:

- name;
- endpoint;
- capabilities;
- trust policy;
- priority;
- health state.

Capabilities may include:

- public metadata;
- calendar/airing;
- authenticated sync;
- images.

Token access must be separately and explicitly authorized.

Public-data providers may fail over automatically.

Authenticated failover requires both capability and explicit token authorization.

---

# 33. Anime metadata provenance

Use field-level source provenance.

Each field should be able to know conceptually:

- source;
- revision;
- updated time.

Effective value priority:

```text
Local Override > trusted source value
```

Users may override:

- title;
- cover;
- total episode count;
- airing time;
- other relevant metadata.

Sync must not silently overwrite Local Overrides.

---

# 34. Anime IDs

ReminNote owns stable local IDs.

Use local stable IDs such as:

```text
AnimeEntryId
LocalEpisodeId
```

External Bangumi IDs are mappings, not primary identity.

Watch history binds to `LocalEpisodeId`.

External episode IDs are mapping data only.

Core local entities should prefer UUID v7.

Do not use SQLite auto-increment IDs as cross-device identity.

---

# 35. Episode model

Episode types may include:

```text
MAIN
SP
OVA
SPECIAL
OTHER
```

Episode mapping may consider:

- number;
- type;
- title;
- date;
- provider ID.

If uncertain, ask the user to confirm rather than silently creating a wrong mapping.

---

# 36. Anime tracking semantics

Bangumi -> ReminNote watch-state integration is initially one-way.

Do not write ReminNote watch status back to Bangumi unless a future plan explicitly changes this.

Bangumi “watching” and ReminNote “tracking subscription” are distinct concepts.

When current-season shows are first discovered, ask whether the user wants Tracking enabled.

---

# 37. Anime airing time

Priority:

1. Local Custom Override
2. bangumi-data `broadcast`
3. bangumi-data `begin`
4. Bangumi date/weekday fallback

Widget presentation:

- local time prominent;
- original JST time secondary;
- if system timezone is JST, avoid redundant duplicate time display.

---

# 38. WATCH LATER

After an episode airs, it enters local WATCH LATER.

Default smart ordering should prioritize concepts such as:

- another episode is coming soon while the previous one remains unwatched;
- NEXT TO WATCH / higher relevance;
- backlog age;
- update time.

Alternative sort modes may exist.

---

# 39. Anime Widget

Hierarchy:

```text
PRIMARY -> UPCOMING -> SUMMARY
```

Nearest show is the main visual.

Future shows form an upcoming queue.

Small widget may show:

```text
+N TONIGHT
```

Possible actions:

- WATCHED;
- WATCH LATER;
- SCHEDULE AS TASK;
- REMIND ME LATER;
- OPEN DETAILS.

---

# 40. Anime schedule anomalies

Support:

- temporary time change;
- hiatus;
- delay;
- consecutive airing.

If no Local Override:

- automatically update from trusted source;
- notify the user.

If Local Override exists:

- do not overwrite it automatically;
- ask user what to do.

Use a concept such as:

```text
SCHEDULE NOTICE
```

Do not incorrectly place schedule anomalies into WATCH LATER.

Keep schedule history.

---

# 41. Anime daily digest

Support:

- today's update summary;
- optional tomorrow preview.

Default:

```text
today digest = on
tomorrow preview = off
```

May appear on first startup/wake or at a configured time.

Deduplicate within the same logical day.

Digest may intelligently note situations such as:

> another episode airs tonight, but the previous one is still unwatched.

---

# 42. Anime Library / Dashboard

Primary sections:

- NEXT AIRING;
- WATCH LATER;
- WATCHING;
- PLAN TO WATCH;
- THIS SEASON.

THIS SEASON is a real seasonal page.

Support concepts such as:

- MY SEASON;
- ALL / DISCOVER;
- weekday filter;
- status filter;
- season switcher.

---

# 43. Anime -> Task

User may schedule episodes as tasks.

Examples:

```text
EP1 -> tomorrow
EP2 + EP3 -> day after tomorrow
```

Support:

- independent tasks;
- merged task.

Completing a related Task may ask whether to mark Episode Watched.

Marking an Episode Watched in ANIME may ask whether to complete related future tasks.

Do not silently change both domains without user-visible intent.

---

# 44. Widget system

Windows desktop Widget supports separate:

```text
TODAY
ANIME
```

pages.

Do not overcrowd both into one page by default.

Long-term widget capabilities:

- free resize;
- responsive layout;
- multi-monitor;
- multiple widget instances;
- layout presets;
- lock/click-through;
- temporary interactive state;
- smart topmost behavior;
- snapping;
- alignment;
- privacy mode.

Conceptual states:

```text
LOCKED
TEMP_INTERACTIVE
UNLOCKED
ALERT
```

---

# 45. Multi-monitor behavior

Use Per-Monitor DPI awareness.

Persist conceptually:

- monitor identity;
- relative position;
- DIP size;
- safe fallback.

If a monitor disappears, move the widget to a visible safe location.

If the monitor returns, layout restoration may be offered/performed.

---

# 46. Tray

Left click:

- open lightweight Tray Panel.

Double click:

- open Main App.

Right click:

- system menu.

Tray Panel should normally dismiss on focus loss.

Temporary pinning may be supported.

---

# 47. Reminder Drawer

Widget action:

```text
REMINDERS · N
```

opens a lightweight Reminder Drawer for recent unresolved reminders.

Full history/diagnostics belongs in Main App Reminder Center.

---

# 48. Cross-page Widget alerts

Use smart temporary takeover.

Normal reminders should not randomly flip TODAY/ANIME pages.

Normal:

- badge;
- overlay;
- breathing/attention state.

HIGH:

- stronger treatment.

PIN + HIGH:

- may temporarily take primary visual focus.

User preference may support:

```text
NEVER SWITCH
SMART
ALWAYS SWITCH
```

---

# 49. UI direction

Visual direction is frozen:

> bright, clean Japanese game / JRPG / galgame information-panel aesthetic.

Do not turn it into cyberpunk.

Core visual language:

- white;
- light gray;
- pale sky blue;
- thin lines;
- large numbers;
- small English labels;
- restrained decoration.

Main App and Widget must share one coherent design system.

Use native WPF plus a custom:

```text
ReminNote Design System
```

Do not base the app on a large Material/HandyControl-style UI framework unless a later explicit decision changes this.

---

# 50. Theme

Support:

- built-in backgrounds;
- custom user backgrounds;
- blur;
- brightness;
- overlay;
- readability controls.

Global accent:

- manual selection;
- optional wallpaper-derived color.

Anime may extract a restrained accent from cover art.

Fonts should be configurable independently for:

- Chinese;
- English/UI;
- countdown display.

---

# 51. Accessibility

Support architecture for:

- larger fonts;
- high contrast;
- reduced transparency;
- reduced/disabled animation;
- keyboard usage;
- Windows accessibility setting response.

Accessibility must not be added as an afterthought that requires rebuilding the UI system.

---

# 52. Technology stack

Windows stack:

```text
C#
.NET 10 LTS
WPF
CommunityToolkit.Mvvm
EF Core
SQLite
Noda Time
Microsoft.Extensions.Hosting
Microsoft.Extensions.Logging
Serilog
SQLite FTS5
Win32 / DWM interop where necessary
```

As of 2026-08-27, .NET 10 is the active LTS line.

Pin the SDK with:

```text
global.json
```

Use stable packages only.

Do not use Preview/RC packages in stable development unless explicitly approved.

Centralize package versions using:

```text
Directory.Packages.props
```

Commit:

```text
packages.lock.json
```

CI should restore in locked mode.

---

# 53. Repository structure

Monorepo.

Windows projects:

```text
src/windows/
  ReminNote.Core
  ReminNote.Infrastructure
  ReminNote.Windows
  ReminNote.Agent
  ReminNote.Bootstrap
```

Future roots:

```text
src/android/
src/sync-server/
protocol/
```

Do not create future projects prematurely unless needed.

Do not add a new `ReminNote.Application` project merely because layered architecture tutorials commonly do so. If a separate Application project later becomes clearly justified, treat that as an architecture decision and record it.

---

# 54. Architecture responsibilities

## 54.1 Bootstrap

Responsibilities only:

- start/activate processes;
- crash recovery;
- safe mode;
- updater/version switching later.

Bootstrap must not contain business logic.

Bootstrap must not directly manipulate the business database.

## 54.2 Agent

Long-running process.

Responsibilities:

- single business writer;
- reminders;
- Widget;
- Tray;
- background sync later;
- provider/background network work;
- fullscreen/app rules;
- notification handling.

## 54.3 Main App

Full management UI.

Open only when needed.

## 54.4 Core

Must remain independent from:

- WPF;
- EF Core;
- Serilog;
- Windows API.

Core contains domain concepts and architecture contracts that truly belong there.

Do not leak infrastructure or UI concerns into Core.

## 54.5 Infrastructure

Implements persistence/network/provider infrastructure.

---

# 55. Single Writer

All business writes are owned by Agent once P2.5 migration is complete.

Main App writes through:

```text
Command -> Named Pipe -> Agent
```

Widget is hosted with Agent and may invoke the application service path internally.

Windows notification actions are handled by Agent.

Main App may use a read-only query path to SQLite.

Use:

```text
AsNoTracking
```

where appropriate.

SQLite uses:

```text
WAL
```

Overall pattern:

```text
Single Writer + Multiple Safe Readers
```

During P2.5 migration, implementation may be gradual, but **no runnable milestone may expose two legitimate writer paths simultaneously**.

---

# 56. IPC

Use Windows Named Pipes behind abstractions such as:

```text
IReminNoteIpcClient
IReminNoteIpcServer
```

Protocol concepts:

- Request;
- Response;
- Event.

First version may use JSON.

Protocol must include concepts such as:

- `ProtocolVersion`;
- `RequestId`;
- timeout;
- reconnect;
- current-Windows-user isolation.

Do not overengineer binary serialization in the first version.

IPC protocol changes are high-risk changes.

---

# 57. Change Journal

Every successful business transaction creates a global revision and a durable change-journal entry in the same transaction.

Main App tracks:

```text
LastSeenRevision
```

After IPC interruption:

```text
GET CHANGES SINCE X
```

can repair incremental UI state.

Change Journal is a short-term consistency mechanism.

It is **not Event Sourcing**.

Do not redesign the whole system around event sourcing.

---

# 58. Database

Use:

```text
SQLite + EF Core
```

EF Core is primary.

Raw SQL is allowed for:

- hot paths;
- FTS;
- queries that are clearly better expressed directly.

Do not introduce a second ORM without strong justification.

---

# 59. Migration policy

From P1 onward:

- every Schema change must be managed through a real Migration workflow;
- do not normalize “edit entity, delete DB, rerun” as the development process.

Before P3:

- development databases may still be reset when necessary;
- backward compatibility with every dev snapshot is not promised;
- migration history may be squashed/reset deliberately before the compatibility boundary if justified and documented.

From the first P3 daily-use Alpha:

- normal upgrades must migrate forward;
- “delete your database” is not an acceptable routine upgrade path.

Migration is a high-risk operation.

---

# 60. Minimum data-safety gate before P3

Before the first daily-use P3 Alpha, implement a minimum safety baseline:

```text
Backup -> Migration -> Verify -> Startup
```

Minimum required behavior:

- create safety backup before schema migration;
- preserve original DB;
- perform basic post-migration verification;
- if migration fails, stop normal writes/startup;
- expose a clear recovery path;
- do not attempt destructive in-place repair.

This is **P2.75**.

Do not pull the full long-term recovery suite into P2.75.

---

# 61. Full database recovery — later phase

Long-term Recovery Mode principles:

- stop writes first;
- preserve corrupted DB;
- never repair the only original file in place;
- prefer restoring latest healthy backup;
- repair into a new DB;
- verify;
- atomically switch only after success.

DB health checks should be tiered:

- startup quick check;
- elevated checks after abnormal exit;
- checks after migration;
- low-frequency full `integrity_check`.

Do not full-scan the database on every normal startup.

---

# 62. Backup policy

Long-term local automatic retention target:

- 7 daily;
- 4 weekly;
- 6 monthly.

Configurable.

Manual backups should not be silently auto-deleted by the normal retention job.

Migration always gets a safety backup.

Secrets are excluded by default.

---

# 63. Data directory

User chooses data root.

Do not force large data onto C:.

Example:

```text
D:\ReminNote\
  Data
  Cache
  Backup
  Logs
  UserAssets
```

Portable mode:

```text
.\UserData
```

Development data must remain isolated from real user data.

---

# 64. Configuration layers

## Bootstrap JSON

Only minimal startup-level configuration such as:

- DataRoot;
- current version;
- minimal bootstrap flags.

## SQLite

Normal application settings.

## Credential Manager / DPAPI

Secrets.

## Runtime state

Machine/window/crash-specific state.

Do not collapse all configuration into one giant JSON file.

---

# 65. Data encryption

Default:

```text
normal SQLite
```

Advanced users may later enable at-rest encryption.

Installed mode:

- DPAPI/Credential Manager integration.

Portable mode:

- independent user password.

Keep an abstraction equivalent to:

```text
DatabaseEncryptionProvider
```

Do not implement custom cryptography.

Research and reuse audited components when this phase begins.

---

# 66. Sync — future, not P3

There is no ReminNote official account/cloud service.

Future advanced settings:

```text
SETTINGS -> ADVANCED -> SYNC
```

Potential modes:

- LAN Sync;
- Self-hosted Sync.

LAN may use QR + pairing code.

Self-hosted sync must use client-side E2EE.

Server sees ciphertext.

P3 is intentionally single-device/local-first.

However, data identity and revision design must remain future-sync-compatible.

Do not implement network sync in P3.

---

# 67. Sync conflicts — future

Use field-level merge.

Different fields:

- auto merge.

True same-field conflict:

- ask user;
- preserve a conflict copy;
- never silently lose data.

---

# 68. Android — future

Future first companion scope:

- TODAY;
- DONE;
- Reminder;
- Anime countdown;
- WATCH LATER;
- WATCHED;
- Quick Add.

Prefer native Android UI.

Do not force UI-code sharing with WPF.

---

# 69. Search

Command Panel uses:

```text
SQLite FTS5 + SearchIndexer
```

Support future matching for:

- Chinese;
- pinyin;
- pinyin initials;
- Anime Chinese/Japanese/English titles;
- aliases;
- Task;
- History;
- Tag;
- Category.

FTS is derived data.

Business data remains truth.

Business transactions should enqueue/update a concept equivalent to:

```text
SearchIndexWorkItem
```

Background indexing may update FTS.

FTS must be rebuildable.

---

# 70. App lifecycle

Single-instance targets:

```text
1 Bootstrap
1 Agent
1 Main App
N WidgetInstance
```

Launching Main App again should activate the existing instance via IPC rather than open another full process.

---

# 71. Agent crash handling

Bootstrap acts as watchdog.

On crash:

- limited automatic restart.

On repeated crash loop:

```text
SAFE MODE
```

Safe Mode may temporarily disable:

- third-party providers;
- custom widget effects;
- background sync.

Safe Mode must not delete configuration or user data.

---

# 72. Update system — future

Full updater is **not required for P3 Alpha**.

P3 must support:

- clear version identity;
- reliable manual upgrade;
- migration safety.

Later updater architecture:

- versioned directories;
- download;
- hash/signature verification;
- safety backup;
- stop processes;
- start new Agent;
- migration;
- health check;
- atomic version switch;
- rollback on failure.

Schema evolution should follow a strategy equivalent to:

```text
Expand -> Migrate -> Contract
```

Do not rush self-update into P3.

---

# 73. Windows support and packaging

Supported OS:

- Windows 10 22H2 x64;
- Windows 11 x64.

Do not support ARM64 in the initial product.

Distribution:

- Inno Setup installer;
- ZIP Portable.

Release:

- self-contained x64;
- user should not need to install .NET Runtime separately.

Installed version may start for current Windows user at login.

Allow startup delay choices such as:

```text
0 / 10 / 30 / 60 seconds
```

Portable mode must not silently enable startup.

Agent is not a Windows Service.

---

# 74. P3 data export / controlled restore

By the P3 daily-use Alpha, provide reliable structured export.

Goals:

- user data has an escape hatch independent of a single SQLite file;
- support controlled migration/restoration;
- secrets excluded;
- format is versioned.

Export may include relevant:

- Tasks;
- Results;
- Reminder History;
- exportable settings.

Do **not** implement complex intelligent merge of two independent ReminNote datasets in P3.

That belongs to later Import/Sync work.

---

# 75. Plugin policy

Do not create a general plugin platform in P3.

Do not build:

- plugin store;
- arbitrary dynamic DLL ecosystem;
- stable third-party Plugin SDK;
- third-party UI injection framework.

Keep only naturally useful internal abstractions where there is a clear foreseeable boundary, for example:

- provider interface;
- notification channel interface.

Do not over-generalize every service “for plugins”.

---

# 76. Internationalization

First release quality target:

```text
Simplified Chinese
```

Architecture must support i18n from day one.

Use stable resource keys.

Do not scatter user-facing text literals throughout ViewModels/business logic.

Future languages:

- English;
- Japanese;
- Traditional Chinese.

---

# 77. Code language

C# identifiers:

```text
English
```

Branch/commit names:

```text
English
```

Product/development documentation:

```text
Simplified Chinese preferred
```

README may later gain an English version.

---

# 78. Logging

Business code uses:

```text
Microsoft.Extensions.Logging
```

Serilog is the backend.

Use structured rolling logs.

Use a centralized sanitizer.

Never log:

- task title;
- notes;
- attachment content;
- Bangumi token;
- Authorization header;
- sync key;
- recovery key.

Do not add debug logging that silently violates this policy.

---

# 79. Diagnostics

Support later:

- local logs;
- one-click diagnostic package;
- optional anonymous crash report.

Remote upload default:

```text
OFF
```

Diagnostic package may include:

- version;
- Windows version;
- Schema version;
- widget state;
- sync state;
- error stack.

Do not include user content.

---

# 80. Release symbols

Release build should generate:

- portable PDB;
- Source Link.

Normal installer should not ship full debug symbols unnecessarily.

CI/Release storage should retain:

- symbols;
- commit SHA;
- version.

---

# 81. Assets and licensing

Separate code licensing from assets.

Third-party:

- icons;
- fonts;
- sounds;
- themes

must have recorded source and license.

Anime cover art is runtime cache.

Do not commit it to GitHub.

Do not bundle cached anime covers into installer.

Use Git LFS selectively for large repository-owned assets such as:

- large backgrounds;
- audio;
- design source files.

Small assets stay in normal Git.

---

# 82. Open-source reuse policy

Do not reinvent solved problems without reason.

Before medium/large/high-risk modules, perform a short Open Source Research pass covering:

- GitHub projects;
- NuGet packages;
- official Microsoft/platform solution;
- license;
- maintenance state;
- recent security issues;
- architectural fit;
- long-term dependency risk.

Allowed outcomes:

- direct dependency;
- fork;
- module reuse;
- adapted source;
- reference implementation;
- custom implementation.

If an existing project solves most of the need, a fork or targeted reuse is allowed if licensing and maintenance cost are acceptable.

For any copied/adapted source, record:

- source;
- repository;
- commit/tag;
- license;
- local modifications;
- upstream relationship.

Security-sensitive modules deserve extra scrutiny:

- networking;
- updater;
- encryption;
- notifications.

ReminNote client target license:

```text
GPL-3.0-or-later
```

Future self-hosted sync server:

```text
AGPL-3.0-or-later
```

“Source available” does not mean “safe to copy”.

If source license is unclear, do not copy it.

---

# 83. Dependency policy

Controlled dependencies.

Do not practice NIH.
Do not add packages casually.

Use:

```text
Directory.Packages.props
packages.lock.json
global.json
```

CI restore:

```text
locked mode
```

No preview/RC dependencies in normal stable development.

Every new material dependency should have a brief reason.

---

# 84. Code quality

Required:

- nullable enabled;
- `.editorconfig`;
- Microsoft analyzers;
- strict Core project;
- narrow exceptions only for WPF/interop;
- no broad `NoWarn`;
- new code should not casually add warnings.

Architecture boundaries should be enforced with:

- project references;
- a small number of architecture tests where valuable.

Do not create a massive architecture-test framework before it provides value.

---

# 85. Application service style

Do not force MediatR.

Prefer explicit application services such as:

```text
ITaskApplicationService
IAnimeApplicationService
IReminderApplicationService
```

Split complex operations into handlers only when useful.

Avoid “handler ocean” architecture.

---

# 86. Generic Host

Agent and Main App should use:

```text
Microsoft.Extensions.Hosting
```

for:

- DI;
- logging;
- configuration;
- lifetime;
- hosted services.

Bootstrap remains intentionally small.

---

# 87. GitHub / repository governance

Initial repository may remain Private.

Before making Public, complete at least:

- P0;
- secret hygiene review;
- license review;
- repository-history hygiene review.

Eventually keep ReminNote strictly open source.

Repository should include:

- `CONTRIBUTING.md`
- `CODE_OF_CONDUCT.md`
- `SECURITY.md`
- Issue templates
- PR templates

Use DCO.

Contributor commits use:

```text
Signed-off-by
```

No heavy CLA unless a future explicit decision changes this.

---

# 88. Git workflow

Each Vertical Slice uses a short-lived branch and PR.

Example:

```text
slice/p1-task-sqlite
```

Flow:

```text
Codex
-> local validation
-> user/manual validation
-> Push
-> CI
-> AI Review
-> fixes
-> Merge main
```

`main` should remain runnable.

Codex defaults:

- may edit files;
- small changes may proceed directly;
- medium changes: explain scope briefly first;
- high-risk changes require explicit user approval.

High-risk includes:

- Migration strategy changes;
- Schema restructuring;
- IPC protocol changes;
- encryption;
- updater/rollback;
- Core architecture;
- large-scale deletion.

Codex must not automatically:

- commit;
- push;
- tag;
- release;
- rebase;
- force push.

---

# 89. Commit policy

When the user later asks for commits, split by meaningful logic.

Examples:

- domain;
- storage;
- UI;
- fixes.

Before merge, clean meaningless history such as:

- WIP;
- try fix;
- fix again.

Do not perform rebase automatically.

---

# 90. AI review severity

Use:

```text
BLOCKER
MAJOR
MINOR
STYLE/NIT
```

BLOCKER:

- must fix.

MAJOR:

- must fix.

MINOR:

- fix now if trivial and in-scope, otherwise create Issue.

STYLE/NIT:

- not blocking.

AI review must not override frozen product/architecture decisions without demonstrating a real conflict.

---

# 91. CI

Windows CI minimum:

- restore;
- Release build;
- required automated tests;
- basic analyzers;
- locked dependency restore verification.

Build must be green.

Later enable/maintain:

- Dependabot;
- Secret Scanning;
- Push Protection;
- CodeQL;
- `SECURITY.md`.

Do not auto-merge dependency/security updates blindly.

---

# 92. Releases

Ordinary merge does not publish a release.

Only explicit SemVer tags trigger GitHub Release.

Example:

```text
v0.3.0-alpha.1
```

Release artifacts later include:

- `Setup-x64.exe`;
- `Portable-x64.zip`;
- hashes;
- update manifest when updater exists;
- release notes.

Version concepts remain separate:

- AppVersion;
- SchemaVersion;
- IpcProtocolVersion;
- SyncProtocolVersion.

Do not conflate them.

---

# 93. Changelog

Every user-visible PR should create a change fragment.

Release process aggregates fragments and archives them into:

```text
CHANGELOG.md
```

Do not force internal refactors to produce fake user-facing changelog entries.

---

# 94. Development data safety

Debug/Codex runs must default to:

```text
.devdata/
```

This must remain separate from production data.

Development must not read:

- production DB;
- real token;
- production Widget config.

High-risk testing damages `.devdata` first.

Testing with real user data requires explicit opt-in plus automatic backup.

---

# 95. Dev secrets

Real tokens only live in local secure storage.

Never put them in:

- Git;
- source;
- example config;
- logs;
- CI.

Bangumi CI tests use mocks.

---

# 96. Development scripts

Repository should provide lightweight PowerShell scripts:

```text
scripts/
  bootstrap.ps1
  build.ps1
  run.ps1
  test.ps1
  clean.ps1
```

`clean.ps1` must never delete real production data by default.

---

# 97. Required living documents

Maintain:

```text
PRODUCT_RULES.md
ARCHITECTURE.md
DEVELOPMENT.md
TESTING.md
DECISIONS.md
```

When a Slice changes a product rule or architecture rule, update the matching document in the same Slice.

Each Slice begins with a short:

```text
Implementation Brief
```

containing:

- Goal;
- In Scope;
- Out of Scope;
- Product Rules;
- Reuse Research;
- Acceptance;
- Manual Test;
- Risks.

---

# 98. Scope freeze

P0–P3 main scope is frozen.

During development, new ideas are classified:

## Class 1 — immediate

Allowed to interrupt the active Slice:

- BLOCKER;
- architecture error;
- security issue;
- data-loss/data-corruption risk.

## Class 2 — current-Slice required gap

If a missing decision prevents correct implementation of the current Slice, decide it locally and document it.

Do not reopen a broad requirements interview.

## Class 3 — useful but not required now

Put into:

- Backlog;
- GitHub Issue.

Do not expand current Slice.

There is no “Question 314” product interview phase.

Fine implementation details are intentionally resolved during the relevant Slice.

---

# 99. Development phases

The implementation order is authoritative.

Do not jump ahead without a concrete blocker.

---

# P0 — Repository foundation + high-fidelity UI skeleton

## Goal

Create a real, maintainable Windows project foundation and validate the product's visual/interaction language without coupling it to persistence/background architecture too early.

## Architecture choice

P0 uses:

> real UI/project structure + Mock data/services.

P0 is not merely screenshots.

P0 must not yet wire real SQLite, Agent IPC, or Reminder Scheduler.

## In scope

- repository/sln foundation;
- Windows projects;
- build scripts;
- central package management;
- global SDK pin;
- nullable/analyzers/editorconfig;
- Generic Host where appropriate;
- MVVM foundation;
- navigation shell;
- ReminNote Design System;
- high-fidelity TODAY Mock;
- high-fidelity ANIME Mock;
- high-fidelity Widget;
- responsive widget states;
- mock Quick Add interaction;
- mock task status interactions;
- resource/i18n foundation;
- `.devdata` safety;
- living documentation;
- baseline CI.

## Out of scope

- SQLite business persistence;
- real Reminder Scheduler;
- Agent IPC;
- Bangumi network access;
- updater;
- sync;
- Android;
- general plugin platform.

## P0 acceptance

At minimum:

- clean clone can bootstrap/build;
- app launches;
- navigation works;
- TODAY Mock communicates the intended hierarchy;
- ANIME Mock communicates the intended hierarchy;
- Widget is interactive enough to validate behavior;
- no production data is touched;
- Core has no WPF/EF/Windows dependencies;
- design resources are reusable rather than duplicated page-by-page;
- no critical warnings introduced;
- CI builds successfully.

## P0 completion rule

Do not declare P0 complete merely because the windows render.

The user must manually validate the visual hierarchy and primary interactions.

---

# P1 — Real Task domain + SQLite persistence

## Goal

Replace mock Task data with a durable, long-term-capable Task core.

## In scope

- Task IDs using UUID v7;
- `TaskTimeType`;
- `TimeSpec`;
- ANYTIME/TIME/RANGE;
- local date/time semantics;
- cross-midnight range rules;
- core result model required by current Slice;
- SQLite + EF Core;
- real migrations;
- schema constraints;
- basic repository/query/application service;
- real create/read/update/delete path needed by P1;
- Noda Time abstraction;
- deterministic domain validation;
- unit tests for time-shape invariants.

## Schema principle

Design the Task core schema as a long-term model now.

Do not create a disposable flat prototype schema.

Also do not pre-create every future Reminder/Anime/Sync table.

## Compatibility policy

Use real migrations from day one.

P1 development data may still be reset if necessary.

## Required automated tests

High-value logic includes:

- legal/illegal time-spec combinations;
- cross-midnight detection;
- local-date ownership;
- domain equality/value-object behavior where relevant.

---

# P2 — Real TODAY / Widget task loop

## Goal

Make Task actually useful before the Agent migration.

## In scope

- Widget reads real Task data;
- DONE;
- result recording;
- RANGE `AWAITING RESULT`;
- NEEDS REVIEW;
- Quick Add;
- deterministic TaskParser;
- workday boundary;
- cross-midnight behavior;
- reschedule rules;
- continuation relation;
- basic Today grouping;
- required task history behavior.

## Acceptance

The user can realistically manage daily tasks locally.

No real Reminder system yet.

## Deferred UI polish — lightweight frosted detail dialog

The Main App Anime detail dialog should later be refined into a low-distraction,
lightweight overlay that visually floats above the current page instead of
looking like a separate default desktop window.

Desired direction:

- translucent/frosted surface with restrained shadow and the existing ReminNote
  accent system;
- visually quiet chrome, clear hierarchy, and no duplicated border/corner lines;
- preserve the current modal ownership, keyboard focus, close/cancel behavior,
  DPI scaling, resizing limits, and content scrolling;
- respect Windows reduced-transparency/high-contrast settings with an opaque,
  accessible fallback;
- keep the change presentation-only: no new persistence, networking, Anime
  provider, or domain semantics.

This is a deferred P2/P3 visual-polish item, not a P1/P0 acceptance blocker.
Manual acceptance must confirm that the dialog remains readable, does not
silently lose focus, does not cover or trap the parent window unexpectedly, and
can be opened/closed repeatedly at compact, standard, and high-DPI sizes.

---

# P2.5 — Bootstrap + Agent + IPC + Single Writer migration

## Goal

Move to the long-term process architecture before implementing production Reminder scheduling.

## In scope

- Bootstrap process;
- Agent process;
- Main App activation/lifetime;
- Named Pipe protocol;
- Single Writer;
- read-only query layer;
- SQLite WAL;
- Change Journal;
- revision recovery;
- idempotent command model;
- existing P2 Task operations routed through Agent.

## Migration rule

Implementation may be incremental.

However:

> any runnable checkpoint has only one legitimate write path.

Once Agent owns writes, Main App direct business writes are removed.

## Out of scope

- new major user-facing product features;
- full Reminder;
- Anime networking.

P2.5 is an architecture migration Slice, not a feature expansion Slice.

---

# P2.75 — Minimum migration/data safety

## Goal

Make the upcoming P3 daily-use Alpha safe enough to trust with real personal data.

## In scope

- migration safety backup;
- migration verification;
- failure stops normal writes;
- preserve original DB;
- minimal recovery entry path;
- tested upgrade path.

## Out of scope

- polished full Recovery Center;
- full backup retention manager;
- advanced DB repair UI;
- complete encryption system.

---

# P3 — Production Reminder loop / first daily-use Alpha

## Goal

Create a reliable local reminder product.

This is the first milestone intended for real daily use and limited friend testing.

## In scope

- `ReminderRule`;
- `ReminderSchedule`;
- `ReminderInstance`;
- schedule revision/supersede behavior;
- purpose/kind semantics;
- Scheduler;
- reminder persistence;
- Windows Toast channel;
- Tray alert;
- Widget alert;
- Reminder Drawer/Center foundation;
- `UNREAD -> READ -> RESOLVED`;
- Snooze;
- multi-reminder support needed by product;
- priority/PIN behavior;
- Quiet Hours;
- sleep/shutdown recovery;
- Notification Health;
- task completion cancels future reminder schedules for that instance;
- history;
- manual-upgrade compatibility;
- P3 structured export + controlled restore/migration.

## Reminder invariants

- Rule is business truth.
- Schedule is rebuildable derived execution state.
- Instance is a historical fact.
- historical instances are not rewritten.
- Snooze does not mutate the original Rule.
- task completion invalidates only future reminders for that TaskInstance.
- Toast is not the reminder system itself.
- Agent remains the writer.

## P3 compatibility boundary

From the first P3 daily-use Alpha onward:

- do not rely on deleting the DB for routine upgrades;
- every normal release must migrate forward safely;
- protect user data as persistent assets.

## P3 intentionally excludes

- automatic self-update;
- LAN Sync;
- self-hosted Sync;
- general plugin system;
- Anime networking;
- complex dataset merge import.

---

# P3 Stability Gate

Do not immediately begin P4 merely because P3 code compiles.

The user should actually use Task + Reminder.

Gate requirements include:

- no known BLOCKER/MAJOR reminder correctness issue;
- Agent stays stable through normal use;
- restart recovery works;
- sleep/wake recovery works;
- migration safety is manually verified;
- no structural data-model failure is discovered;
- notification-channel failure does not erase reminder truth.

No fixed “7-day” or “30-day” waiting period.

Pass based on stability evidence, not calendar duration.

---

# P4 — Local Anime domain

## Goal

Build Anime locally before networking complexity.

## In scope

- AnimeEntry;
- LocalEpisode;
- stable IDs;
- episode types;
- airing/countdown model;
- WATCH LATER;
- local tracking;
- watch history;
- plan-to-watch;
- Anime Widget;
- Anime -> Task relationship.

## Out of scope

- broad provider failover;
- authenticated Bangumi sync;
- sync server;
- streaming/downloading.

---

# P5 — Bangumi + provider layer

## Goal

Connect the stable Anime domain to real sources without compromising security or local ownership.

## In scope

- Bangumi PAT;
- `/v0/me` verification;
- official API;
- bangumi-data;
- Provider Profile;
- provider health/failover;
- field provenance;
- Local Override behavior;
- secure credentials;
- cache;
- authenticated capability authorization.

Perform Open Source Research before implementation, especially for networking/cache/auth/token handling.

---

# 100. Post-P5 roadmap

After the above foundations are stable, possible future work includes:

- Calendar;
- full Backup/Recovery;
- automatic updater;
- database encryption;
- LAN Sync;
- self-hosted E2EE Sync;
- Android companion;
- deeper search;
- advanced import;
- optional extension/plugin ecosystem if actual demand justifies it.

Order should be decided from real usage evidence, not speculative completeness.

---

# 101. Testing strategy

The user is the primary end-to-end/manual tester.

Automated tests are mandatory where logic is brittle and deterministic.

Strong candidates:

- time logic;
- workday boundary;
- RANGE cross-midnight;
- recurrence;
- reminder calculation;
- reminder supersede/revision;
- Snooze chain behavior;
- migration;
- Change Journal;
- merge logic later;
- parser behavior;
- provider metadata resolution later.

Do not spend large amounts of time snapshot-testing every pixel of WPF UI.

## Every Slice handoff must include

1. What changed.
2. Files/components changed.
3. Build/test commands run.
4. Any warning or known limitation.
5. Exact manual test steps.
6. Expected result for each step.
7. Failure indicators.
8. Any Backlog/Issue discovered but intentionally not fixed.

---

# 102. Manual test format Codex must use

Example template:

```text
Manual Test

Environment
- Windows version:
- Build configuration:
- Data path: .devdata/...

Test 1 — Create ANYTIME task
1. Run ...
2. Open TODAY.
3. Click ...
4. Enter ...
Expected:
- ...
Failure:
- ...

Test 2 — RANGE crossing midnight
...
```

The user should never need to infer how to test the change.

---

# 103. Definition of Done for a Slice

A Slice is not done until:

- in-scope behavior is implemented;
- out-of-scope work was not silently pulled in;
- build passes;
- appropriate tests pass;
- no new unexplained warnings;
- architecture boundaries remain valid;
- security/data-safety rules remain valid;
- living docs updated if behavior/architecture changed;
- manual test instructions provided;
- user validates the important user-facing behavior;
- major discovered issues are resolved or explicitly recorded.

---

# 104. First execution instruction for Codex

When this document is first given to Codex, do **not** implement all phases.

Begin only with:

```text
P0-01 Repository Foundation
```

## P0-01 objectives

1. Inspect the existing repository.
2. If the repository is empty/new, create the minimum monorepo/Solution foundation.
3. Establish the Windows project structure:
   - `ReminNote.Core`
   - `ReminNote.Infrastructure`
   - `ReminNote.Windows`
   - `ReminNote.Agent`
   - `ReminNote.Bootstrap`
4. Pin .NET 10 LTS SDK with `global.json`.
5. Configure:
   - nullable;
   - `.editorconfig`;
   - central package management;
   - package lock files;
   - analyzers;
   - deterministic/reproducible basics where reasonable.
6. Add the minimum baseline dependencies only.
7. Establish project-reference boundaries.
8. Add `.devdata/` isolation and `.gitignore` rules.
9. Add or initialize:
   - `PRODUCT_RULES.md`
   - `ARCHITECTURE.md`
   - `DEVELOPMENT.md`
   - `TESTING.md`
   - `DECISIONS.md`
10. Add:
   - `scripts/bootstrap.ps1`
   - `scripts/build.ps1`
   - `scripts/run.ps1`
   - `scripts/test.ps1`
   - `scripts/clean.ps1`
11. Ensure `clean.ps1` cannot delete production data by default.
12. Add baseline Windows CI if the repo already has GitHub workflow structure; otherwise include it in this Slice if appropriate.
13. Build the solution.
14. Do not implement SQLite, Reminder, Anime, IPC, or real Task persistence yet.
15. Do not commit unless explicitly asked.

## Before coding P0-01

Codex should briefly report:

```text
Repository state:
- existing projects/files:
- constraints discovered:
- conflicts with plan:
- files expected to change:

Reuse/dependency check:
- dependencies required now:
- why each dependency is needed:
- license:
- maintenance/security concern:
```

For ordinary foundational Microsoft packages, this can be concise.

## P0-01 acceptance

- clean repository bootstrap succeeds;
- Release build succeeds;
- project reference graph matches architecture;
- Core has no WPF/EF Core/Serilog/Windows API dependency;
- package management is centralized;
- SDK is pinned;
- `.devdata` cannot be confused with production data;
- scripts are safe;
- docs exist;
- CI/build entry point exists;
- no future product feature has been prematurely implemented.

After P0-01, stop and report results + manual verification instructions.

Do not continue automatically to P0-02 unless the user explicitly tells Codex to continue.

---

# 105. Suggested subsequent P0 slices

After P0-01 is approved, continue approximately as:

```text
P0-02 Host + MVVM + App Shell
P0-03 ReminNote Design System foundation
P0-04 TODAY high-fidelity Mock
P0-05 ANIME high-fidelity Mock
P0-06 Widget high-fidelity Mock
P0-07 P0 polish/accessibility/i18n/CI stabilization
```

Slice boundaries may be adjusted based on the real repo, but scope must remain P0.

Each Slice gets an Implementation Brief.

---

# 106. Final rule for Codex

Optimize for:

```text
correctness
data safety
clear architecture
small reversible slices
manual testability
open-source reuse with license discipline
```

Do not optimize for:

```text
maximum number of features per iteration
premature abstractions
framework fashion
speculative plugin systems
hidden background behavior
clever but opaque architecture
```

When uncertain about a minor implementation detail:

1. choose the simplest option consistent with this plan;
2. document it in the current Slice;
3. continue.

When uncertain about a major irreversible choice affecting product semantics, architecture, schema, security, or user data:

1. stop that high-risk part;
2. explain the conflict clearly;
3. ask the user before proceeding.

This plan is the handoff from requirements planning to implementation.
