using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Suharness.Abstractions;
using Suharness.Core;
using System.Text.Json;

namespace Suharness.Web.Components.Pages;

/// <summary>
/// The modified-files panel: what this session has changed, the diff for one file, and
/// reverting a single file. Refresh is debounced because a running turn can touch a file
/// many times in a second.
/// </summary>
public partial class Home
{
	private void ScheduleModifiedFilesRefresh()
	{
		ChangesRefreshCancellation?.Cancel();
		ChangesRefreshCancellation?.Dispose();
		ChangesRefreshCancellation = new CancellationTokenSource();
		_ = RefreshModifiedFilesDebouncedAsync(ChangesRefreshCancellation.Token);
	}

	private async Task RefreshModifiedFilesDebouncedAsync(CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(250, cancellationToken);
			await InvokeAsync(() => RefreshModifiedFilesAsync(cancellationToken));
			await InvokeAsync(StateHasChanged);
		}
		catch (OperationCanceledException) { }
	}

	private async Task RefreshModifiedFilesAsync(CancellationToken cancellationToken = default)
	{
		if (ActiveSession is null || CurrentWorkspace is null)
		{
			ModifiedFiles = [];
			ChangesError = null;
			return;
		}
		try
		{
			ModifiedFiles = await Runtime.GetModifiedFilesAsync(ActiveSession.Session.Id, cancellationToken);
			ChangesError = null;
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
		{
			ModifiedFiles = [];
			ChangesError = "Changes unavailable: " + exception.Message;
		}
	}

	private string FullChangePath(SnapshotChange change) =>
		Path.GetFullPath(Path.Combine(WorkspacePath, change.Path.Replace('/', Path.DirectorySeparatorChar)));

	private string ChangeDisplayName(SnapshotChange change)
	{
		var parts = change.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
		for (var length = 1; length <= parts.Length; length++)
		{
			var candidate = string.Join('/', parts[^length..]);
			if (ModifiedFiles.Count(other => EndsWithPath(other.Path, candidate)) == 1) return candidate;
		}
		return change.Path;
	}

	private static bool EndsWithPath(string path, string suffix) =>
		path.Equals(suffix, StringComparison.OrdinalIgnoreCase)
		|| path.EndsWith('/' + suffix, StringComparison.OrdinalIgnoreCase);

	private static string ChangeKindLetter(SnapshotChangeKind kind) => kind switch
	{
		SnapshotChangeKind.Added => "A",
		SnapshotChangeKind.Deleted => "D",
		SnapshotChangeKind.Renamed => "R",
		_ => "M",
	};

	private async Task OpenFileDiffAsync(SnapshotChange change)
	{
		if (ActiveSession is null) return;
		DiffFile = new WorkspaceFileDiff(change, string.Empty);
		DiffBusy = true;
		DiffError = null;
		try
		{
			DiffFile = await Runtime.GetModifiedFileDiffAsync(ActiveSession.Session.Id, change.Path)
				?? new WorkspaceFileDiff(change, string.Empty);
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
		{
			DiffError = exception.Message;
		}
		finally { DiffBusy = false; }
	}

	private IReadOnlyList<string> DiffLines => DiffFile?.Patch.ReplaceLineEndings("\n").Split('\n') ?? [];
	private static string DiffLineClass(string line) => line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal) ? "diff-file-line"
		: line.StartsWith('+') ? "diff-added"
		: line.StartsWith('-') ? "diff-removed"
		: line.StartsWith("@@", StringComparison.Ordinal) ? "diff-hunk"
		: "";
	private void CloseFileDiff() { DiffFile = null; DiffError = null; }
	private void AskFileRevert(SnapshotChange change) { FileRevertTarget = change; FileActionError = null; }
	private void CancelFileRevert() { FileRevertTarget = null; FileActionError = null; }

	private async Task ConfirmFileRevertAsync()
	{
		if (ActiveSession is null || FileRevertTarget is null) return;
		FileActionBusy = true;
		FileActionError = null;
		try
		{
			await Runtime.RevertModifiedFileAsync(ActiveSession.Session.Id, FileRevertTarget.Path);
			CancelFileRevert();
			await RefreshModifiedFilesAsync();
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
		{
			FileActionError = exception.Message;
		}
		finally { FileActionBusy = false; }
	}
}
