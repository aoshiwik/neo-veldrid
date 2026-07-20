using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NeoVeldrid.TestResultValidator;

internal sealed class ValidationPolicy
{
    public int SchemaVersion { get; init; }
    public List<SuitePolicy> Suites { get; init; } = new List<SuitePolicy>();
}

internal sealed class SuitePolicy
{
    public required string Name { get; init; }
    public TestIdentityFingerprint ExpectedDiscovered { get; init; }
    public List<SkipRule> SkipRules { get; init; } = new List<SkipRule>();

    public long GetExpectedSkippedCount() =>
        SkipRules.Sum(rule => (long)rule.GetExpectedCount());
}

internal sealed class SkipRule
{
    public required string TestMethod { get; init; }
    public required string ReasonCode { get; init; }
    public required string Classification { get; init; }
    public required string ExitCondition { get; init; }
    public List<string> ExpectedTestNames { get; init; } = new List<string>();
    public SkipIdentityFingerprint ExpectedFingerprint { get; init; }

    public bool MatchesScope(SkipIdentity identity) =>
        string.Equals(identity.ReasonCode, ReasonCode, StringComparison.Ordinal)
        && (string.Equals(identity.TestName, TestMethod, StringComparison.Ordinal)
            || identity.TestName.StartsWith(TestMethod + "(", StringComparison.Ordinal));

    public int GetExpectedCount() =>
        ExpectedFingerprint?.Count ?? ExpectedTestNames?.Count ?? 0;
}

internal sealed class SkipIdentityFingerprint
{
    public required string Algorithm { get; init; }
    public int Count { get; init; }
    public required string Sha256 { get; init; }
}

internal sealed class TestIdentityFingerprint
{
    public required string Algorithm { get; init; }
    public int Count { get; init; }
    public required string Sha256 { get; init; }
}

internal static class ValidationPolicyContract
{
    public const int CurrentSchemaVersion = 3;
    public const string FingerprintAlgorithm = "sha256-trx-test-name-reason-nul-v1";
    public const string TestIdentityFingerprintAlgorithm = "sha256-trx-test-name-nul-v1";
    public const int MinimumFingerprintIdentityCount = 16;

    private static readonly HashSet<string> s_knownClassifications = new HashSet<string>(
        new[]
        {
            "backend-contract",
            "backend-specific-test",
            "device-capability",
            "known-defect",
        },
        StringComparer.Ordinal);

    public static void Validate(ValidationPolicy policy)
    {
        if (policy == null)
        {
            throw new InvalidDataException("The validation policy is null.");
        }
        if (policy.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported validation-policy schema {policy.SchemaVersion}; "
                + $"expected {CurrentSchemaVersion}.");
        }
        if (policy.Suites == null || policy.Suites.Count == 0)
        {
            throw new InvalidDataException("The validation policy must contain at least one suite.");
        }

        HashSet<string> suiteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SuitePolicy suite in policy.Suites)
        {
            ValidateSuite(suite);
            if (!suiteNames.Add(suite.Name))
            {
                throw new InvalidDataException($"Validation suite '{suite.Name}' is defined more than once.");
            }
        }
    }

    public static void ValidateSuite(SuitePolicy suite)
    {
        if (suite == null)
        {
            throw new InvalidDataException("A validation suite is null.");
        }
        RequireNonEmpty(suite.Name, "Suite name");
        ValidateDiscoveredFingerprint(suite);
        if (suite.SkipRules == null)
        {
            throw new InvalidDataException($"Suite '{suite.Name}' SkipRules cannot be null.");
        }

        HashSet<string> scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (SkipRule rule in suite.SkipRules)
        {
            ValidateRule(suite.Name, rule);
            string scope = rule.TestMethod + "\0" + rule.ReasonCode;
            if (!scopes.Add(scope))
            {
                throw new InvalidDataException(
                    $"Suite '{suite.Name}' defines skip scope '{rule.TestMethod}' with reason "
                    + $"'{rule.ReasonCode}' more than once.");
            }
        }

        long expectedSkipCount = suite.GetExpectedSkippedCount();
        if (expectedSkipCount > suite.ExpectedDiscovered.Count)
        {
            throw new InvalidDataException(
                $"Suite '{suite.Name}' defines {expectedSkipCount} expected skip identities, "
                + $"but only {suite.ExpectedDiscovered.Count} discovered test identities.");
        }
    }

    private static void ValidateDiscoveredFingerprint(SuitePolicy suite)
    {
        TestIdentityFingerprint fingerprint = suite.ExpectedDiscovered
            ?? throw new InvalidDataException(
                $"Suite '{suite.Name}' must define ExpectedDiscovered.");
        if (!string.Equals(
            fingerprint.Algorithm,
            TestIdentityFingerprintAlgorithm,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Suite '{suite.Name}' uses unsupported discovered-test fingerprint algorithm "
                + $"'{fingerprint.Algorithm}'.");
        }
        if (fingerprint.Count <= 0)
        {
            throw new InvalidDataException(
                $"Suite '{suite.Name}' discovered-test fingerprint count must be greater than zero.");
        }
        if (!IsLowercaseSha256(fingerprint.Sha256))
        {
            throw new InvalidDataException(
                $"Suite '{suite.Name}' discovered-test Sha256 must be 64 lowercase hexadecimal "
                + "characters.");
        }
    }

    private static void ValidateRule(string suiteName, SkipRule rule)
    {
        if (rule == null)
        {
            throw new InvalidDataException($"Suite '{suiteName}' contains a null skip rule.");
        }

        RequireNonEmpty(rule.TestMethod, $"Suite '{suiteName}' skip-rule TestMethod");
        RequireCanonicalText(rule.TestMethod, $"Suite '{suiteName}' skip-rule TestMethod");
        RequireNonEmpty(rule.ReasonCode, $"Suite '{suiteName}' skip-rule ReasonCode");
        if (!IsStableReasonCode(rule.ReasonCode))
        {
            throw new InvalidDataException(
                $"Suite '{suiteName}' skip reason '{rule.ReasonCode}' must match NV-SKIP-[A-Z0-9-]+.");
        }
        if (!s_knownClassifications.Contains(rule.Classification ?? string.Empty))
        {
            throw new InvalidDataException(
                $"Suite '{suiteName}' skip rule '{rule.TestMethod}' has unknown classification "
                + $"'{rule.Classification}'.");
        }
        RequireNonEmpty(
            rule.ExitCondition,
            $"Suite '{suiteName}' skip rule '{rule.TestMethod}' ExitCondition");

        bool hasExplicitNames = rule.ExpectedTestNames != null && rule.ExpectedTestNames.Count != 0;
        bool hasFingerprint = rule.ExpectedFingerprint != null;
        if (hasExplicitNames == hasFingerprint)
        {
            throw new InvalidDataException(
                $"Suite '{suiteName}' skip rule '{rule.TestMethod}' must define exactly one of "
                + "ExpectedTestNames or ExpectedFingerprint.");
        }

        if (hasExplicitNames)
        {
            ValidateExplicitNames(suiteName, rule);
        }
        else
        {
            ValidateFingerprint(suiteName, rule);
        }
    }

    private static void ValidateExplicitNames(string suiteName, SkipRule rule)
    {
        HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string testName in rule.ExpectedTestNames)
        {
            RequireNonEmpty(
                testName,
                $"Suite '{suiteName}' skip rule '{rule.TestMethod}' expected test name");
            RequireCanonicalText(
                testName,
                $"Suite '{suiteName}' skip rule '{rule.TestMethod}' expected test name");
            if (!string.Equals(testName, rule.TestMethod, StringComparison.Ordinal)
                && !testName.StartsWith(rule.TestMethod + "(", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Expected test '{testName}' is outside skip method '{rule.TestMethod}'.");
            }
            if (!names.Add(testName))
            {
                throw new InvalidDataException(
                    $"Suite '{suiteName}' skip rule '{rule.TestMethod}' repeats expected test "
                    + $"'{testName}'.");
            }
        }
    }

    private static void ValidateFingerprint(string suiteName, SkipRule rule)
    {
        SkipIdentityFingerprint fingerprint = rule.ExpectedFingerprint;
        if (!string.Equals(fingerprint.Algorithm, FingerprintAlgorithm, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Suite '{suiteName}' skip rule '{rule.TestMethod}' uses unsupported fingerprint "
                + $"algorithm '{fingerprint.Algorithm}'.");
        }
        if (fingerprint.Count < MinimumFingerprintIdentityCount)
        {
            throw new InvalidDataException(
                $"Suite '{suiteName}' skip rule '{rule.TestMethod}' fingerprints only "
                + $"{fingerprint.Count} identities; enumerate fewer than "
                + $"{MinimumFingerprintIdentityCount} names explicitly.");
        }
        if (!IsLowercaseSha256(fingerprint.Sha256))
        {
            throw new InvalidDataException(
                $"Suite '{suiteName}' skip rule '{rule.TestMethod}' Sha256 must be 64 lowercase "
                + "hexadecimal characters.");
        }
    }

    internal static bool IsStableReasonCode(string value)
    {
        const string prefix = "NV-SKIP-";
        if (string.IsNullOrEmpty(value)
            || !value.StartsWith(prefix, StringComparison.Ordinal)
            || value.Length == prefix.Length)
        {
            return false;
        }

        bool previousWasHyphen = true;
        for (int index = prefix.Length; index < value.Length; index++)
        {
            char character = value[index];
            if (character == '-')
            {
                if (previousWasHyphen)
                {
                    return false;
                }
                previousWasHyphen = true;
                continue;
            }

            if ((character < 'A' || character > 'Z')
                && (character < '0' || character > '9'))
            {
                return false;
            }
            previousWasHyphen = false;
        }
        return !previousWasHyphen;
    }

    private static bool IsLowercaseSha256(string value) =>
        value != null
        && value.Length == 64
        && value.All(character =>
            (character >= '0' && character <= '9')
            || (character >= 'a' && character <= 'f'));

    private static void RequireNonEmpty(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{description} cannot be empty.");
        }
    }

    private static void RequireCanonicalText(string value, string description)
    {
        if (value.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException(
                $"{description} cannot contain the fingerprint record separator.");
        }
    }
}
