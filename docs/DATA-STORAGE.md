# Local data and updates

ZikWall stores user data in `%LOCALAPPDATA%\ZikWall`, outside the installation
folder. Installed and portable versions use the same directory for the same
Windows account. The MSI does not own or remove these files during upgrades.

Activity saving defaults on for new installations and older settings without a
history-saving preference. An explicitly saved off preference remains off.
Rules, profiles, Windows Firewall sync choices, DNS settings, appearance and
monitor preferences continue to save in this directory automatically.

Saved connection events and adapter samples have no automatic expiry. Each
capture appends new records to `SavedHistory\Activity\YYYY-MM-DD.jsonl` and flushes
them to disk before trimming the live view. Each nonblank line is a JSON
`ActivityArchive` containing `Events` and `Traffic`. Existing `activity.json`
records are migrated before trimming, including records older than seven days.
The `activity.json` file becomes a bounded recent-view cache. Startup replays
newer archived batches if an exit interrupted the cache checkpoint. An incomplete
append is left intact and skipped on replay; subsequent batches remain readable.

The live monitor still uses up to seven days / 10,000 events and 15,000 adapter
samples. Log analysis queries the saved archive independently of that cache,
including after exit or an upgrade. **All saved history** includes older dates;
**Last 7 days** queries that full date range. **Older events** and **Newer events**
page through matching records, 1,000 at a time. First-app observations are based
on the earliest saved observation, so cache trimming cannot label an app new again.
Queries run off the UI thread and retain one page plus the first observation per
app. The visible newest page refreshes at most every 30 seconds while at the top.
Automatic archive scans pause while scrolling or reading below the top; scrolling
also cancels a pending automatic scan. **Refresh** reloads the newest page.
Incremental updates preserve existing rows and the reading position. Browsing
older pages does not jump when new traffic arrives.
**Open saved logs** still opens the raw archive in Explorer for external processing.

Per-app usage totals are saved by local calendar date under
`SavedHistory\Usage\YYYY-MM-DD.json`. The usage view keeps its 30-day window;
older totals remain on disk. Existing usage dates are archived before the ledger
applies that window. Collecting usage and website names still follows the user's
separate collection choices.

Opening a log event marks only that event read, updates the unread count and
removes it from Unread only. Read markers are saved immediately to
`log-read-state.json`; legacy `log-read-through.txt` watermarks migrate on load.
Mark all as read also includes currently filtered-out archived events.

Normal exit and Windows update/shutdown session requests flush pending local
history and usage. Diagnostic runs use isolated files or suppress persistence.
An unopened view cannot overwrite history, and a failed history load preserves
the original file for recovery.

**Options → Clear history** explicitly removes the recent activity cache and all
saved connection/adapter archives after confirmation. It preserves usage totals,
read markers, rules and settings. There is no scheduled archive cleanup. Disk
space grows with recorded activity; old archive files can also be deleted from
Explorer when no longer needed.

Validation: the model tests cover click identity, legacy read-state migration,
indefinite archiving, bursts beyond the live cap, usage retention, restart recovery
and explicit clearing. `--saved-logs-only` covers archive queries after restart,
stable first observations, search/date ranges and pagination at shared timestamps.
Native `--test-log-analysis` invokes the actual list row and loads older saved
history beyond the recent-cache limit, including older/newer navigation;
`--test-preferences` checks replacement views, unreadable-file protection and a
Windows session query that flushes pending history.
