namespace Suharness.Abstractions;

/// <summary>
/// A provider is selected but cannot be used as configured — for example a remote endpoint with no
/// API key. The message is written for the user and is shown verbatim in the session transcript,
/// rather than being wrapped as a transport failure.
/// </summary>
public sealed class ModelConfigurationException(string message) : Exception(message);
