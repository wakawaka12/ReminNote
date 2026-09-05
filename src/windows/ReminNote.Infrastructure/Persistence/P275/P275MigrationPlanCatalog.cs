namespace ReminNote.Infrastructure.Persistence.P275;

/// <summary>
/// One compiled migration allow-list shared by the normal Agent gate and the
/// structured user-data Candidate verifier. Keeping the list in
/// Infrastructure prevents a restore path from silently drifting away from
/// the startup migration contract.
/// </summary>
public static class P275MigrationPlanCatalog
{
    public static P275MigrationPlan Current => new(
        [
            "20260828025922_InitialTaskSchema",
            "20260828120000_P2TaskLoop",
            "20260828130000_P2ContinuationDeleteBoundary",
            "20260831090000_P25StorageConsistency"
        ],
        [
            "20260828025922_InitialTaskSchema",
            "20260828120000_P2TaskLoop",
            "20260828130000_P2ContinuationDeleteBoundary",
            "20260831090000_P25StorageConsistency",
            "20260902141656_P3ReminderPersistence",
            "20260904090000_P304"
        ]);
}
