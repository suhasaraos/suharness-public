using Suharness.Tools;
using System.Text.Json;

namespace Suharness.Core.Tests;

/// <summary>
/// The <c>workflow</c> tool's script surface. <see cref="WorkflowEngine"/> is a JavaScript-subset
/// interpreter the model writes against, so every operator, method, and literal form it documents is
/// a contract: a script that parses but evaluates to the wrong thing is worse than one that fails,
/// because the workflow keeps running on the wrong value.
/// </summary>
/// <remarks>
/// The engine is exercised directly rather than through the tool, so a failure names the construct
/// that broke. Scripts here never call <c>agent()</c>, which is why the subagent service is not
/// needed; delegation itself is covered in <c>OrchestrationTests</c>.
/// </remarks>
public sealed class WorkflowEngineTests
{
	private static async Task<JsonElement> RunAsync(string script)
	{
		var engine = new WorkflowEngine(subagents: null!, Guid.NewGuid(), Guid.NewGuid());
		using var arguments = JsonDocument.Parse("{}");
		return (await engine.RunAsync(script, arguments.RootElement, CancellationToken.None)).Value;
	}

	private static async Task<double> NumberAsync(string script) => (await RunAsync(script)).GetDouble();

	private static async Task<bool> BooleanAsync(string script) => (await RunAsync(script)).GetBoolean();

	private static async Task<string?> TextAsync(string script) => (await RunAsync(script)).GetString();

	/// <summary>The value rendered as compact JSON, for asserting on arrays and objects at once.</summary>
	private static async Task<string> JsonAsync(string script) => (await RunAsync(script)).GetRawText();

	[Theory]
	[InlineData("return 1 + 1;", 2)]
	[InlineData("return 7 - 4;", 3)]
	[InlineData("return 3 * 4;", 12)]
	[InlineData("return 9 / 2;", 4.5)]
	[InlineData("return 7 % 4;", 3)]
	[InlineData("return -5;", -5)]
	[InlineData("return 2 + 3 * 4;", 14)]
	[InlineData("return (2 + 3) * 4;", 20)]
	public async Task ArithmeticEvaluates(string script, double expected) =>
		Assert.Equal(expected, await NumberAsync(script));

	/// <summary>
	/// Every operator the engine tokenizes. The multi-character forms matter most: the tokenizer emits
	/// each as a single token, so a comparison that walks characters instead never matches one, and a
	/// script using <c>===</c> fails to parse rather than comparing.
	/// </summary>
	[Theory]
	[InlineData("return 1 === 1;", true)]
	[InlineData("return 1 === 2;", false)]
	[InlineData("return 1 !== 2;", true)]
	[InlineData("return 1 !== 1;", false)]
	[InlineData("return 1 == 1;", true)]
	[InlineData("return 1 != 2;", true)]
	[InlineData("return 'a' === 'a';", true)]
	[InlineData("return 'a' === 'b';", false)]
	[InlineData("return 2 > 1;", true)]
	[InlineData("return 1 < 2;", true)]
	[InlineData("return 2 >= 2;", true)]
	[InlineData("return 2 <= 1;", false)]
	[InlineData("return true && true;", true)]
	[InlineData("return true && false;", false)]
	[InlineData("return false || true;", true)]
	[InlineData("return false || false;", false)]
	[InlineData("return !false;", true)]
	public async Task ComparisonAndLogicalOperatorsEvaluate(string script, bool expected) =>
		Assert.Equal(expected, await BooleanAsync(script));

	/// <summary>
	/// Short-circuiting is observable, not incidental: the right operand of a <c>&amp;&amp;</c> whose
	/// left is false must never be evaluated, or a guard such as
	/// <c>x &amp;&amp; x.length</c> throws on the very input the guard exists for.
	/// </summary>
	[Fact]
	public async Task LogicalOperatorsShortCircuit()
	{
		Assert.False(await BooleanAsync("return false && missing;"));
		Assert.True(await BooleanAsync("return true || missing;"));
	}

	[Theory]
	[InlineData("return true ? 1 : 2;", 1)]
	[InlineData("return false ? 1 : 2;", 2)]
	[InlineData("return 1 === 1 ? 10 : 20;", 10)]
	public async Task ConditionalExpressionsEvaluate(string script, double expected) =>
		Assert.Equal(expected, await NumberAsync(script));

	/// <summary>
	/// Both arrow spellings. A single parameter is written without parentheses far more often than
	/// with them, and it is the form every array callback in the tool's own documentation uses.
	/// </summary>
	[Theory]
	[InlineData("const f = x => x * 2; return f(21);", 42)]
	[InlineData("const f = (x) => x * 2; return f(21);", 42)]
	[InlineData("const f = (a, b) => a + b; return f(40, 2);", 42)]
	[InlineData("const f = () => 42; return f();", 42)]
	[InlineData("const f = x => { return x + 1; }; return f(41);", 42)]
	[InlineData("const f = function (x) { return x + 1; }; return f(41);", 42)]
	public async Task ArrowAndFunctionFormsEvaluate(string script, double expected) =>
		Assert.Equal(expected, await NumberAsync(script));

	/// <summary>A closure keeps the scope it was created in, which is what makes a stage composable.</summary>
	[Fact]
	public async Task ClosuresCaptureTheirDefiningScope() =>
		Assert.Equal(42, await NumberAsync("const base = 40; const add = x => base + x; return add(2);"));

	[Theory]
	[InlineData("return [1, 2, 3].length;", "3")]
	[InlineData("return [1, 2, 3].map(x => x * 2);", "[2,4,6]")]
	[InlineData("return [1, 2, 3, 4].filter(x => x > 2);", "[3,4]")]
	[InlineData("return [1, 2, 3].find(x => x > 1);", "2")]
	[InlineData("return [1, 2, 3].some(x => x > 2);", "true")]
	[InlineData("return [1, 2, 3].every(x => x > 0);", "true")]
	[InlineData("return [1, 2, 3].every(x => x > 1);", "false")]
	[InlineData("return [1, 2, 3, 4].slice(1, 3);", "[2,3]")]
	[InlineData("return [1, 2, 3].includes(2);", "true")]
	[InlineData("return [1, 2, 3].includes(9);", "false")]
	[InlineData("return [1, 2, 3].reduce((total, x) => total + x, 0);", "6")]
	[InlineData("return [[1, 2], [3]].flat();", "[1,2,3]")]
	public async Task ArrayMethodsEvaluate(string script, string expected) =>
		Assert.Equal(expected, await JsonAsync(script));

	/// <summary>
	/// <c>join</c> renders the values themselves. Routing it through the engine's diagnostic formatter
	/// instead would quote every element, so a joined list of agent outputs would arrive at the next
	/// stage full of stray quotation marks.
	/// </summary>
	[Fact]
	public async Task JoinRendersValuesWithoutQuotingThem()
	{
		Assert.Equal("a-b", await TextAsync("return ['a', 'b'].join('-');"));
		Assert.Equal("a,b", await TextAsync("return ['a', 'b'].join();"));
		Assert.Equal("1|2", await TextAsync("return [1, 2].join('|');"));
	}

	[Theory]
	[InlineData("return 'hello'.length;", "5")]
	[InlineData("return '  hi  '.trim();", "\"hi\"")]
	[InlineData("return 'hi'.toUpperCase();", "\"HI\"")]
	[InlineData("return 'HI'.toLowerCase();", "\"hi\"")]
	[InlineData("return 'hello'.startsWith('he');", "true")]
	[InlineData("return 'hello'.endsWith('lo');", "true")]
	[InlineData("return 'hello'.includes('ell');", "true")]
	[InlineData("return 'hello'.includes('xyz');", "false")]
	[InlineData("return 'a,b'.split(',');", "[\"a\",\"b\"]")]
	[InlineData("return 'hello'.slice(2);", "\"llo\"")]
	public async Task StringMethodsEvaluate(string script, string expected) =>
		Assert.Equal(expected, await JsonAsync(script));

	/// <summary>
	/// String concatenation produces the strings themselves. This is the same hazard as
	/// <c>join</c>: a prompt assembled with <c>+</c> is handed straight to a model, so a stray quote
	/// is not cosmetic.
	/// </summary>
	[Fact]
	public async Task StringConcatenationDoesNotQuoteItsOperands()
	{
		Assert.Equal("ab", await TextAsync("return 'a' + 'b';"));
		Assert.Equal("a1", await TextAsync("return 'a' + 1;"));
		Assert.Equal("1a", await TextAsync("return 1 + 'a';"));
	}

	[Fact]
	public async Task ObjectAndArrayLiteralsEvaluate()
	{
		Assert.Equal("""{"a":1,"b":"two"}""", await JsonAsync("return { a: 1, b: 'two' };"));
		Assert.Equal("[1,\"a\",true,null]", await JsonAsync("return [1, 'a', true, null];"));
		Assert.Equal("1", await JsonAsync("const o = { a: 1 }; return o.a;"));
	}

	[Fact]
	public async Task StatementsEvaluate()
	{
		Assert.Equal(3, await NumberAsync("let total = 0; for (const x of [1, 2]) { total = total + x; } return total;"));
		Assert.Equal(1, await NumberAsync("if (true) { return 1; } return 2;"));
		Assert.Equal(2, await NumberAsync("if (false) { return 1; } else { return 2; } "));
	}

	/// <summary>The tool's arguments arrive as <c>args</c>, converted from JSON to engine values.</summary>
	[Fact]
	public async Task ArgumentsAreBoundAndConverted()
	{
		var engine = new WorkflowEngine(subagents: null!, Guid.NewGuid(), Guid.NewGuid());
		using var arguments = JsonDocument.Parse("""{ "name": "release", "count": 2, "tags": ["a"] }""");

		var result = await engine.RunAsync(
			"return args.name + ':' + args.count + ':' + args.tags.length;",
			arguments.RootElement,
			CancellationToken.None);

		Assert.Equal("release:2:1", result.Value.GetString());
	}

	[Fact]
	public async Task BuiltinsEvaluate()
	{
		Assert.Equal("""{"a":1}""", await TextAsync("return JSON.stringify({ a: 1 });"));
		Assert.True(await BooleanAsync("return Boolean('text');"));
		Assert.False(await BooleanAsync("return Boolean('');"));
	}

	/// <summary>
	/// <c>phase</c> and <c>log</c> are how a running workflow reports itself to the reader, so their
	/// output has to be the value the script passed rather than its diagnostic rendering.
	/// </summary>
	[Fact]
	public async Task PhaseAndLogReachTheirReporters()
	{
		var phases = new List<string>();
		var logs = new List<string>();
		var engine = new WorkflowEngine(
			subagents: null!, Guid.NewGuid(), Guid.NewGuid(), phases.Add, logs.Add);
		using var arguments = JsonDocument.Parse("{}");

		await engine.RunAsync(
			"phase('Exploring'); log('found', 2); return null;",
			arguments.RootElement,
			CancellationToken.None);

		Assert.Equal("Exploring", Assert.Single(phases));
		Assert.Equal("found 2", Assert.Single(logs));
	}

	/// <summary>
	/// An unsupported construct has to fail loudly. Silently evaluating to null would let a workflow
	/// carry on against a value the script never produced.
	/// </summary>
	[Theory]
	[InlineData("return missingIdentifier;")]
	[InlineData("return [1].nope();")]
	[InlineData("return 'a'.nope();")]
	[InlineData("return 1();")]
	[InlineData("while (true) { }")]
	public async Task UnsupportedConstructsThrow(string script) =>
		await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(script));

	/// <summary>Comments are stripped, so a documented script is still a runnable one.</summary>
	[Fact]
	public async Task CommentsAreIgnored() =>
		Assert.Equal(42, await NumberAsync("""
			// leading line comment
			const answer = 42; /* inline block */
			return answer; // trailing
			"""));
}
