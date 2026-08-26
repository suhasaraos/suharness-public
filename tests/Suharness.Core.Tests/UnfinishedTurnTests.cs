using Suharness.Core;

namespace Suharness.Core.Tests;

/// <summary>
/// The heuristic behind the "ended its turn while describing work it had not done yet" notice. It
/// exists because the agent loop ends the moment a reply carries no tool call, so a model that
/// abandons a half-finished task looks exactly like one that answered. Getting it wrong in the other
/// direction is worse than saying nothing: a notice on an ordinary sign-off tells the reader their
/// finished turn was broken.
/// </summary>
public sealed class UnfinishedTurnTests
{
	/// <summary>
	/// The sign-offs that regressed. Every one of these contains "I'll" while saying the model is
	/// finished and waiting on the reader, which a substring scan of the closing text cannot tell
	/// apart from an abandoned step.
	/// </summary>
	[Theory]
	[InlineData("Understood — I'll stop here. If you want a concrete mcp.json for a specific server "
		+ "(stdio or Streamable HTTP), just tell me the server name or command/URL and I'll write it out.")]
	[InlineData("Done. Tell me which provider you want and I'll add it.")]
	[InlineData("That covers the refactor. Let me know if you want the tests split out too.")]
	[InlineData("I've left the migration script in place. If you'd rather I ran it now, I'll do that next.")]
	[InlineData("The build is green. I'll wait for your review before touching the release notes.")]
	[InlineData("I've applied the patch. Want me to run the suite?")]
	[InlineData("Both files are updated. Should I commit them?")]
	public void AnOrdinarySignOffIsNotReportedAsUnfinished(string text) =>
		Assert.False(HarnessRuntime.ReadsAsUnfinished(text));

	/// <summary>
	/// The case the notice is for: the model narrates the next step and then stops without taking it.
	/// </summary>
	[Theory]
	[InlineData("The latest commit is a release bump. Let me check the previous commits for actual fixes.")]
	[InlineData("The config is missing a provider. Now I'll add the KoboldCpp entry.")]
	[InlineData("Found three call sites. Next, I will update each one.")]
	[InlineData("That explains the failure.\nLet's fix the parser.")]
	[InlineData("I have the file open. I'm going to rewrite the header block.")]
	[InlineData("Two tests still fail.\n- Let me re-read the assertion.")]
	public void AnAbandonedNextStepIsReported(string text) =>
		Assert.True(HarnessRuntime.ReadsAsUnfinished(text));

	/// <summary>
	/// Only the closing sentence is an announcement of what happens next. An "I'll" earlier in the
	/// message is describing what the model already did on the way to its answer.
	/// </summary>
	[Fact]
	public void AnAnnouncementEarlierInTheMessageDoesNotCount()
	{
		Assert.False(HarnessRuntime.ReadsAsUnfinished(
			"I'll summarize what changed. The parser now accepts trailing commas, and the tests cover it."));
	}

	/// <summary>
	/// A file name or version number is not a sentence boundary, so the closing sentence is found
	/// whole rather than from the middle of a token.
	/// </summary>
	[Fact]
	public void ADottedTokenDoesNotSplitTheClosingSentence()
	{
		// Splitting at "mcp.json" would leave "json ... and I'll write it out" as the closing sentence
		// and lose the "If you want" that makes it an offer.
		Assert.False(HarnessRuntime.ReadsAsUnfinished("If you want mcp.json for that server, I'll write it out."));
		Assert.True(HarnessRuntime.ReadsAsUnfinished("The version is 3.5. Let me confirm the context window."));
	}

	[Fact]
	public void EmptyTextIsNotUnfinished() => Assert.False(HarnessRuntime.ReadsAsUnfinished("   "));
}
