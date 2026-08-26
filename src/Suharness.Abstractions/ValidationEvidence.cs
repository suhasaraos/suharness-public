using System.Text.RegularExpressions;

namespace Suharness.Abstractions;

/// <summary>
/// Reading build and test output for evidence that something actually ran and passed.
/// </summary>
/// <remarks>
/// <para>
/// Three parts of the harness need this same judgement and reached different answers on the same
/// text: the turn loop, which decides whether a mutation was validated before the turn may end; the
/// subagent service, which records what a delegated role proved; and the coding workflow, which
/// decides whether a Verifier's "pass" is backed by anything. When those disagreed, one gate could
/// accept a run the next rejected, which is a harness that argues with itself about the same
/// transcript.
/// </para>
/// <para>
/// The rules are deliberately conservative in one direction. Absence of failure is never success:
/// a run reporting zero tests, or no count at all, is not a passing test run, because the common way
/// to "pass" a test suite by accident is to run none of it.
/// </para>
/// </remarks>
public static partial class ValidationEvidence
{
	/// <summary>Whether a command was an attempt to build.</summary>
	public static bool IsBuildCommand(string command) => BuildCommand().IsMatch(command);

	/// <summary>Whether a command was an attempt to run tests.</summary>
	public static bool IsTestCommand(string command) => TestCommand().IsMatch(command);

	/// <summary>
	/// Whether output reports at least one passing test. A count is required: "tests passed" as prose
	/// is a claim, and this method exists to find the number behind it.
	/// </summary>
	public static bool HasPassingTests(string output) => PositiveCount(output, PassedCount());

	/// <summary>Whether output reports at least one failing test.</summary>
	public static bool HasFailingTests(string output) => PositiveCount(output, FailedCount());

	/// <summary>
	/// Whether output reports that nothing ran. This is the case a naive "no failures" check gets
	/// wrong, and the one a broken test discovery produces.
	/// </summary>
	public static bool HasZeroTests(string output) =>
		ZeroTests().IsMatch(output)
		|| output.Contains("No test is available", StringComparison.OrdinalIgnoreCase)
		|| output.Contains("TYPE NOT FOUND", StringComparison.OrdinalIgnoreCase);

	/// <summary>Whether output reports a failed build.</summary>
	public static bool HasBuildFailure(string output) =>
		output.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase)
		|| PositiveCount(output, ErrorCount())
		|| CompilerError().IsMatch(output);

	/// <summary>Output that demonstrates tests ran and all of them passed.</summary>
	public static bool IsSuccessfulTestRun(string output) =>
		!HasZeroTests(output) && !HasFailingTests(output) && HasPassingTests(output);

	/// <summary>
	/// Whether output looks like it came from running tests at all, however it went. Used to tell a
	/// command that attempted validation from one that merely mentioned the word.
	/// </summary>
	public static bool IsTestAttempt(string command, string output) =>
		IsTestCommand(command)
		|| HasPassingTests(output)
		|| HasZeroTests(output)
		|| HasFailingTests(output);

	/// <summary>
	/// Whether a command and its output together amount to a successful validation: a test run that
	/// passed, or a build that did not fail.
	/// </summary>
	public static bool IsSuccessfulValidation(string? command, string output)
	{
		if (string.IsNullOrWhiteSpace(command)) return false;
		if (IsTestCommand(command)) return IsSuccessfulTestRun(output);
		return IsBuildCommand(command) && !HasBuildFailure(output);
	}

	/// <summary>
	/// Whether a reported command line carries visible evidence of failure.
	/// </summary>
	/// <remarks>
	/// This reads a role's own one-line summary of what it ran — <c>csc Runner.cs =&gt; CS0103</c>,
	/// <c>dotnet test =&gt; 4 passed</c> — not raw tool output, and the two are not interchangeable.
	/// A summary names a diagnostic code with no surrounding "error", and marks its own outcome with
	/// an arrow, so it needs patterns the raw-output rules above would never see.
	/// </remarks>
	public static bool HasReportedFailure(string command) => ReportedFailure().IsMatch(command);

	/// <summary>
	/// Whether a reported command line carries a positive executed-test count. Any of the several
	/// counters a runner might print will do, since the summary is the role's own paraphrase.
	/// </summary>
	public static bool HasReportedPassingTests(string command) =>
		ReportedPassingTests().IsMatch(command) && !HasReportedFailure(command);

	/// <summary>
	/// Whether any capture of <paramref name="pattern"/> is a number greater than zero. The patterns
	/// spell both orderings a runner may print ("Passed: 4" and "4 passed") as alternatives sharing
	/// one named group, so the count is read from the branch that actually matched.
	/// </summary>
	private static bool PositiveCount(string output, Regex pattern)
	{
		foreach (Match match in pattern.Matches(output))
			if (int.TryParse(match.Groups["count"].Value, out var count) && count > 0) return true;
		return false;
	}

	[GeneratedRegex(@"\b(?:dotnet\s+(?:build|publish)|msbuild|npm\s+run\s+build|pnpm\s+build|yarn\s+build|cargo\s+build|go\s+build)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex BuildCommand();

	[GeneratedRegex(@"\b(?:dotnet\s+test|vstest|nunit|pytest|npm\s+(?:run\s+)?test|pnpm\s+test|yarn\s+test|cargo\s+test|go\s+test)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex TestCommand();

	[GeneratedRegex(@"\bPassed\s*[:=]\s*(?<count>\d+)|\b(?<count>\d+)\s+passed\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex PassedCount();

	[GeneratedRegex(@"\bFailed\s*[:=]\s*(?<count>\d+)|\b(?<count>\d+)\s+failed\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex FailedCount();

	[GeneratedRegex(@"\b(?<count>\d+)\s+Error\(s\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex ErrorCount();

	[GeneratedRegex(@"\b(?:Total Tests?|Test Count|Tests run|Executed)\s*[:=]\s*0\b|\bPassed\s*[:=]\s*0\b[^\r\n]*\bFailed\s*[:=]\s*0\b|\bNo tests? (?:were |was )?(?:found|available|discovered)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex ZeroTests();

	[GeneratedRegex(@"(?:^|\r?\n).*\berror\s+(?:[A-Z]{2,})\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex CompilerError();

	[GeneratedRegex(@"=>\s*(?:FAILED|FAIL\b|ERROR\b)|\b(?:CS|FS|BC|TS)\d{4}\b|\bBuild\s+FAILED\b|\bFailed\s*[:=]\s*[1-9]\d*|\b(?:Executed|Total Tests?|Test Count|Tests run)\s*[:=]\s*0\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex ReportedFailure();

	[GeneratedRegex(@"\b(?:Passed|Executed|Total Tests?|Test Count|Tests run)\s*[:=]\s*[1-9]\d*\b|\b[1-9]\d*\s+passed\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex ReportedPassingTests();
}
