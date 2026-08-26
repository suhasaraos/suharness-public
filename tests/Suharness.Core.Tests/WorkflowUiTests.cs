using Suharness.Abstractions;
using Suharness.Web.Components.Pages;

namespace Suharness.Core.Tests;

public sealed class WorkflowUiTests
{
	[Fact]
	public void SidebarContainsOnlyHumanSessions()
	{
		var now = DateTimeOffset.UtcNow;
		var parent = new HarnessSession(Guid.NewGuid(), "workspace", "Parent", now, now);
		var child = new HarnessSession(
			Guid.NewGuid(),
			"workspace",
			"explorer",
			now,
			now,
			ParentSessionId: parent.Id,
			DelegationDepth: 1);

		var visible = Home.SidebarSessions([child, parent]);

		Assert.Equal(parent.Id, Assert.Single(visible).Id);
	}

	[Fact]
	public void WorkflowStagesBecomeParentSessionTodos()
	{
		const string result = """
			{
			  "status": "fail",
			  "repairRounds": 1,
			  "summary": "verification failed",
			  "stages": [
			    { "role": "explorer", "status": "completed", "summary": "found files", "handoff": {} },
			    { "role": "junior-developer:1", "status": "blocked", "summary": "needs repair", "handoff": {} }
			  ]
			}
			""";

		var todos = Home.WorkflowTodoProjection(result);

		Assert.Equal(("Explorer", "completed"), todos[0]);
		Assert.Equal(("Junior Developer · 1", "pending"), todos[1]);
	}
}
