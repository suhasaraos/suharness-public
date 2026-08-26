using System.Reflection;

namespace Suharness.Cli;

public static class Program
{
	/// <summary>
	/// The build's own version, read off the assembly rather than repeated as a literal. The value
	/// comes from <c>Directory.Build.props</c>, so a release is a single edit in one place.
	/// </summary>
	private static string Version =>
		typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			is { } informational && informational.Length > 0
			// A source-linked build appends "+<commit sha>"; the bare version is what a user wants.
			? informational.Split('+')[0]
			: typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

	public static async Task<int> Main(string[] args)
	{
		if (args is ["--version"]) { Console.WriteLine($"suharness {Version}"); return 0; }
		if (args.Length == 0 || args[0] is "--help" or "-h") { PrintHelp(); return 0; }
		if (args[0] != "web") { Console.Error.WriteLine($"suharness: unknown command '{args[0]}'"); PrintHelp(); return 2; }

		var host = "127.0.0.1";
		var port = "3090";
		for (var index = 1; index < args.Length; index++)
		{
			if (args[index] == "--host" && index + 1 < args.Length) host = args[++index];
			else if (args[index] == "--port" && index + 1 < args.Length) port = args[++index];
			else if (args[index] is "--help" or "-h") { Console.WriteLine("Serve the SuHarness browser UI. Options: --host <host> --port <port>"); return 0; }
			else { Console.Error.WriteLine($"suharness web: unknown option '{args[index]}'"); return 2; }
		}

		if (host is not ("127.0.0.1" or "localhost")) { Console.Error.WriteLine("suharness web: --host must be 127.0.0.1 or localhost"); return 2; }
		if (!int.TryParse(port, out var portNumber) || portNumber is < 0 or > 65535) { Console.Error.WriteLine("suharness web: --port must be between 0 and 65535"); return 2; }

		var url = $"http://{host}:{portNumber}";
		Console.WriteLine($"SuHarness Web UI: {url}");
		await Web.Program.Main(["--urls", url]);
		return 0;
	}

	private static void PrintHelp() => Console.WriteLine("""
		SuHarness - a self-extensible agent harness on .NET 10

		Usage:
		  suharness web [--host 127.0.0.1] [--port 3090]
		  suharness --version

		No model or provider is bundled. Configure one in the Web UI, or implement IAgentFactory
		in the source to add your own.
		""");
}
