using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NeoVeldrid.TestResultValidator;

internal sealed class TrxResultValidator
{
    private const string TrxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    private const string VstestDefinitionTruncationMarker = "\u00B7\u00B7\u00B7";

    internal const string BlameCompletionRunInfoText =
        "Data collector 'Blame' message: All tests finished running, Sequence file will not be generated.";

    private static readonly Regex s_xunitSkipRunInfo = new Regex(
        @"^\[xUnit\.net [0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]+\]\s+(?<testName>.+) \[SKIP\]$",
        RegexOptions.CultureInvariant);

    private static readonly string[] s_adverseCounterNames =
    {
        "error",
        "timeout",
        "aborted",
        "inconclusive",
        "passedButRunAborted",
        "notRunnable",
        "disconnected",
        "warning",
        "completed",
        "inProgress",
        "pending",
    };

    private readonly SuitePolicy _policy;

    public TrxResultValidator(SuitePolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public ValidationSummary Validate(string resultsPath)
    {
        XDocument document = XDocument.Load(resultsPath, LoadOptions.PreserveWhitespace);
        XElement root = document.Root
            ?? throw new InvalidDataException($"'{resultsPath}' has no XML root element.");

        List<string> errors = new List<string>();
        if (!string.Equals(root.Name.LocalName, "TestRun", StringComparison.Ordinal)
            || !string.Equals(root.Name.NamespaceName, TrxNamespace, StringComparison.Ordinal))
        {
            errors.Add(
                $"TRX root must be '{{{TrxNamespace}}}TestRun', but was '{root.Name}'.");
        }

        XNamespace ns = TrxNamespace;
        XElement[] resultElements = ReadContainerChildren(root, ns, "Results", "UnitTestResult", errors);
        XElement[] definitionElements = ReadContainerChildren(root, ns, "TestDefinitions", "UnitTest", errors);
        XElement[] entryElements = ReadContainerChildren(root, ns, "TestEntries", "TestEntry", errors);

        TestResult[] results = resultElements.Select(element => ReadResult(element, ns)).ToArray();
        ValidateStructuralIdentities(
            resultElements,
            definitionElements,
            entryElements,
            results,
            ns,
            errors);

        TestResult[] passed = results.Where(result => IsPassed(result.Outcome)).ToArray();
        TestResult[] skipped = results.Where(result => IsSkipped(result.Outcome)).ToArray();
        TestResult[] failed = results
            .Where(result => !IsPassed(result.Outcome) && !IsSkipped(result.Outcome))
            .ToArray();
        int expectedDiscovered = _policy.ExpectedDiscovered.Count;
        int expectedSkipped = checked((int)_policy.GetExpectedSkippedCount());

        string discoveredFingerprint = TestIdentityFingerprintComputer.Compute(
            results.Select(result => result.Name));
        if (results.Length != expectedDiscovered)
        {
            errors.Add(
                $"Discovered {results.Length} tests; exact identity policy requires "
                + $"{expectedDiscovered}.");
        }
        if (!string.Equals(
            discoveredFingerprint,
            _policy.ExpectedDiscovered.Sha256,
            StringComparison.Ordinal))
        {
            errors.Add(
                "Discovered-test identity fingerprint changed: "
                + $"expected {_policy.ExpectedDiscovered.Sha256}, observed {discoveredFingerprint}.");
        }

        if (failed.Length != 0)
        {
            errors.Add(
                $"TRX contains {failed.Length} non-passing result(s): "
                + string.Join(", ", failed.Select(result => $"{result.Name} [{result.Outcome}]")));
        }

        if (skipped.Length != expectedSkipped)
        {
            errors.Add(
                $"Observed {skipped.Length} skipped tests; policy requires exactly "
                + $"{expectedSkipped}.");
        }

        ValidateSkippedIdentities(skipped, errors);
        ValidateResultSummary(
            root,
            ns,
            results.Length,
            passed.Length,
            skipped,
            failed.Length,
            errors);

        return new ValidationSummary
        {
            Suite = _policy.Name,
            ResultsPath = Path.GetFullPath(resultsPath),
            Discovered = results.Length,
            Passed = passed.Length,
            Skipped = skipped.Length,
            Failed = failed.Length,
            ExpectedDiscovered = expectedDiscovered,
            ExpectedSkipped = expectedSkipped,
            ExpectedDiscoveredFingerprint = _policy.ExpectedDiscovered.Sha256,
            DiscoveredFingerprint = discoveredFingerprint,
            Errors = errors,
        };
    }

    private static XElement[] ReadContainerChildren(
        XElement root,
        XNamespace ns,
        string containerName,
        string childName,
        List<string> errors)
    {
        XElement[] containers = root.Elements(ns + containerName).ToArray();
        if (containers.Length != 1)
        {
            errors.Add(
                $"TRX must contain exactly one {containerName} element; observed {containers.Length}.");
        }

        return containers.SelectMany(container => container.Elements(ns + childName)).ToArray();
    }

    private static TestResult ReadResult(XElement element, XNamespace ns)
    {
        string[] errorMessages = element
            .Elements(ns + "Output")
            .Elements(ns + "ErrorInfo")
            .Elements(ns + "Message")
            .Select(message => message.Value)
            .ToArray();

        return new TestResult(
            element.Attribute("testName")?.Value ?? string.Empty,
            element.Attribute("outcome")?.Value ?? string.Empty,
            element.Attribute("executionId")?.Value ?? string.Empty,
            element.Attribute("testId")?.Value ?? string.Empty,
            errorMessages);
    }

    private static void ValidateStructuralIdentities(
        XElement[] resultElements,
        XElement[] definitionElements,
        XElement[] entryElements,
        TestResult[] results,
        XNamespace ns,
        List<string> errors)
    {
        if (definitionElements.Length != resultElements.Length)
        {
            errors.Add(
                $"TRX has {resultElements.Length} results but {definitionElements.Length} test definitions.");
        }
        if (entryElements.Length != resultElements.Length)
        {
            errors.Add(
                $"TRX has {resultElements.Length} results but {entryElements.Length} test entries.");
        }

        Dictionary<Guid, Guid> resultEdges = ReadExecutionEdges(
            resultElements,
            "result",
            errors);
        Dictionary<Guid, Guid> entryEdges = ReadExecutionEdges(
            entryElements,
            "test entry",
            errors);
        Dictionary<Guid, TestDefinition> definitions = ReadDefinitions(
            definitionElements,
            ns,
            errors);

        if (!resultEdges.Keys.ToHashSet().SetEquals(entryEdges.Keys))
        {
            errors.Add("Result execution IDs do not exactly match TestEntry execution IDs.");
        }
        if (!resultEdges.Values.ToHashSet().SetEquals(entryEdges.Values))
        {
            errors.Add("Result test IDs do not exactly match TestEntry test IDs.");
        }
        if (!resultEdges.Values.ToHashSet().SetEquals(definitions.Keys))
        {
            errors.Add("Result test IDs do not exactly match TestDefinitions IDs.");
        }
        if (!resultEdges.Keys.ToHashSet().SetEquals(
            definitions.Values.Select(definition => definition.ExecutionId)))
        {
            errors.Add("Result execution IDs do not exactly match TestDefinitions execution IDs.");
        }

        foreach ((Guid executionId, Guid testId) in resultEdges)
        {
            if (entryEdges.TryGetValue(executionId, out Guid entryTestId) && entryTestId != testId)
            {
                errors.Add(
                    $"Execution '{executionId}' maps to test '{testId}' in Results but "
                    + $"'{entryTestId}' in TestEntries.");
            }
            if (definitions.TryGetValue(testId, out TestDefinition definition)
                && definition.ExecutionId != executionId)
            {
                errors.Add(
                    $"Test definition '{testId}' maps to execution '{definition.ExecutionId}', "
                    + $"but its result uses '{executionId}'.");
            }
        }

        foreach (TestResult result in results)
        {
            if (Guid.TryParse(result.TestId, out Guid testId)
                && testId != Guid.Empty
                && definitions.TryGetValue(testId, out TestDefinition definition))
            {
                ValidateResultDefinitionIdentity(result, definition, errors);
            }
        }

        foreach (IGrouping<string, TestResult> duplicate in results
            .GroupBy(result => result.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            errors.Add(
                $"Duplicate test identity '{duplicate.Key}' appears {duplicate.Count()} times.");
        }
        foreach (TestResult result in results)
        {
            if (string.IsNullOrWhiteSpace(result.Name))
            {
                errors.Add("A UnitTestResult is missing testName.");
            }
            if (string.IsNullOrWhiteSpace(result.Outcome))
            {
                errors.Add($"UnitTestResult '{result.Name}' is missing outcome.");
            }
            if (result.Name.IndexOf('\0') >= 0)
            {
                errors.Add($"UnitTestResult '{result.Name}' contains an invalid identity separator.");
            }
        }
    }

    private static Dictionary<Guid, Guid> ReadExecutionEdges(
        IEnumerable<XElement> elements,
        string description,
        List<string> errors)
    {
        Dictionary<Guid, Guid> edges = new Dictionary<Guid, Guid>();
        HashSet<Guid> testIds = new HashSet<Guid>();
        foreach (XElement element in elements)
        {
            if (!TryReadGuid(element, "executionId", description + " execution ID", errors, out Guid executionId)
                || !TryReadGuid(element, "testId", description + " test ID", errors, out Guid testId))
            {
                continue;
            }

            if (!edges.TryAdd(executionId, testId))
            {
                errors.Add($"Duplicate {description} execution ID '{executionId}'.");
            }
            if (!testIds.Add(testId))
            {
                errors.Add($"Duplicate {description} test ID '{testId}'.");
            }
        }
        return edges;
    }

    private static Dictionary<Guid, TestDefinition> ReadDefinitions(
        IEnumerable<XElement> elements,
        XNamespace ns,
        List<string> errors)
    {
        Dictionary<Guid, TestDefinition> definitions = new Dictionary<Guid, TestDefinition>();
        HashSet<Guid> executionIds = new HashSet<Guid>();
        foreach (XElement element in elements)
        {
            if (!TryReadGuid(element, "id", "test definition ID", errors, out Guid testId))
            {
                continue;
            }

            XElement[] executions = element.Elements(ns + "Execution").ToArray();
            if (executions.Length != 1)
            {
                errors.Add(
                    $"Test definition '{testId}' must contain exactly one Execution element; "
                    + $"observed {executions.Length}.");
                continue;
            }
            if (!TryReadGuid(
                executions[0],
                "id",
                $"test definition '{testId}' execution ID",
                errors,
                out Guid executionId))
            {
                continue;
            }

            XElement[] testMethods = element.Elements(ns + "TestMethod").ToArray();
            if (testMethods.Length != 1)
            {
                errors.Add(
                    $"Test definition '{testId}' must contain exactly one TestMethod element; "
                    + $"observed {testMethods.Length}.");
                continue;
            }

            string definitionName = element.Attribute("name")?.Value ?? string.Empty;
            string className = testMethods[0].Attribute("className")?.Value ?? string.Empty;
            string methodName = testMethods[0].Attribute("name")?.Value ?? string.Empty;
            ValidateDefinitionText(testId, "name", definitionName, errors);
            ValidateDefinitionText(testId, "TestMethod className", className, errors);
            ValidateDefinitionText(testId, "TestMethod name", methodName, errors);

            if (!definitions.TryAdd(
                testId,
                new TestDefinition(executionId, definitionName, className, methodName)))
            {
                errors.Add($"Duplicate test definition ID '{testId}'.");
            }
            if (!executionIds.Add(executionId))
            {
                errors.Add($"Duplicate test definition execution ID '{executionId}'.");
            }
        }
        return definitions;
    }

    private static void ValidateDefinitionText(
        Guid testId,
        string description,
        string value,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"Test definition '{testId}' has an empty {description}.");
        }
        else if (value.IndexOf('\0') >= 0)
        {
            errors.Add($"Test definition '{testId}' {description} contains an invalid separator.");
        }
    }

    private static void ValidateResultDefinitionIdentity(
        TestResult result,
        TestDefinition definition,
        List<string> errors)
    {
        string methodIdentity = definition.ClassName + "." + definition.MethodName;
        if (!string.Equals(result.Name, methodIdentity, StringComparison.Ordinal)
            && !result.Name.StartsWith(methodIdentity + "(", StringComparison.Ordinal))
        {
            errors.Add(
                $"Result '{result.Name}' does not belong to TestMethod '{methodIdentity}'.");
        }

        // VSTest truncates long parameterized UnitTest names with three middle-dot
        // characters. The intact TestMethod identity remains authoritative there.
        bool definitionUsesMethodIdentity =
            string.Equals(definition.Name, methodIdentity, StringComparison.Ordinal);
        if (!definitionUsesMethodIdentity
            && !definition.Name.EndsWith(VstestDefinitionTruncationMarker, StringComparison.Ordinal)
            && !string.Equals(result.Name, definition.Name, StringComparison.Ordinal))
        {
            errors.Add(
                $"Result '{result.Name}' does not match test definition name '{definition.Name}'.");
        }
    }

    private static bool TryReadGuid(
        XElement element,
        string attributeName,
        string description,
        List<string> errors,
        out Guid value)
    {
        string text = element.Attribute(attributeName)?.Value;
        if (!Guid.TryParse(text, out value) || value == Guid.Empty)
        {
            errors.Add($"Invalid or missing {description} '{text}'.");
            return false;
        }
        return true;
    }

    private void ValidateSkippedIdentities(TestResult[] skipped, List<string> errors)
    {
        Dictionary<SkipRule, List<SkipIdentity>> observedByRule = _policy.SkipRules.ToDictionary(
            rule => rule,
            _ => new List<SkipIdentity>());

        foreach (TestResult skippedResult in skipped)
        {
            if (!TryReadStableReasonCode(skippedResult, out string reasonCode, out string reasonFailure))
            {
                errors.Add($"Skip '{skippedResult.Name}' has no valid stable reason: {reasonFailure}");
                continue;
            }

            SkipIdentity identity = new SkipIdentity(skippedResult.Name, reasonCode);
            SkipRule[] matches = _policy.SkipRules
                .Where(rule => rule.MatchesScope(identity))
                .ToArray();
            if (matches.Length == 0)
            {
                errors.Add(
                    $"Unapproved skip '{identity.TestName}' with reason '{identity.ReasonCode}'.");
                continue;
            }
            if (matches.Length > 1)
            {
                errors.Add(
                    $"Skip '{identity.TestName}' with reason '{identity.ReasonCode}' matches "
                    + $"{matches.Length} policy rules.");
                continue;
            }

            observedByRule[matches[0]].Add(identity);
        }

        foreach ((SkipRule rule, List<SkipIdentity> observed) in observedByRule)
        {
            if (rule.ExpectedFingerprint != null)
            {
                ValidateFingerprintRule(rule, observed, errors);
            }
            else
            {
                ValidateExplicitRule(rule, observed, errors);
            }
        }
    }

    private static bool TryReadStableReasonCode(
        TestResult result,
        out string reasonCode,
        out string failure)
    {
        reasonCode = null;
        if (result.ErrorMessages.Count != 1)
        {
            failure = $"expected exactly one Output/ErrorInfo/Message, observed "
                + $"{result.ErrorMessages.Count}.";
            return false;
        }

        string message = result.ErrorMessages[0];
        int separator = message.IndexOf(": ", StringComparison.Ordinal);
        if (separator <= 0 || separator + 2 >= message.Length)
        {
            failure = "skip message must begin with 'NV-SKIP-...: ' followed by an explanation.";
            return false;
        }

        reasonCode = message[..separator];
        if (!ValidationPolicyContract.IsStableReasonCode(reasonCode))
        {
            failure = $"'{reasonCode}' does not match NV-SKIP-[A-Z0-9-]+.";
            reasonCode = null;
            return false;
        }

        failure = null;
        return true;
    }

    private static void ValidateExplicitRule(
        SkipRule rule,
        IReadOnlyCollection<SkipIdentity> observed,
        List<string> errors)
    {
        Dictionary<string, int> expectedCounts = ToNameCounts(rule.ExpectedTestNames);
        Dictionary<string, int> observedCounts = ToNameCounts(observed.Select(identity => identity.TestName));
        string[] names = expectedCounts.Keys
            .Concat(observedCounts.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        foreach (string name in names)
        {
            int expected = expectedCounts.GetValueOrDefault(name);
            int actual = observedCounts.GetValueOrDefault(name);
            if (expected != actual)
            {
                errors.Add(
                    $"Skip identity '{name}' with reason '{rule.ReasonCode}' was observed "
                    + $"{actual} time(s); policy requires {expected}.");
            }
        }
    }

    private static void ValidateFingerprintRule(
        SkipRule rule,
        IReadOnlyCollection<SkipIdentity> observed,
        List<string> errors)
    {
        SkipIdentityFingerprint expected = rule.ExpectedFingerprint;
        if (observed.Count != expected.Count)
        {
            errors.Add(
                $"Skip fingerprint scope '{rule.TestMethod}' with reason '{rule.ReasonCode}' "
                + $"observed {observed.Count} identities; policy requires {expected.Count}.");
        }

        string actualHash = SkipIdentityFingerprintComputer.Compute(observed);
        if (!string.Equals(actualHash, expected.Sha256, StringComparison.Ordinal))
        {
            errors.Add(
                $"Skip identity fingerprint changed for '{rule.TestMethod}' with reason "
                + $"'{rule.ReasonCode}': expected {expected.Sha256}, observed {actualHash}.");
        }
    }

    private static Dictionary<string, int> ToNameCounts(IEnumerable<string> names)
    {
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        return counts;
    }

    private static void ValidateResultSummary(
        XElement root,
        XNamespace ns,
        int total,
        int passed,
        TestResult[] skipped,
        int failed,
        List<string> errors)
    {
        XElement[] summaries = root.Elements(ns + "ResultSummary").ToArray();
        if (summaries.Length != 1)
        {
            errors.Add(
                $"TRX must contain exactly one ResultSummary element; observed {summaries.Length}.");
            return;
        }

        XElement summary = summaries[0];
        string summaryOutcome = summary.Attribute("outcome")?.Value ?? string.Empty;
        if (!string.Equals(summaryOutcome, "Completed", StringComparison.Ordinal))
        {
            errors.Add(
                $"TRX ResultSummary outcome must be 'Completed'; observed '{summaryOutcome}'.");
        }

        ValidateRunInfos(summary, ns, skipped, errors);

        XElement[] countersElements = summary.Elements(ns + "Counters").ToArray();
        if (countersElements.Length != 1)
        {
            errors.Add(
                $"TRX ResultSummary must contain exactly one Counters element; observed "
                + $"{countersElements.Length}.");
            return;
        }

        XElement counters = countersElements[0];
        ValidateCounter(counters, "total", total, errors);
        ValidateCounter(counters, "executed", total - skipped.Length, errors);
        ValidateCounter(counters, "passed", passed, errors);
        ValidateCounter(counters, "failed", failed, errors);

        if (TryReadCounter(counters, "notExecuted", errors, out int notExecuted)
            && notExecuted != 0
            && notExecuted != skipped.Length)
        {
            errors.Add(
                $"TRX counter 'notExecuted' is {notExecuted}; expected either 0 for the current "
                + $"xUnit adapter or the skipped-result count {skipped.Length}.");
        }

        foreach (string counterName in s_adverseCounterNames)
        {
            ValidateCounter(counters, counterName, 0, errors);
        }
    }

    private static void ValidateRunInfos(
        XElement summary,
        XNamespace ns,
        IReadOnlyCollection<TestResult> skipped,
        List<string> errors)
    {
        XElement[] containers = summary.Elements(ns + "RunInfos").ToArray();
        if (containers.Length > 1)
        {
            errors.Add(
                $"TRX ResultSummary may contain at most one RunInfos element; observed "
                + $"{containers.Length}.");
        }

        Dictionary<string, int> skippedCounts = ToNameCounts(skipped.Select(result => result.Name));
        Dictionary<string, int> observedSkipAnnouncements = new Dictionary<string, int>(StringComparer.Ordinal);
        int blameCompletionMessages = 0;
        foreach (XElement container in containers)
        {
            XElement[] runInfos = container.Elements(ns + "RunInfo").ToArray();
            if (container.Elements().Count() != runInfos.Length)
            {
                errors.Add("TRX RunInfos contains an unexpected child element.");
            }

            foreach (XElement runInfo in runInfos)
            {
                string outcome = runInfo.Attribute("outcome")?.Value ?? string.Empty;
                XElement[] textElements = runInfo.Elements(ns + "Text").ToArray();
                if (textElements.Length != 1 || runInfo.Elements().Count() != 1)
                {
                    errors.Add(
                        "TRX RunInfo must contain exactly one Text element and no other child elements.");
                    continue;
                }

                string text = textElements[0].Value;
                if (string.Equals(outcome, "Warning", StringComparison.Ordinal)
                    && string.Equals(text, BlameCompletionRunInfoText, StringComparison.Ordinal))
                {
                    blameCompletionMessages++;
                    if (blameCompletionMessages > 1)
                    {
                        errors.Add("TRX repeats the successful Blame collector completion warning.");
                    }
                    continue;
                }

                Match skipAnnouncement = s_xunitSkipRunInfo.Match(text);
                if (string.Equals(outcome, "Warning", StringComparison.Ordinal)
                    && skipAnnouncement.Success)
                {
                    string testName = skipAnnouncement.Groups["testName"].Value;
                    int observedCount = observedSkipAnnouncements.GetValueOrDefault(testName) + 1;
                    observedSkipAnnouncements[testName] = observedCount;
                    int approvedCount = skippedCounts.GetValueOrDefault(testName);
                    if (observedCount > approvedCount)
                    {
                        errors.Add(
                            $"Run-level skip announcement '{testName}' was observed {observedCount} "
                            + $"time(s), but only {approvedCount} matching skipped result(s) exist.");
                    }
                    continue;
                }

                errors.Add(
                    $"TRX contains an unapproved run-level '{outcome}' message: '{text}'.");
            }
        }
    }

    private static void ValidateCounter(
        XElement counters,
        string name,
        int expected,
        List<string> errors)
    {
        if (TryReadCounter(counters, name, errors, out int actual) && actual != expected)
        {
            errors.Add($"TRX counter '{name}' is {actual}; expected {expected}.");
        }
    }

    private static bool TryReadCounter(
        XElement counters,
        string name,
        List<string> errors,
        out int value)
    {
        string text = counters.Attribute(name)?.Value;
        if (!int.TryParse(
            text,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value))
        {
            errors.Add($"TRX counter '{name}' is missing or is not a nonnegative integer: '{text}'.");
            return false;
        }
        return true;
    }

    private static bool IsPassed(string outcome) =>
        string.Equals(outcome, "Passed", StringComparison.Ordinal);

    private static bool IsSkipped(string outcome) =>
        string.Equals(outcome, "NotExecuted", StringComparison.Ordinal)
        || string.Equals(outcome, "Skipped", StringComparison.Ordinal);
}

internal readonly record struct TestResult(
    string Name,
    string Outcome,
    string ExecutionId,
    string TestId,
    IReadOnlyList<string> ErrorMessages);

internal readonly record struct TestDefinition(
    Guid ExecutionId,
    string Name,
    string ClassName,
    string MethodName);

internal readonly record struct SkipIdentity(string TestName, string ReasonCode);

internal static class TestIdentityFingerprintComputer
{
    public static string Compute(IEnumerable<string> testNames)
    {
        // sha256-trx-test-name-nul-v1: ordinal-sort the complete display-name
        // multiset, UTF-8 encode each name, and terminate every record with NUL.
        StringBuilder canonical = new StringBuilder();
        foreach (string testName in testNames.OrderBy(name => name, StringComparer.Ordinal))
        {
            if (testName.IndexOf('\0') >= 0)
            {
                throw new InvalidDataException("A test identity contains the fingerprint separator.");
            }
            canonical.Append(testName).Append('\0');
        }
        return ComputeSha256(canonical);
    }

    internal static string ComputeSha256(StringBuilder canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
}

internal static class SkipIdentityFingerprintComputer
{
    public static string Compute(IEnumerable<SkipIdentity> identities)
    {
        // sha256-trx-test-name-reason-nul-v1: ordinal-sort the logical identity
        // multiset, then encode testName NUL reasonCode NUL for every record.
        StringBuilder canonical = new StringBuilder();
        foreach (SkipIdentity identity in identities
            .OrderBy(candidate => candidate.TestName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ReasonCode, StringComparer.Ordinal))
        {
            if (identity.TestName.IndexOf('\0') >= 0 || identity.ReasonCode.IndexOf('\0') >= 0)
            {
                throw new InvalidDataException("A skip identity contains the fingerprint separator.");
            }
            canonical
                .Append(identity.TestName)
                .Append('\0')
                .Append(identity.ReasonCode)
                .Append('\0');
        }
        return TestIdentityFingerprintComputer.ComputeSha256(canonical);
    }
}
