using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using NeoVeldrid.TestResultValidator;
using Xunit;
using ValidatorProgram = NeoVeldrid.TestResultValidator.Program;

namespace NeoVeldrid.Tests;

public sealed class TestResultValidatorTests
{
    private const string StableReason = "NV-SKIP-CAPABILITY";

    [Fact]
    public void ValidResultAndApprovedExplicitSkipPassIntegrityGate()
    {
        SyntheticResult passed = Result("Example.Tests.Passes", "Passed");
        SyntheticResult skipped = Result(
            "Example.Tests.CapabilityTheory(value: 1)",
            "NotExecuted",
            StableReason + ": unavailable");
        SuitePolicy policy = CreatePolicy(new[] { passed, skipped });
        policy.SkipRules.Add(ExplicitRule(
            "Example.Tests.CapabilityTheory",
            StableReason,
            skipped.Name));

        ValidationSummary summary = Validate(policy, passed, skipped);

        Assert.Empty(summary.Errors);
        Assert.Equal(2, summary.Discovered);
        Assert.Equal(1, summary.Passed);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(2, summary.ExpectedDiscovered);
        Assert.Equal(1, summary.ExpectedSkipped);
        Assert.Equal(summary.ExpectedDiscoveredFingerprint, summary.DiscoveredFingerprint);
    }

    [Fact]
    public void ExactSkipIdentitySubstitutionFailsWithStableReasonAndCount()
    {
        SyntheticResult actual = Result(
            "Example.Tests.CapabilityTheory(value: 2)",
            "NotExecuted",
            StableReason + ": unavailable");
        SuitePolicy policy = CreatePolicy(new[] { actual });
        policy.SkipRules.Add(ExplicitRule(
            "Example.Tests.CapabilityTheory",
            StableReason,
            "Example.Tests.CapabilityTheory(value: 1)"));

        ValidationSummary summary = Validate(policy, actual);

        Assert.Contains(summary.Errors, error => error.Contains("value: 1", StringComparison.Ordinal));
        Assert.Contains(summary.Errors, error => error.Contains("value: 2", StringComparison.Ordinal));
    }

    [Fact]
    public void FingerprintSkipIdentitySubstitutionFailsWithStableReasonAndCount()
    {
        SyntheticResult[] expected = Enumerable.Range(0, 16)
            .Select(index => Result(
                $"Example.Tests.LargeTheory(value: {index})",
                "NotExecuted",
                StableReason + ": unavailable"))
            .ToArray();
        SyntheticResult[] actual = expected
            .Select(result => result with
            {
                ExecutionId = Guid.NewGuid().ToString("D"),
                TestId = Guid.NewGuid().ToString("D"),
            })
            .ToArray();
        actual[^1] = RenameResult(actual[^1], "Example.Tests.LargeTheory(value: 99)");

        SuitePolicy policy = CreatePolicy(actual);
        policy.SkipRules.Add(FingerprintRule(
            "Example.Tests.LargeTheory",
            StableReason,
            expected.Select(result => result.Name)));

        ValidationSummary summary = Validate(policy, actual);

        Assert.Contains(
            summary.Errors,
            error => error.Contains("Skip identity fingerprint changed", StringComparison.Ordinal));
    }

    [Fact]
    public void SkipReasonMustBeExactStableMessagePrefix()
    {
        string[] invalidMessages =
        {
            "diagnostic text containing " + StableReason + ": unavailable",
            StableReason,
            "nv-skip-capability: unavailable",
        };

        foreach (string invalidMessage in invalidMessages)
        {
            SyntheticResult skipped = Result(
                "Example.Tests.Capability",
                "NotExecuted",
                invalidMessage);
            SuitePolicy policy = CreatePolicy(new[] { skipped });
            policy.SkipRules.Add(ExplicitRule(
                "Example.Tests.Capability",
                StableReason,
                skipped.Name));

            ValidationSummary summary = Validate(policy, skipped);

            Assert.Contains(
                summary.Errors,
                error => error.Contains("no valid stable reason", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void OnlyPassedIsAcceptedAsPassingUnitOutcome()
    {
        foreach (string outcome in new[] { "Completed", "Failed", "Error" })
        {
            SyntheticResult result = Result("Example.Tests.NotAPass", outcome);
            SuitePolicy policy = CreatePolicy(new[] { result });

            ValidationSummary summary = Validate(policy, result);

            Assert.Equal(0, summary.Passed);
            Assert.Equal(1, summary.Failed);
            Assert.Contains(
                summary.Errors,
                error => error.Contains($"[{outcome}]", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void DuplicateExecutionTestAndDisplayIdentitiesFail()
    {
        SyntheticResult first = Result("Example.Tests.First", "Passed");
        SyntheticResult second = Result("Example.Tests.Second", "Passed");
        (SyntheticResult[] Results, string ExpectedError)[] cases =
        {
            (new[] { first, second with { ExecutionId = first.ExecutionId } },
                "Duplicate result execution ID"),
            (new[] { first, second with { TestId = first.TestId } },
                "Duplicate result test ID"),
            (new[] { first, RenameResult(second, first.Name) },
                "Duplicate test identity"),
        };

        foreach ((SyntheticResult[] results, string expectedError) in cases)
        {
            SuitePolicy policy = CreatePolicy(results);
            ValidationSummary summary = Validate(policy, results);

            Assert.Contains(
                summary.Errors,
                error => error.Contains(expectedError, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ResultSummaryAndCountersMustAccountForResults()
    {
        SyntheticResult result = Result("Example.Tests.Passes", "Passed");
        SuitePolicy policy = CreatePolicy(new[] { result });
        (TrxOptions Options, string ExpectedError)[] cases =
        {
            (new TrxOptions { SummaryOutcome = "Aborted" }, "ResultSummary outcome"),
            (new TrxOptions { Total = 2 }, "counter 'total'"),
            (new TrxOptions { Executed = 0 }, "counter 'executed'"),
            (new TrxOptions { Passed = 0 }, "counter 'passed'"),
            (new TrxOptions { Failed = 1 }, "counter 'failed'"),
        };

        foreach ((TrxOptions options, string expectedError) in cases)
        {
            ValidationSummary summary = Validate(policy, new[] { result }, options);
            Assert.Contains(
                summary.Errors,
                error => error.Contains(expectedError, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void DiscoveryRegressionFailsIntegrityGate()
    {
        SyntheticResult expectedOne = Result("Example.Tests.One", "Passed");
        SyntheticResult expectedTwo = Result("Example.Tests.Two", "Passed");
        SuitePolicy policy = CreatePolicy(new[] { expectedOne, expectedTwo });

        ValidationSummary summary = Validate(policy, expectedOne);

        Assert.Contains(summary.Errors, error => error.Contains("Discovered 1 tests", StringComparison.Ordinal));
        Assert.Contains(
            summary.Errors,
            error => error.Contains("Discovered-test identity fingerprint changed", StringComparison.Ordinal));
    }

    [Fact]
    public void PolicyInvariantViolationsAreRejected()
    {
        SyntheticResult result = Result("Example.Tests.Passes", "Passed");
        SuitePolicy zeroDiscovery = new SuitePolicy
        {
            Name = "InvalidZeroDiscovery",
            ExpectedDiscovered = DiscoveredFingerprint(Array.Empty<string>()),
        };
        SuitePolicy excessSkips = CreatePolicy(new[] { result });
        excessSkips.SkipRules.Add(new SkipRule
        {
            TestMethod = result.Name,
            ReasonCode = StableReason,
            Classification = "known-defect",
            ExitCondition = "Reduce the expected skip set to discovered identities.",
            ExpectedTestNames = new List<string>
            {
                result.Name + "(value: 1)",
                result.Name + "(value: 2)",
            },
        });
        SuitePolicy unknownClassification = CreatePolicy(new[] { result });
        unknownClassification.SkipRules.Add(ExplicitRule(
            result.Name,
            StableReason,
            result.Name,
            classification: "temporary"));
        SuitePolicy emptyExitCondition = CreatePolicy(new[] { result });
        emptyExitCondition.SkipRules.Add(new SkipRule
        {
            TestMethod = result.Name,
            ReasonCode = StableReason,
            Classification = "known-defect",
            ExitCondition = " ",
            ExpectedTestNames = new List<string> { result.Name },
        });
        SuitePolicy ambiguousIdentityMode = CreatePolicy(new[] { result });
        ambiguousIdentityMode.SkipRules.Add(new SkipRule
        {
            TestMethod = result.Name,
            ReasonCode = StableReason,
            Classification = "known-defect",
            ExitCondition = "Fix it.",
            ExpectedTestNames = new List<string> { result.Name },
            ExpectedFingerprint = new SkipIdentityFingerprint
            {
                Algorithm = ValidationPolicyContract.FingerprintAlgorithm,
                Count = 16,
                Sha256 = new string('0', 64),
            },
        });

        foreach (SuitePolicy invalid in new[]
        {
            zeroDiscovery,
            excessSkips,
            unknownClassification,
            emptyExitCondition,
            ambiguousIdentityMode,
        })
        {
            Assert.Throws<InvalidDataException>(() => ValidationPolicyContract.ValidateSuite(invalid));
        }
    }

    [Fact]
    public void MissingAndCorruptTrxWriteMachineReadableFailureJson()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"neoveldrid-validator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            SyntheticResult expected = Result("Example.Tests.Passes", "Passed");
            ValidationPolicy policy = new ValidationPolicy
            {
                SchemaVersion = ValidationPolicyContract.CurrentSchemaVersion,
                Suites = new List<SuitePolicy> { CreatePolicy(new[] { expected }) },
            };
            string policyPath = Path.Combine(directory, "policy.json");
            File.WriteAllText(policyPath, JsonSerializer.Serialize(policy));

            foreach (bool corrupt in new[] { false, true })
            {
                string resultsPath = Path.Combine(directory, corrupt ? "corrupt.trx" : "missing.trx");
                if (corrupt)
                {
                    File.WriteAllText(resultsPath, "<TestRun>");
                }
                string outputPath = Path.Combine(directory, corrupt ? "corrupt.json" : "missing.json");

                int exitCode = ValidatorProgram.Main(new[]
                {
                    "--policy", policyPath,
                    "--suite", "Synthetic",
                    "--results", resultsPath,
                    "--output", outputPath,
                });

                Assert.Equal(2, exitCode);
                using JsonDocument summary = JsonDocument.Parse(File.ReadAllText(outputPath));
                Assert.False(string.IsNullOrWhiteSpace(
                    summary.RootElement.GetProperty("InfrastructureFailure").GetString()));
                Assert.True(summary.RootElement.GetProperty("Errors").GetArrayLength() > 0);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IdentityFingerprintsAreOrderIndependentAndReasonSensitive()
    {
        string discoveredA = TestIdentityFingerprintComputer.Compute(new[] { "B", "A", "A" });
        string discoveredB = TestIdentityFingerprintComputer.Compute(new[] { "A", "B", "A" });
        Assert.Equal(discoveredA, discoveredB);
        Assert.NotEqual(discoveredA, TestIdentityFingerprintComputer.Compute(new[] { "A", "B" }));

        SkipIdentity[] identities =
        {
            new SkipIdentity("B", "NV-SKIP-TWO"),
            new SkipIdentity("A", "NV-SKIP-ONE"),
        };
        string skipA = SkipIdentityFingerprintComputer.Compute(identities);
        string skipB = SkipIdentityFingerprintComputer.Compute(identities.Reverse());
        Assert.Equal(skipA, skipB);
        Assert.NotEqual(
            skipA,
            SkipIdentityFingerprintComputer.Compute(new[]
            {
                new SkipIdentity("B", "NV-SKIP-TWO"),
                new SkipIdentity("A", "NV-SKIP-CHANGED"),
            }));
    }

    [Fact]
    public void ResultsDefinitionsAndEntriesMustJoinExactly()
    {
        SyntheticResult result = Result("Example.Tests.Passes", "Passed");
        SuitePolicy policy = CreatePolicy(new[] { result });

        ValidationSummary missingDefinition = Validate(
            policy,
            new[] { result },
            new TrxOptions { OmitLastDefinition = true });
        ValidationSummary missingEntry = Validate(
            policy,
            new[] { result },
            new TrxOptions { OmitLastEntry = true });

        Assert.Contains(
            missingDefinition.Errors,
            error => error.Contains("test definitions", StringComparison.Ordinal));
        Assert.Contains(
            missingEntry.Errors,
            error => error.Contains("test entries", StringComparison.Ordinal));
    }

    [Fact]
    public void KnownXunitAndBlameRunInfoWarningsPassIntegrityGate()
    {
        SyntheticResult skipped = Result(
            "Example.Tests.CapabilityTheory(value: 1)",
            "NotExecuted",
            StableReason + ": unavailable");
        SuitePolicy policy = CreatePolicy(new[] { skipped });
        policy.SkipRules.Add(ExplicitRule(
            "Example.Tests.CapabilityTheory",
            StableReason,
            skipped.Name));
        TrxOptions options = new TrxOptions();
        options.RunInfos.Add(new SyntheticRunInfo(
            "Warning",
            $"[xUnit.net 00:00:01.23]     {skipped.Name} [SKIP]"));
        options.RunInfos.Add(new SyntheticRunInfo(
            "Warning",
            TrxResultValidator.BlameCompletionRunInfoText));

        ValidationSummary summary = Validate(policy, new[] { skipped }, options);

        Assert.Empty(summary.Errors);
    }

    [Fact]
    public void UnexpectedRunInfoWarningFailsIntegrityGate()
    {
        SyntheticResult passed = Result("Example.Tests.Passes", "Passed");
        SuitePolicy policy = CreatePolicy(new[] { passed });
        TrxOptions unexpectedWarning = new TrxOptions();
        unexpectedWarning.RunInfos.Add(new SyntheticRunInfo(
            "Warning",
            "The test adapter reported a partial discovery warning."));

        ValidationSummary warningSummary = Validate(policy, new[] { passed }, unexpectedWarning);

        Assert.Contains(
            warningSummary.Errors,
            error => error.Contains("unapproved run-level 'Warning'", StringComparison.Ordinal));

        SyntheticResult skipped = Result(
            "Example.Tests.CapabilityTheory(value: 1)",
            "NotExecuted",
            StableReason + ": unavailable");
        policy = CreatePolicy(new[] { skipped });
        policy.SkipRules.Add(ExplicitRule(
            "Example.Tests.CapabilityTheory",
            StableReason,
            skipped.Name));
        TrxOptions mismatchedSkip = new TrxOptions();
        mismatchedSkip.RunInfos.Add(new SyntheticRunInfo(
            "Warning",
            "[xUnit.net 00:00:01.23]     Example.Tests.CapabilityTheory(value: 2) [SKIP]"));

        ValidationSummary skipSummary = Validate(policy, new[] { skipped }, mismatchedSkip);

        Assert.Contains(
            skipSummary.Errors,
            error => error.Contains("only 0 matching skipped result(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void ResultMustBelongToDefinitionTestMethod()
    {
        SyntheticResult result = Result("Example.Tests.Passes", "Passed") with
        {
            DefinitionClassName = "Substituted.Tests",
            DefinitionMethodName = "DifferentTest",
        };
        SuitePolicy policy = CreatePolicy(new[] { result });

        ValidationSummary summary = Validate(policy, result);

        Assert.Contains(
            summary.Errors,
            error => error.Contains("does not belong to TestMethod", StringComparison.Ordinal));
    }

    [Fact]
    public void TruncatedParameterizedDefinitionNameUsesIntactMethodIdentity()
    {
        string resultName = "Example.Tests.LongTheory(value: " + new string('x', 500) + ")";
        SyntheticResult result = Result(resultName, "Passed") with
        {
            DefinitionName = resultName[..444] + "\u00B7\u00B7\u00B7",
        };
        SuitePolicy policy = CreatePolicy(new[] { result });

        ValidationSummary summary = Validate(policy, result);

        Assert.Empty(summary.Errors);
    }

    [Fact]
    public void ParameterizedResultMayUseMethodIdentityAsDefinitionName()
    {
        SyntheticResult result = Result(
            "Example.Tests.Theory(value: 42)",
            "Passed") with
        {
            DefinitionName = "Example.Tests.Theory",
        };
        SuitePolicy policy = CreatePolicy(new[] { result });

        ValidationSummary summary = Validate(policy, result);

        Assert.Empty(summary.Errors);
    }

    private static SuitePolicy CreatePolicy(
        IReadOnlyCollection<SyntheticResult> expectedResults)
    {
        string[] expectedNames = expectedResults.Select(result => result.Name).ToArray();
        return new SuitePolicy
        {
            Name = "Synthetic",
            ExpectedDiscovered = DiscoveredFingerprint(expectedNames),
        };
    }

    private static TestIdentityFingerprint DiscoveredFingerprint(IEnumerable<string> names)
    {
        string[] materialized = names.ToArray();
        return new TestIdentityFingerprint
        {
            Algorithm = ValidationPolicyContract.TestIdentityFingerprintAlgorithm,
            Count = materialized.Length,
            Sha256 = TestIdentityFingerprintComputer.Compute(materialized),
        };
    }

    private static SkipRule ExplicitRule(
        string testMethod,
        string reasonCode,
        string expectedName,
        string classification = "device-capability") => new SkipRule
    {
        TestMethod = testMethod,
        ReasonCode = reasonCode,
        Classification = classification,
        ExitCondition = "Provide the capability.",
        ExpectedTestNames = new List<string> { expectedName },
    };

    private static SkipRule FingerprintRule(
        string testMethod,
        string reasonCode,
        IEnumerable<string> expectedNames)
    {
        SkipIdentity[] identities = expectedNames
            .Select(name => new SkipIdentity(name, reasonCode))
            .ToArray();
        return new SkipRule
        {
            TestMethod = testMethod,
            ReasonCode = reasonCode,
            Classification = "device-capability",
            ExitCondition = "Provide the capability.",
            ExpectedFingerprint = new SkipIdentityFingerprint
            {
                Algorithm = ValidationPolicyContract.FingerprintAlgorithm,
                Count = identities.Length,
                Sha256 = SkipIdentityFingerprintComputer.Compute(identities),
            },
        };
    }

    private static ValidationSummary Validate(
        SuitePolicy policy,
        params SyntheticResult[] results) =>
        Validate(policy, results, new TrxOptions());

    private static ValidationSummary Validate(
        SuitePolicy policy,
        IReadOnlyList<SyntheticResult> results,
        TrxOptions options)
    {
        string path = Path.Combine(Path.GetTempPath(), $"neoveldrid-results-{Guid.NewGuid():N}.trx");
        try
        {
            CreateDocument(results, options).Save(path);
            return ValidatorProgram.Validate(path, policy);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static XDocument CreateDocument(
        IReadOnlyList<SyntheticResult> results,
        TrxOptions options)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        int passed = results.Count(result => result.Outcome == "Passed");
        int skipped = results.Count(result => result.Outcome == "NotExecuted" || result.Outcome == "Skipped");
        int failed = results.Count - passed - skipped;

        IEnumerable<XElement> resultElements = results.Select(result => new XElement(
            ns + "UnitTestResult",
            new XAttribute("executionId", result.ExecutionId),
            new XAttribute("testId", result.TestId),
            new XAttribute("testName", result.Name),
            new XAttribute("outcome", result.Outcome),
            result.ErrorMessage == null
                ? null
                : new XElement(
                    ns + "Output",
                    new XElement(
                        ns + "ErrorInfo",
                        new XElement(ns + "Message", result.ErrorMessage)))));
        IEnumerable<SyntheticResult> definitions = options.OmitLastDefinition
            ? results.Take(Math.Max(0, results.Count - 1))
            : results;
        IEnumerable<SyntheticResult> entries = options.OmitLastEntry
            ? results.Take(Math.Max(0, results.Count - 1))
            : results;

        return new XDocument(
            new XElement(
                ns + "TestRun",
                new XElement(ns + "Results", resultElements),
                new XElement(
                    ns + "TestDefinitions",
                    definitions.Select(result => new XElement(
                        ns + "UnitTest",
                        new XAttribute("id", result.TestId),
                        new XAttribute("name", result.DefinitionName),
                        new XElement(
                            ns + "Execution",
                            new XAttribute("id", result.ExecutionId)),
                        new XElement(
                            ns + "TestMethod",
                            new XAttribute("className", result.DefinitionClassName),
                            new XAttribute("name", result.DefinitionMethodName))))),
                new XElement(
                    ns + "TestEntries",
                    entries.Select(result => new XElement(
                        ns + "TestEntry",
                        new XAttribute("executionId", result.ExecutionId),
                        new XAttribute("testId", result.TestId)))),
                new XElement(
                    ns + "ResultSummary",
                    new XAttribute("outcome", options.SummaryOutcome ?? (failed == 0 ? "Completed" : "Failed")),
                    new XElement(
                        ns + "Counters",
                        new XAttribute("total", options.Total ?? results.Count),
                        new XAttribute("executed", options.Executed ?? (results.Count - skipped)),
                        new XAttribute("passed", options.Passed ?? passed),
                        new XAttribute("failed", options.Failed ?? failed),
                        new XAttribute("error", 0),
                        new XAttribute("timeout", 0),
                        new XAttribute("aborted", 0),
                        new XAttribute("inconclusive", 0),
                        new XAttribute("passedButRunAborted", 0),
                        new XAttribute("notRunnable", 0),
                        new XAttribute("notExecuted", 0),
                        new XAttribute("disconnected", 0),
                        new XAttribute("warning", 0),
                        new XAttribute("completed", 0),
                        new XAttribute("inProgress", 0),
                        new XAttribute("pending", 0)),
                    options.RunInfos.Count == 0
                        ? null
                        : new XElement(
                            ns + "RunInfos",
                            options.RunInfos.Select(runInfo => new XElement(
                                ns + "RunInfo",
                                new XAttribute("computerName", "synthetic-runner"),
                                new XAttribute("outcome", runInfo.Outcome),
                                new XAttribute("timestamp", "2026-07-19T00:00:00.0000000Z"),
                                new XElement(ns + "Text", runInfo.Text)))))));
    }

    private static SyntheticResult Result(
        string name,
        string outcome,
        string errorMessage = null)
    {
        (string className, string methodName) = SplitMethodIdentity(name);
        return new SyntheticResult(
            name,
            outcome,
            errorMessage,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            name,
            className,
            methodName);
    }

    private static SyntheticResult RenameResult(SyntheticResult result, string name)
    {
        (string className, string methodName) = SplitMethodIdentity(name);
        return result with
        {
            Name = name,
            DefinitionName = name,
            DefinitionClassName = className,
            DefinitionMethodName = methodName,
        };
    }

    private static (string ClassName, string MethodName) SplitMethodIdentity(string displayName)
    {
        int argumentsStart = displayName.IndexOf('(');
        string identity = argumentsStart < 0 ? displayName : displayName[..argumentsStart];
        int methodSeparator = identity.LastIndexOf('.');
        if (methodSeparator <= 0 || methodSeparator == identity.Length - 1)
        {
            throw new ArgumentException($"'{displayName}' is not a qualified test identity.", nameof(displayName));
        }
        return (identity[..methodSeparator], identity[(methodSeparator + 1)..]);
    }

    private sealed record SyntheticResult(
        string Name,
        string Outcome,
        string ErrorMessage,
        string ExecutionId,
        string TestId,
        string DefinitionName,
        string DefinitionClassName,
        string DefinitionMethodName);

    private sealed record SyntheticRunInfo(string Outcome, string Text);

    private sealed class TrxOptions
    {
        public string SummaryOutcome { get; init; }
        public int? Total { get; init; }
        public int? Executed { get; init; }
        public int? Passed { get; init; }
        public int? Failed { get; init; }
        public bool OmitLastDefinition { get; init; }
        public bool OmitLastEntry { get; init; }
        public List<SyntheticRunInfo> RunInfos { get; } = new List<SyntheticRunInfo>();
    }
}
