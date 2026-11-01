using RecipeSong.Core;
using RecipeSong.Core.Soulseek;
using Slsk = global::Soulseek;

static class TransferStateChecks
{
    public static void Run()
    {
        var start = DateTime.UtcNow.AddMinutes(-1);
        Slsk.Transfer Update(Slsk.TransferStates state, long bytes, DateTime? time = null) =>
            new(Slsk.TransferDirection.Download, "peer", "track.flac", 1, state, 100, 0, bytes, 50, time, null, null, null, null);
        var row = new SoulseekTransfer(Guid.NewGuid(), false, "peer", "track.flac", "pending", 0, 100, 0, null);
        row = SoulseekSession.ApplyTransferUpdate(row, Update(Slsk.TransferStates.Queued | Slsk.TransferStates.Remotely, 0));
        Check(!row.IsRewaiting && row.WaitingSince == null, "Initial queue must not be requeue.");
        row = SoulseekSession.ApplyTransferUpdate(row, Update(Slsk.TransferStates.InProgress, 40, start));
        Check(row.IsActive && row.HasTransferred && !row.IsRewaiting, "Active transfer must record history.");
        row = SoulseekSession.ApplyTransferUpdate(row, Update(Slsk.TransferStates.Queued | Slsk.TransferStates.Remotely, 0));
        var since = row.WaitingSince;
        Check(row.IsRewaiting && row.IsRemoteQueued && since != null && row.Bytes == 40 && row.Percent == 40 && row.Speed == 0, "Requeue must retain progress and clear speed.");
        Check(row.DisplayState == L.T("다운로드 후 다시 대기 중") && row.RewaitText.Length > 0, "Requeue label and time must be visible.");
        row = SoulseekSession.ApplyTransferUpdate(row, Update(Slsk.TransferStates.Queued | Slsk.TransferStates.Remotely, 0));
        Check(row.WaitingSince == since, "Repeated queue callbacks must not reset waiting time.");
        row = SoulseekSession.ApplyTransferUpdate(row, Update(Slsk.TransferStates.InProgress, 60, start));
        Check(!row.IsRewaiting && row.WaitingSince == null && row.RewaitText == "", "Resume must clear requeue status.");
        row = SoulseekSession.ApplyTransferUpdate(row, Update(Slsk.TransferStates.Queued | Slsk.TransferStates.Locally, 60)) with { IsLocalQueued = true };
        Check(row.IsRewaiting && row.QueueStatus == L.T("내 다운로드 슬롯 대기"), "Local requeue must retain its cause.");
        row = SoulseekSession.ApplyTransferUpdate(row, Update(Slsk.TransferStates.Succeeded | Slsk.TransferStates.Completed, 100));
        Check(!row.IsRewaiting && row.IsFinished, "Completed transfer must not look queued.");
        var ended = SoulseekSession.ApplyTransferUpdate(row, Update(Slsk.TransferStates.Queued, 0));
        Check(ReferenceEquals(row, ended), "Late callback must not revive completed transfer.");
        Console.WriteLine("Initial queue, transfer/requeue/resume, progress retention, wait timing and terminal-state checks passed");
    }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
