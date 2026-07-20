using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeoVeldrid.TestResultValidator;

internal static class Program
{
    private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static int Main(string[] args)
    {
        ValidatorArguments arguments = null;
        try
        {
            arguments = ValidatorArguments.Parse(args);
            ValidationPolicy policy = ReadPolicy(arguments.PolicyPath);
            SuitePolicy suite = policy.Suites.SingleOrDefault(
                candidate => string.Equals(candidate.Name, arguments.Suite, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Suite '{arguments.Suite}' does not exist in '{arguments.PolicyPath}'.");

            ValidationSummary summary = Validate(arguments.ResultsPath, suite);
            WriteSummary(summary, arguments.OutputPath);

            if (summary.Errors.Count != 0)
            {
                foreach (string error in summary.Errors)
                {
                    Console.Error.WriteLine($"Result integrity failure: {error}");
                }
                return 1;
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (arguments != null && !string.IsNullOrWhiteSpace(arguments.OutputPath))
            {
                ValidationSummary failure = ValidationSummary.CreateInfrastructureFailure(
                    arguments.Suite,
                    arguments.ResultsPath,
                    exception);
                try
                {
                    WriteSummary(failure, arguments.OutputPath);
                }
                catch (Exception outputException)
                {
                    Console.Error.WriteLine(
                        $"Unable to write validator failure summary to '{arguments.OutputPath}': "
                        + outputException);
                }
            }
            return 2;
        }
    }

    internal static ValidationSummary Validate(string resultsPath, SuitePolicy policy)
    {
        ValidationPolicyContract.ValidateSuite(policy);
        return new TrxResultValidator(policy).Validate(resultsPath);
    }

    internal static ValidationPolicy ReadPolicy(string path)
    {
        ValidationPolicy policy = JsonSerializer.Deserialize<ValidationPolicy>(
            File.ReadAllText(path),
            s_jsonOptions)
            ?? throw new InvalidDataException($"'{path}' did not contain a validation policy.");
        ValidationPolicyContract.Validate(policy);
        return policy;
    }

    private static void WriteSummary(ValidationSummary summary, string outputPath)
    {
        string json = JsonSerializer.Serialize(summary, s_jsonOptions);
        Console.WriteLine(json);

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        File.WriteAllText(fullOutputPath, json + Environment.NewLine);
    }
}

internal sealed class ValidatorArguments
{
    private static readonly HashSet<string> s_knownArguments = new HashSet<string>(
        new[] { "policy", "suite", "results", "output" },
        StringComparer.OrdinalIgnoreCase);

    public required string PolicyPath { get; init; }
    public required string Suite { get; init; }
    public required string ResultsPath { get; init; }
    public string OutputPath { get; init; }

    public static ValidatorArguments Parse(string[] args)
    {
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw UsageError();
            }

            string name = args[index][2..];
            if (!s_knownArguments.Contains(name))
            {
                throw new ArgumentException($"Unknown argument '--{name}'.", nameof(args));
            }
            if (!values.TryAdd(name, args[index + 1]))
            {
                throw new ArgumentException($"Argument '--{name}' was provided more than once.", nameof(args));
            }
        }

        return new ValidatorArguments
        {
            PolicyPath = Require(values, "policy"),
            Suite = Require(values, "suite"),
            ResultsPath = Require(values, "results"),
            OutputPath = values.GetValueOrDefault("output"),
        };
    }

    private static ArgumentException UsageError() => new ArgumentException(
        "Usage: --policy <path> --suite <name> --results <trx-path> [--output <json-path>]");

    private static string Require(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out string value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required --{name} argument.");
}

internal sealed class ValidationSummary
{
    public required string Suite { get; init; }
    public required string ResultsPath { get; init; }
    public int Discovered { get; init; }
    public int Passed { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public int ExpectedDiscovered { get; init; }
    public int ExpectedSkipped { get; init; }
    public string ExpectedDiscoveredFingerprint { get; init; }
    public string DiscoveredFingerprint { get; init; }
    public string InfrastructureFailure { get; init; }
    public required List<string> Errors { get; init; }

    public static ValidationSummary CreateInfrastructureFailure(
        string suite,
        string resultsPath,
        Exception exception)
    {
        string failure = $"{exception.GetType().Name}: {exception.Message}";
        return new ValidationSummary
        {
            Suite = string.IsNullOrWhiteSpace(suite) ? "Unknown" : suite,
            ResultsPath = TryGetFullPath(resultsPath),
            InfrastructureFailure = failure,
            Errors = new List<string> { $"Validator infrastructure failure: {failure}" },
        };
    }

    private static string TryGetFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}
