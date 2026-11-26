namespace Trykatch.Infrastructure.Jobs;

/// <summary>Exact pinned Quartz 4.6.0 base schema; optional execution history is not enabled.</summary>
internal static class QuartzSchemaContract
{
    internal static readonly string[] Tables =
    [
        "qrtz_job_details", "qrtz_triggers", "qrtz_simple_triggers", "qrtz_simprop_triggers",
        "qrtz_cron_triggers", "qrtz_blob_triggers", "qrtz_calendars", "qrtz_paused_trigger_grps",
        "qrtz_paused_job_grps", "qrtz_fired_triggers", "qrtz_scheduler_state", "qrtz_locks"
    ];
}
