using Suharness.Abstractions;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Suharness.Tools;

/// <summary>
/// A minimal, sandboxed JavaScript-subset interpreter for the <c>workflow</c>
/// tool. It supports the documented orchestration surface — literals, objects,
/// arrays, arrows, <c>agent</c>, <c>pipeline</c>, <c>parallel</c>, <c>phase</c>,
/// <c>log</c>, <c>args</c>, and a small set of array/string methods — with no
/// filesystem, network, timers, or Node.js APIs. Anything unsupported throws a
/// clear error rather than being silently ignored.
/// </summary>
public sealed class WorkflowEngine(ISubagentService subagents, Guid parentSessionId, Guid workspaceSessionId, Action<string>? phaseReporter = null, Action<string>? logReporter = null)
{
	public sealed record Result(JsonElement Value, int AgentsStarted);

	public async Task<Result> RunAsync(string script, JsonElement args, CancellationToken cancellationToken)
	{
		CancellationToken = cancellationToken;
		var parser = new Parser(script);
		var program = parser.ParseProgram();
		var evaluator = new Evaluator(this, args, cancellationToken);
		return await evaluator.RunAsync(program);
	}

	internal int AgentsStarted;
	internal Action<string>? PhaseReporter => phaseReporter;
	internal Action<string>? LogReporter => logReporter;
	internal ISubagentService Subagents => subagents;
	internal Guid ParentSessionId => parentSessionId;
	internal Guid WorkspaceSessionId => workspaceSessionId;
	internal CancellationToken CancellationToken { get; set; }

	// ── Host functions ────────────────────────────────────────────────────

	internal async Task<object?> AgentAsync(object?[] argsList)
	{
		if (argsList.Length == 0) throw new Error("agent() requires a prompt string");
		var prompt = Require<string>(argsList[0], "agent prompt");
		var opts = argsList.Length > 1 ? AsDictionary(argsList[1]) : null;

		// Reject unsupported options loudly.
		if (opts is not null)
		{
			foreach (var key in opts.Keys)
			{
				if (key is "label" or "phase" or "provider" or "model" or "schema") continue;
				throw new Error($"agent() option '{key}' is not supported (only label, phase, provider, model, schema)");
			}
		}

		var label = opts?.GetValueOrDefault("label") as string ?? prompt[..Math.Min(prompt.Length, 40)];
		var run = await subagents.StartAsync(label, prompt, parentSessionId, workspaceSessionId, runInBackground: false, cancellationToken: CancellationToken);
		try
		{
			var result = await run.Result;
			Interlocked.Increment(ref AgentsStarted);
			// An agent that did not complete has no output to hand on, with or without a schema.
			if (result.StopReason != SubagentStopReason.Completed) return null;
			return opts is not null && opts.ContainsKey("schema")
				? JsonSerializer.Deserialize<object?>(result.Output)
				: result.Output;
		}
		finally
		{
			await run.Dispose();
		}
	}

	internal async Task<object?> PipelineAsync(object? items, IReadOnlyList<object?> stages)
	{
		var list = RequireArray(items, "pipeline items");
		var results = new object?[list.Length];
		var tasks = new List<Task>();
		for (var i = 0; i < list.Length; i++)
		{
			var index = i;
			tasks.Add(Task.Run(async () =>
			{
				object? prev = list[index];
				try
				{
					foreach (var stage in stages)
					{
						var fn = Require<Closure>(stage, "pipeline stage");
						prev = await fn.InvokeAsync([prev, list[index], index]);
					}
					results[index] = prev;
				}
				// One failing item yields null rather than failing the whole pipeline, but cancellation
				// is not an item failure: swallowing it here would defeat the tool's own timeout.
				catch (Exception exception) when (exception is not OperationCanceledException)
				{
					results[index] = null;
				}
			}));
		}
		await Task.WhenAll(tasks);
		return (object?)results;
	}

	internal async Task<object?> ParallelAsync(IReadOnlyList<object?> thunks)
	{
		var tasks = thunks.Select(async thunk =>
		{
			var fn = Require<Closure>(thunk, "parallel thunk");
			try { return await fn.InvokeAsync([]); }
			catch (Exception exception) when (exception is not OperationCanceledException) { return null; }
		}).ToArray();
		return await Task.WhenAll(tasks);
	}

	// ── Helpers ───────────────────────────────────────────────────────────

	internal static T Require<T>(object? value, string what) =>
		value is T typed ? typed : throw new Error($"{what} must be a {typeof(T).Name}, got {Describe(value)}");

	internal static object?[] RequireArray(object? value, string what) =>
		value is object?[] array ? array : throw new Error($"{what} must be an array, got {Describe(value)}");

	internal static Dictionary<string, object?> AsDictionary(object? value)
	{
		if (value is Dictionary<string, object?> dict) return dict;
		throw new Error($"expected an object, got {Describe(value)}");
	}

	/// <summary>
	/// A value as an error message should name it: strings quoted, containers named by kind. This is
	/// for diagnostics only — see <see cref="Stringify"/> for the value itself.
	/// </summary>
	internal static string Describe(object? value) =>
		value switch
		{
			null => "null",
			string s => $"\"{s}\"",
			bool b => b ? "true" : "false",
			double d => d.ToString(CultureInfo.InvariantCulture),
			object?[] => "array",
			Dictionary<string, object?> => "object",
			Closure => "function",
			_ => value.GetType().Name,
		};

	/// <summary>
	/// A value as the script itself renders it, with strings as their own text. Used by <c>+</c>,
	/// <c>join</c>, and <c>log</c>, all of which produce text a model or a reader consumes directly —
	/// so a quotation mark there is content the author wrote, never punctuation the engine added.
	/// </summary>
	internal static string Stringify(object? value) =>
		value switch
		{
			null => "null",
			string s => s,
			bool b => b ? "true" : "false",
			double d => d.ToString(CultureInfo.InvariantCulture),
			object?[] items => string.Join(",", items.Select(Stringify)),
			Dictionary<string, object?> => "[object Object]",
			Closure => "function",
			_ => value.ToString() ?? string.Empty,
		};

	internal sealed class Error(string message) : Exception(message);

	// ── Closure ───────────────────────────────────────────────────────────

	internal sealed class Closure
	{
		private readonly Evaluator _owner;
		private readonly IReadOnlyList<string> _parameters;
		private readonly Ast.Node _body;
		private readonly IReadOnlyDictionary<string, object?> _scope;

		public Closure(Evaluator owner, IReadOnlyList<string> parameters, Ast.Node body, IReadOnlyDictionary<string, object?> scope)
		{
			_owner = owner;
			_parameters = parameters;
			_body = body;
			_scope = scope;
		}

		public async Task<object?> InvokeAsync(object?[] arguments)
		{
			// A closure body reads `args` from the captured scope, never from its own; the default
			// element stands in for the arguments this evaluator is not the one to supply.
			var child = new Evaluator(_owner.Engine, default, _owner.Engine.CancellationToken, _scope);
			for (var i = 0; i < _parameters.Count; i++)
				child.Scope[_parameters[i]] = i < arguments.Length ? arguments[i] : null;
			// A block body evaluates to the signal `return` raised, which stops at this boundary: the
			// caller wants the returned value, not the marker used to unwind the statements to here.
			return Evaluator.Unwrap(await child.EvalAsync(_body));
		}
	}

	// ── AST ───────────────────────────────────────────────────────────────

	internal static class Ast
	{
		public abstract record Node;
		public sealed record Number(double Value) : Node;
		public sealed record String(string Value) : Node;
		public sealed record Bool(bool Value) : Node;
		public sealed record Null : Node;
		public sealed record Identifier(string Name) : Node;
		public sealed record Array(IReadOnlyList<Node> Items) : Node;
		public sealed record Object(IReadOnlyList<(string Key, Node Value)> Properties) : Node;
		public sealed record Call(Node Callee, IReadOnlyList<Node> Arguments) : Node;
		public sealed record Member(Node Target, string Name) : Node;
		public sealed record Arrow(IReadOnlyList<string> Parameters, Node Body) : Node;
		public sealed record Binary(string Op, Node Left, Node Right) : Node;
		public sealed record Unary(string Op, Node Operand) : Node;
		public sealed record Conditional(Node Test, Node Consequent, Node Alternate) : Node;
		public sealed record Assignment(string Target, Node Value) : Node;
		public sealed record Var(string Name, Node? Initializer) : Node;
		public sealed record Return(Node? Value) : Node;
		public sealed record If(Node Test, Node Consequent, Node? Alternate) : Node;
		public sealed record Block(IReadOnlyList<Node> Statements) : Node;
		public sealed record ForOf(string Variable, Node Iterable, Node Body) : Node;
		public sealed record ExpressionStatement(Node Expression) : Node;
	}

	// ── Parser ────────────────────────────────────────────────────────────

	private sealed class Parser
	{
		private readonly List<Token> _tokens;
		private int _index;

		public Parser(string source) => _tokens = Tokenize(source);

		public Ast.Block ParseProgram()
		{
			var statements = new List<Ast.Node>();
			while (!At(TokenKind.End)) statements.Add(ParseStatement());
			return new Ast.Block(statements);
		}

		private Ast.Node ParseStatement()
		{
			if (Match(TokenKind.Keyword, "const") || Match(TokenKind.Keyword, "let") || Match(TokenKind.Keyword, "var"))
			{
				var name = RequireIdentifier();
				Ast.Node? init = null;
				if (Match(TokenKind.Punctuation, "=")) init = ParseExpression();
				ExpectPunct(";");
				return new Ast.Var(name, init);
			}
			if (Match(TokenKind.Keyword, "return"))
			{
				Ast.Node? value = null;
				if (!Check(TokenKind.Punctuation, ";")) value = ParseExpression();
				ExpectPunct(";");
				return new Ast.Return(value);
			}
			if (Match(TokenKind.Keyword, "if"))
			{
				ExpectPunct("(");
				var test = ParseExpression();
				ExpectPunct(")");
				var consequent = ParseStatement();
				Ast.Node? alternate = null;
				if (Match(TokenKind.Keyword, "else")) alternate = ParseStatement();
				return new Ast.If(test, consequent, alternate);
			}
			if (Match(TokenKind.Keyword, "for"))
			{
				ExpectPunct("(");
				if (Match(TokenKind.Keyword, "const") || Match(TokenKind.Keyword, "let"))
				{
					var variable = RequireIdentifier();
					if (!Match(TokenKind.Keyword, "of")) throw new Error("workflow only supports for..of loops");
					var iterable = ParseExpression();
					ExpectPunct(")");
					var body = ParseStatement();
					return new Ast.ForOf(variable, iterable, body);
				}
				throw new Error("workflow only supports for..of loops");
			}
			if (Match(TokenKind.Punctuation, "{"))
			{
				var statements = new List<Ast.Node>();
				while (!Match(TokenKind.Punctuation, "}"))
				{
					if (At(TokenKind.End)) throw new Error("unterminated block");
					statements.Add(ParseStatement());
				}
				return new Ast.Block(statements);
			}
			// Expression statement.
			var expression = ParseExpression();
			ExpectPunct(";");
			return new Ast.ExpressionStatement(expression);
		}

		private Ast.Node ParseExpression()
		{
			var left = ParseConditional();
			// Assignment binds loosest and associates right, so `a = b = c` and `a = test ? x : y` both
			// group the way they read. Only a bare name is assignable: this subset has no member or
			// index targets, so anything else on the left is a comparison the author mistyped.
			if (left is Ast.Identifier target && Match(TokenKind.Punctuation, "="))
				return new Ast.Assignment(target.Name, ParseExpression());
			return left;
		}

		private Ast.Node ParseConditional()
		{
			var test = ParseBinary();
			if (Match(TokenKind.Punctuation, "?"))
			{
				var consequent = ParseExpression();
				ExpectPunct(":");
				var alternate = ParseExpression();
				return new Ast.Conditional(test, consequent, alternate);
			}
			return test;
		}

		/// <summary>
		/// Binary operators grouped by precedence, loosest tier first. Each tier binds tighter than the
		/// one above it, so <c>1 + 2 * 3</c> is 7 and <c>a || b &amp;&amp; c</c> is <c>a || (b &amp;&amp; c)</c>.
		/// Ordering within a tier does not matter, because the tokenizer emits each operator whole.
		/// </summary>
		private static readonly string[][] PrecedenceTiers =
		[
			["||"],
			["&&"],
			["===", "!==", "==", "!="],
			["<", "<=", ">", ">="],
			["+", "-"],
			["*", "/", "%"],
		];

		private Ast.Node ParseBinary(int tier = 0)
		{
			if (tier == PrecedenceTiers.Length) return ParseUnary();

			var left = ParseBinary(tier + 1);
			while (true)
			{
				// The tokenizer already emits `===`, `&&`, `<=` and the rest as single tokens, so an
				// operator is matched against one token — never against a run of them.
				var op = Array.Find(PrecedenceTiers[tier], candidate => Check(TokenKind.Punctuation, candidate));
				if (op is null) return left;
				Advance();
				left = new Ast.Binary(op, left, ParseBinary(tier + 1));
			}
		}

		private Ast.Node ParseUnary()
		{
			if (Match(TokenKind.Punctuation, "!") || Match(TokenKind.Punctuation, "-"))
			{
				var op = _tokens[_index - 1].Text;
				return new Ast.Unary(op, ParseUnary());
			}
			if (Match(TokenKind.Keyword, "await"))
				return ParseUnary();
			return ParsePostfix();
		}

		private Ast.Node ParsePostfix()
		{
			var node = ParsePrimary();
			while (true)
			{
				if (Match(TokenKind.Punctuation, "."))
				{
					var name = RequireIdentifier();
					node = new Ast.Member(node, name);
					continue;
				}
				if (Match(TokenKind.Punctuation, "("))
				{
					var args = new List<Ast.Node>();
					if (!Check(TokenKind.Punctuation, ")"))
					{
						do { args.Add(ParseExpression()); } while (Match(TokenKind.Punctuation, ","));
					}
					ExpectPunct(")");
					node = new Ast.Call(node, args);
					continue;
				}
				return node;
			}
		}

		private Ast.Node ParsePrimary()
		{
			var token = Peek();
			switch (token.Kind)
			{
				case TokenKind.Number:
					Advance();
					return new Ast.Number(double.Parse(token.Text, CultureInfo.InvariantCulture));
				case TokenKind.String:
					Advance();
					return new Ast.String(token.Text);
				case TokenKind.Identifier:
					Advance();
					// `x => …`, the unparenthesized single parameter. Match has already consumed the
					// arrow, so the body follows immediately; the parenthesized forms are handled by the
					// "(" branch below. Demanding a second arrow here made every one-parameter callback
					// — which is most of them — fail to parse.
					return Match(TokenKind.Punctuation, "=>")
						? new Ast.Arrow([token.Text], ParseArrowBody())
						: new Ast.Identifier(token.Text);
				case TokenKind.Keyword:
					if (token.Text == "true") { Advance(); return new Ast.Bool(true); }
					if (token.Text == "false") { Advance(); return new Ast.Bool(false); }
					if (token.Text is "null" or "undefined") { Advance(); return new Ast.Null(); }
					if (token.Text == "function") { Advance(); return ParseFunction(); }
					throw new Error($"unexpected keyword '{token.Text}'");
				case TokenKind.Punctuation when token.Text == "(":
					Advance();
					if (Check(TokenKind.Punctuation, ")"))
					{
						Advance();
						ExpectPunct("=>");
						return new Ast.Arrow([], ParseArrowBody());
					}
					var parameters = new List<string>();
					// Either a parenthesized expression or arrow parameters.
					var saved = _index;
					var isArrowParams = true;
					try
					{
						do { parameters.Add(RequireIdentifier()); } while (Match(TokenKind.Punctuation, ","));
						ExpectPunct(")");
						ExpectPunct("=>");
					}
					catch (Error)
					{
						_index = saved;
						isArrowParams = false;
					}
					if (isArrowParams) return new Ast.Arrow(parameters, ParseArrowBody());
					var inner = ParseExpression();
					ExpectPunct(")");
					return inner;
				case TokenKind.Punctuation when token.Text == "[":
					Advance();
					var items = new List<Ast.Node>();
					if (!Check(TokenKind.Punctuation, "]"))
					{
						do { items.Add(ParseExpression()); } while (Match(TokenKind.Punctuation, ","));
					}
					ExpectPunct("]");
					return new Ast.Array(items);
				case TokenKind.Punctuation when token.Text == "{":
					Advance();
					var props = new List<(string, Ast.Node)>();
					if (!Check(TokenKind.Punctuation, "}"))
					{
						do
						{
							var key = Peek().Kind is TokenKind.Identifier or TokenKind.String or TokenKind.Keyword
								? Peek().Text : throw new Error("expected a property name");
							Advance();
							ExpectPunct(":");
							props.Add((key, ParseExpression()));
						} while (Match(TokenKind.Punctuation, ","));
					}
					ExpectPunct("}");
					return new Ast.Object(props);
				default:
					throw new Error($"unexpected token '{token.Text}'");
			}
		}

		private Ast.Node ParseFunction()
		{
			ExpectPunct("(");
			var parameters = new List<string>();
			if (!Check(TokenKind.Punctuation, ")"))
			{
				do { parameters.Add(RequireIdentifier()); } while (Match(TokenKind.Punctuation, ","));
			}
			ExpectPunct(")");
			ExpectPunct("{");
			var statements = new List<Ast.Node>();
			while (!Match(TokenKind.Punctuation, "}")) statements.Add(ParseStatement());
			return new Ast.Arrow(parameters, new Ast.Block(statements));
		}

		private Ast.Node ParseArrowBody()
		{
			if (Match(TokenKind.Punctuation, "{"))
			{
				var statements = new List<Ast.Node>();
				while (!Match(TokenKind.Punctuation, "}")) statements.Add(ParseStatement());
				return new Ast.Block(statements);
			}
			return ParseExpression();
		}

		private string RequireIdentifier()
		{
			var token = Peek();
			if (token.Kind is not (TokenKind.Identifier or TokenKind.Keyword))
				throw new Error($"expected an identifier, got '{token.Text}'");
			Advance();
			return token.Text;
		}

		private void ExpectPunct(string text)
		{
			if (!Match(TokenKind.Punctuation, text)) throw new Error($"expected '{text}', got '{Peek().Text}'");
		}

		private bool Match(TokenKind kind, string text)
		{
			if (!Check(kind, text)) return false;
			Advance();
			return true;
		}

		private bool Match(TokenKind kind)
		{
			if (!Check(kind)) return false;
			Advance();
			return true;
		}

		private bool Check(TokenKind kind, string? text = null) =>
			!At(TokenKind.End) && Peek().Kind == kind && (text is null || Peek().Text == text);

		private bool At(TokenKind kind) => _index >= _tokens.Count || Peek().Kind == kind;

		private Token Peek() => _tokens[_index];

		private void Advance() => _index++;

		// ── Tokenizer ──────────────────────────────────────────────────────

		private enum TokenKind { Number, String, Identifier, Keyword, Punctuation, End }

		private readonly record struct Token(TokenKind Kind, string Text);

		private static readonly HashSet<string> Keywords =
		[
			"const", "let", "var", "return", "if", "else", "for", "of", "in",
			"true", "false", "null", "undefined", "function", "await", "new", "typeof",
		];

		private static List<Token> Tokenize(string source)
		{
			var tokens = new List<Token>();
			var i = 0;
			while (i < source.Length)
			{
				var c = source[i];
				if (char.IsWhiteSpace(c)) { i++; continue; }
				if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
				{
					while (i < source.Length && source[i] != '\n') i++;
					continue;
				}
				if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
				{
					i += 2;
					while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
					i += 2;
					continue;
				}
				if (char.IsDigit(c))
				{
					var start = i;
					while (i < source.Length && (char.IsDigit(source[i]) || source[i] == '.')) i++;
					tokens.Add(new Token(TokenKind.Number, source[start..i]));
					continue;
				}
				if (c == '"' || c == '\'' || c == '`')
				{
					var quote = c;
					i++;
					var sb = new StringBuilder();
					while (i < source.Length && source[i] != quote)
					{
						if (source[i] == '\\' && i + 1 < source.Length) { i++; }
						sb.Append(source[i]);
						i++;
					}
					i++; // closing quote
					tokens.Add(new Token(TokenKind.String, sb.ToString()));
					continue;
				}
				if (char.IsLetter(c) || c == '_' || c == '$')
				{
					var start = i;
					while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_' || source[i] == '$')) i++;
					var text = source[start..i];
					tokens.Add(new Token(Keywords.Contains(text) ? TokenKind.Keyword : TokenKind.Identifier, text));
					continue;
				}
				// Multi-char punctuation.
				foreach (var op in new[] { "===", "!==", "=>", "==", "!=", "<=", ">=", "&&", "||" })
				{
					if (i + op.Length <= source.Length && source.Substring(i, op.Length) == op)
					{
						tokens.Add(new Token(TokenKind.Punctuation, op));
						i += op.Length;
						goto next;
					}
				}
				if ("{}[](),.;:?=+-*/%<>!".Contains(c))
				{
					tokens.Add(new Token(TokenKind.Punctuation, c.ToString()));
					i++;
					continue;
				}
				throw new Error($"unexpected character '{c}' in workflow script");
			next:;
			}
			tokens.Add(new Token(TokenKind.End, string.Empty));
			return tokens;
		}
	}

	// ── Evaluator ─────────────────────────────────────────────────────────

	internal sealed class Evaluator
	{
		private readonly WorkflowEngine _engine;
		private readonly JsonElement _args;
		private readonly CancellationToken _cancellationToken;
		private readonly Dictionary<string, object?> _scope = new(StringComparer.Ordinal);
		internal Dictionary<string, object?> Scope => _scope;

		internal WorkflowEngine Engine => _engine;

		public Evaluator(WorkflowEngine engine, JsonElement args, CancellationToken cancellationToken, IReadOnlyDictionary<string, object?>? parentScope = null)
		{
			_engine = engine;
			_args = args;
			_cancellationToken = cancellationToken;
			if (parentScope is not null)
				foreach (var (key, value) in parentScope) _scope[key] = value;
		}

		public async Task<Result> RunAsync(Ast.Block program)
		{
			DefineBuiltins();
			object? result = null;
			foreach (var statement in program.Statements)
			{
				var value = await EvalAsync(statement);
				if (value is ReturnSignal signal)
				{
					result = signal.Value;
					break;
				}
			}
			return new Result(ToJsonElement(result), _engine.AgentsStarted);
		}

		private sealed record ReturnSignal(object? Value);

		/// <summary>The value behind a <c>return</c>, for the boundaries that stop it unwinding further.</summary>
		internal static object? Unwrap(object? value) => value is ReturnSignal signal ? signal.Value : value;

		private void DefineBuiltins()
		{
			_scope["args"] = JsonToObject(_args);
			_scope["agent"] = new AsyncBuiltin("agent", a => _engine.AgentAsync(a));
			_scope["pipeline"] = new AsyncBuiltin("pipeline", a => _engine.PipelineAsync(a[0], RequireArray(a[1], "pipeline stages")));
			_scope["parallel"] = new AsyncBuiltin("parallel", a => _engine.ParallelAsync(RequireArray(a[0], "parallel thunks")));
			_scope["phase"] = new SyncBuiltin("phase", a => { _engine.PhaseReporter?.Invoke(Require<string>(a[0], "phase title")); return null; });
			_scope["log"] = new SyncBuiltin("log", a => { _engine.LogReporter?.Invoke(string.Join(' ', a.Select(Stringify))); return null; });
			_scope["Boolean"] = new SyncBuiltin("Boolean", a => a.Length == 0 ? false : Truthy(a[0]));
			_scope["JSON"] = new Dictionary<string, object?> { ["stringify"] = new SyncBuiltin("stringify", a => JsonSerializer.Serialize(a[0])) };
		}

		// A simple dual-mode builtin: sync or async. The name is carried for readability at the call
		// sites in DefineBuiltins, which is why it is a positional parameter rather than a property.
		private sealed class SyncBuiltin(string name, Func<object?[], object?> fn)
		{
			public object? Invoke(object?[] args) => fn(args);
			public override string ToString() => name;
		}

		private sealed class AsyncBuiltin(string name, Func<object?[], Task<object?>> fn)
		{
			public Task<object?> Invoke(object?[] args) => fn(args);
			public override string ToString() => name;
		}

		public async Task<object?> EvalAsync(Ast.Node node)
		{
			_cancellationToken.ThrowIfCancellationRequested();
			switch (node)
			{
				case Ast.Number n: return n.Value;
				case Ast.String s: return s.Value;
				case Ast.Bool b: return b.Value;
				case Ast.Null: return null;
				case Ast.Identifier id:
					// `undefined` is a keyword, so it never reaches here as an identifier; it parses
					// straight to Ast.Null.
					if (_scope.TryGetValue(id.Name, out var value)) return value;
					throw new Error($"'{id.Name}' is not defined");
				case Ast.Array array:
					var items = new object?[array.Items.Count];
					for (var i = 0; i < array.Items.Count; i++) items[i] = await EvalAsync(array.Items[i]);
					return items;
				case Ast.Object obj:
					var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
					foreach (var (key, propValue) in obj.Properties) dict[key] = await EvalAsync(propValue);
					return dict;
				case Ast.Var v:
					_scope[v.Name] = v.Initializer is null ? null : await EvalAsync(v.Initializer);
					return null;
				case Ast.Return r: return new ReturnSignal(r.Value is null ? null : await EvalAsync(r.Value));
				case Ast.Block b:
					object? blockLast = null;
					foreach (var statement in b.Statements)
					{
						var stepValue = await EvalAsync(statement);
						if (stepValue is ReturnSignal) return stepValue;
						blockLast = stepValue;
					}
					return blockLast;
				case Ast.ExpressionStatement e: return await EvalAsync(e.Expression);
				case Ast.If i:
					return Truthy(await EvalAsync(i.Test))
					? await EvalAsync(i.Consequent)
					: i.Alternate is null ? null : await EvalAsync(i.Alternate);
				case Ast.ForOf f:
					var iterable = RequireArray(await EvalAsync(f.Iterable), "for..of iterable");
					foreach (var item in iterable)
					{
						_scope[f.Variable] = item;
						var bodyValue = await EvalAsync(f.Body);
						if (bodyValue is ReturnSignal) return bodyValue;
					}
					return null;
				case Ast.Binary bin:
					return await EvalBinaryAsync(bin);
				case Ast.Unary u:
					var operand = await EvalAsync(u.Operand);
					return u.Op switch
					{
						"!" => !Truthy(operand),
						"-" => -(Require<double>(operand, "unary operand")),
						_ => throw new Error($"unsupported unary operator '{u.Op}'"),
					};
				case Ast.Conditional c:
					return Truthy(await EvalAsync(c.Test))
					? await EvalAsync(c.Consequent)
					: await EvalAsync(c.Alternate);
				case Ast.Arrow a: return new Closure(this, a.Parameters, a.Body, _scope);
				case Ast.Member m: return await EvalMemberAsync(m);
				case Ast.Call c: return await EvalCallAsync(c);
				case Ast.Assignment assign:
					var value2 = await EvalAsync(assign.Value);
					_scope[assign.Target] = value2;
					return value2;
				default:
					throw new Error($"unsupported node {node.GetType().Name}");
			}
		}

		private async Task<object?> EvalBinaryAsync(Ast.Binary bin)
		{
			// Short-circuit logic.
			if (bin.Op == "&&") return Truthy(await EvalAsync(bin.Left)) && Truthy(await EvalAsync(bin.Right));
			if (bin.Op == "||") return Truthy(await EvalAsync(bin.Left)) || Truthy(await EvalAsync(bin.Right));

			var left = await EvalAsync(bin.Left);
			var right = await EvalAsync(bin.Right);
			return bin.Op switch
			{
				"+" => Add(left, right),
				"-" => Require<double>(left, "left") - Require<double>(right, "right"),
				"*" => Require<double>(left, "left") * Require<double>(right, "right"),
				"/" => Require<double>(left, "left") / Require<double>(right, "right"),
				"%" => Require<double>(left, "left") % Require<double>(right, "right"),
				"===" => Equals(left, right),
				"!==" => !Equals(left, right),
				"==" => LooseEquals(left, right),
				"!=" => !LooseEquals(left, right),
				"<" => Compare(left, right) < 0,
				"<=" => Compare(left, right) <= 0,
				">" => Compare(left, right) > 0,
				">=" => Compare(left, right) >= 0,
				_ => throw new Error($"unsupported operator '{bin.Op}'"),
			};
		}

		private static object? Add(object? left, object? right)
		{
			// Trimming quotes off the diagnostic rendering instead would also strip a quote the author
			// actually wrote at the start or end of a string.
			if (left is string || right is string) return string.Concat(Stringify(left), Stringify(right));
			return Require<double>(left, "left") + Require<double>(right, "right");
		}

		private static int Compare(object? left, object? right) =>
			Require<double>(left, "comparison").CompareTo(Require<double>(right, "comparison"));

		private static bool LooseEquals(object? left, object? right)
		{
			if (left is double l && right is double r) return l == r;
			if (left is string ls && right is string rs) return ls == rs;
			return ReferenceEquals(left, right);
		}

		private async Task<object?> EvalMemberAsync(Ast.Member member)
		{
			var target = await EvalAsync(member.Target);
			if (target is object?[] array)
			{
				if (member.Name == "length") return (double)array.Length;
				throw new Error($"arrays have no property '{member.Name}'");
			}
			if (target is Dictionary<string, object?> dict)
			{
				return dict.GetValueOrDefault(member.Name);
			}
			if (target is string str)
			{
				if (member.Name == "length") return (double)str.Length;
				throw new Error($"strings have no property '{member.Name}'");
			}
			throw new Error($"cannot read property '{member.Name}' of {Describe(target)}");
		}

		private async Task<object?> EvalCallAsync(Ast.Call call)
		{
			var arguments = new object?[call.Arguments.Count];

			if (call.Callee is Ast.Member member)
			{
				var target = await EvalAsync(member.Target);
				for (var i = 0; i < arguments.Length; i++) arguments[i] = await EvalAsync(call.Arguments[i]);
				// A member call is either a method on a built-in value (arr.map) or a callable stored on
				// an object (JSON.stringify). Resolving the property first is what lets the second case
				// dispatch as an ordinary call instead of being looked for in the method tables below.
				return target is Dictionary<string, object?> map && map.TryGetValue(member.Name, out var stored)
					? await InvokeCallableAsync(stored, arguments)
					: await InvokeMethodAsync(target, member.Name, arguments);
			}

			var callee = await EvalAsync(call.Callee);
			for (var i = 0; i < arguments.Length; i++) arguments[i] = await EvalAsync(call.Arguments[i]);
			return await InvokeCallableAsync(callee, arguments);
		}

		private static async Task<object?> InvokeCallableAsync(object? callee, object?[] arguments) => callee switch
		{
			Closure closure => await closure.InvokeAsync(arguments),
			SyncBuiltin sync => sync.Invoke(arguments),
			AsyncBuiltin asynchronous => await asynchronous.Invoke(arguments),
			_ => throw new Error($"'{Describe(callee)}' is not callable"),
		};

		private static async Task<object?> InvokeMethodAsync(object? target, string name, object?[] args)
		{
			if (target is object?[] array)
			{
				switch (name)
				{
					case "map":
					{
						var fn = Require<Closure>(args[0], "map callback");
						var results = new object?[array.Length];
						for (var i = 0; i < array.Length; i++) results[i] = await fn.InvokeAsync([array[i], (double)i]);
						return results;
					}
					case "filter":
					{
						var fn = args[0];
						var results = new List<object?>();
						for (var i = 0; i < array.Length; i++)
						{
							object? keep;
							if (fn is Closure closure) keep = await closure.InvokeAsync([array[i], (double)i]);
							else if (fn is SyncBuiltin syncBuiltin) keep = syncBuiltin.Invoke([array[i]]);
							else keep = fn;
							if (Truthy(keep)) results.Add(array[i]);
						}
						return results.ToArray();
					}
					case "join":
					{
						var sep = args.Length > 0 && args[0] is string s ? s : ",";
						return string.Join(sep, array.Select(Stringify));
					}
					case "find":
					{
						var fn = Require<Closure>(args[0], "find callback");
						foreach (var item in array)
							if (Truthy(await fn.InvokeAsync([item]))) return item;
						return null;
					}
					case "some":
					{
						var fn = Require<Closure>(args[0], "some callback");
						foreach (var item in array)
							if (Truthy(await fn.InvokeAsync([item]))) return true;
						return false;
					}
					case "every":
					{
						var fn = Require<Closure>(args[0], "every callback");
						foreach (var item in array)
							if (!Truthy(await fn.InvokeAsync([item]))) return false;
						return true;
					}
					case "flat":
						return array.SelectMany(item => item is object?[] inner ? inner : [item]).ToArray();
					case "slice":
					{
						var start = args.Length > 0 && args[0] is double d ? (int)d : 0;
						var end = args.Length > 1 && args[1] is double e ? (int)e : array.Length;
						return array.Skip(start).Take(Math.Max(0, end - start)).ToArray();
					}
					case "includes":
					{
						foreach (var item in array)
							if (LooseEquals(item, args[0])) return true;
						return false;
					}
					case "reduce":
					{
						var fn = Require<Closure>(args[0], "reduce callback");
						var acc = args.Length > 1 ? args[1] : array.Length > 0 ? array[0] : null;
						var start = args.Length > 1 ? 0 : 1;
						for (var i = start; i < array.Length; i++) acc = await fn.InvokeAsync([acc, array[i], (double)i]);
						return acc;
					}
					default:
						throw new Error($"arrays have no method '{name}'");
				}
			}
			if (target is string str)
			{
				switch (name)
				{
					case "trim": return str.Trim();
					case "toUpperCase": return str.ToUpperInvariant();
					case "toLowerCase": return str.ToLowerInvariant();
					case "startsWith": return str.StartsWith(Require<string>(args[0], "argument"), StringComparison.Ordinal);
					case "endsWith": return str.EndsWith(Require<string>(args[0], "argument"), StringComparison.Ordinal);
					case "includes": return str.Contains(Require<string>(args[0], "argument"), StringComparison.Ordinal);
					case "split": return str.Split(Require<string>(args[0], "separator")).Cast<object?>().ToArray();
					case "slice": return str[(int)(args.Length > 0 && args[0] is double s ? s : 0)..];
					default:
						throw new Error($"strings have no method '{name}'");
				}
			}
			throw new Error($"cannot call method '{name}' on {Describe(target)}");
		}

		private static bool Truthy(object? value) => value switch
		{
			null => false,
			bool b => b,
			double d => d != 0,
			string s => s.Length > 0,
			_ => true,
		};

		private static object? JsonToObject(JsonElement element) => element.ValueKind switch
		{
			JsonValueKind.Null or JsonValueKind.Undefined => null,
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			JsonValueKind.String => element.GetString(),
			JsonValueKind.Number => element.GetDouble(),
			JsonValueKind.Array => element.EnumerateArray().Select(JsonToObject).ToArray(),
			JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => JsonToObject(p.Value), StringComparer.Ordinal),
			_ => null,
		};

		private static JsonElement ToJsonElement(object? value)
		{
			var json = JsonSerializer.Serialize(value);
			using var doc = JsonDocument.Parse(json);
			return doc.RootElement.Clone();
		}
	}
}
