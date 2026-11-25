using ActivityJournal;

namespace TeamLeave;

/// <summary>
/// The shared activity journal (1.14.0): one line for each calendar entry calendar-team-leave-sync added, updated,
/// adopted or removed, and each leave request it approved, written through background-tools-lib's ActivityJournal to
/// journal_file (by convention %EA_LOCAL%\journal.jsonl), for a "while you were away" view. Only what a live door sent:
/// never a dry run, `--writes journal` or `refuse`, or an approval preview. <b>Every line is "restricted"</b>: each
/// names a person's leave, so a screen shown to others hides it whole. Never the leave's type. Writing it never fails
/// the run: a problem is logged.
/// <para>No line has an undo: the next sync puts the calendar back as People First says, and an approval can't be
/// taken back from here.</para>
/// </summary>
internal static class Activity
{
    public const string Tool = "calendar-team-leave-sync";

    /// <summary>One line. Never throws.</summary>
    public static void Write(Config config, Action<string> log, string what, string by, string subject, string? rule, string? why,
        string? item, bool unconfirmed = false)
    {
        if (config.JournalFile is not { } path)
            return;
        try
        {
            new Journal(path).TryWrite(new JournalEntry
            {
                Tool = Tool,
                What = what,
                By = by,
                Subject = subject,
                Rule = rule,
                Why = why,
                Item = item,
                Sensitivity = Sensitivity.Restricted,
                Outcome = unconfirmed ? JournalEntry.Unconfirmed : null,
            }, log);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log($"journal: couldn't write to {path}: {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>The callback <see cref="Sync.Apply"/> reports each calendar write to: a line each.</summary>
    public static Sync.Done Calendar(Config config, Action<string> log) =>
        (what, by, subject, why, item, unconfirmed) => Write(config, log, what, by, subject, "sync", why, item, unconfirmed);

    /// <summary>A line for an approval that was sent (approved, or its answer lost), by the rules or by hand.</summary>
    public static void Approval(Config config, Action<string> log, LeaveRequest request, ApprovalResult result, bool byHand)
    {
        if (result.Outcome is not (ApprovalOutcome.Approved or ApprovalOutcome.Unknown))
            return;
        Write(config, log, "approve", byHand ? By.Hand : By.Auto,
            $"{request.AsAbsence(config).Title(config)} {Sync.Fmt(request.Start, request.End)}",
            byHand ? "by hand" : "[approvals]",
            result.Outcome == ApprovalOutcome.Approved
                ? byHand ? "approved by hand; People First shows it approved" : "every [approvals] rule held; People First shows it approved"
                : result.Message,
            request.Id, unconfirmed: result.Outcome == ApprovalOutcome.Unknown);
    }
}
